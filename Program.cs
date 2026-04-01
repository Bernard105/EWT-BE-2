using System.Text.Json;
using EasyWorkTogether.Api.Endpoints;
using EasyWorkTogether.Api.Filters;
using EasyWorkTogether.Api.Infrastructure;
using EasyWorkTogether.Api.Middleware;
using EasyWorkTogether.Api.Services;
using static EasyWorkTogether.Api.Infrastructure.DeploymentSupport;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi.Models;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

ConfigureRenderPort(builder);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "EasyWorkTogether API",
        Version = "v1",
        Description = "Backend API for EasyWorkTogether. Import /swagger/v1/swagger.json into Postman to create a collection automatically."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Paste the session token returned by /api/login as: Bearer {token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var connectionString = ResolveConnectionString(builder.Configuration);

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton<RequireSessionFilter>();
builder.Services.AddHttpClient();
builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendDev", policy =>
    {
        var allowedOrigins = ResolveCorsOrigins(builder.Configuration);

        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
            return;
        }

        var frontendBaseUrl = builder.Configuration["FRONTEND_BASE_URL"] ?? builder.Configuration["Frontend:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(frontendBaseUrl) && Uri.TryCreate(frontendBaseUrl, UriKind.Absolute, out var frontendUri))
        {
            policy.WithOrigins(frontendUri.GetLeftPart(UriPartial.Authority)).AllowAnyHeader().AllowAnyMethod();
            return;
        }

        policy.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseForwardedHeaders();

app.UseSwagger(options =>
{
    options.PreSerializeFilters.Add((swagger, httpRequest) =>
    {
        var serverUrl = $"{httpRequest.Scheme}://{httpRequest.Host.Value}{httpRequest.PathBase.Value}".TrimEnd('/');
        swagger.Servers = new List<OpenApiServer>
        {
            new() { Url = string.IsNullOrWhiteSpace(serverUrl) ? "/" : serverUrl }
        };
    });
});

app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "EasyWorkTogether API v1");
    options.RoutePrefix = "swagger";
});

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors("FrontendDev");
app.UseDefaultFiles();
app.UseStaticFiles();


// 🔥 FIX QUAN TRỌNG CHO RENDER (đợi DB sẵn sàng)
await Task.Delay(5000);

await DbInitializer.InitializeAsync(app.Services);


app.MapGet("/api/status", () => Results.Ok(new { Message = "Task backend is running" }))
    .WithName("GetApiStatus")
    .WithTags("System");

app.MapGet("/health", () => Results.Ok(new { Status = "ok" }))
    .WithName("GetHealth")
    .WithTags("System");

app.MapAuthEndpoints();
app.MapWorkspaceEndpoints();
app.MapTaskEndpoints();

if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html")))
{
    app.MapFallbackToFile("index.html");
}

app.Run();