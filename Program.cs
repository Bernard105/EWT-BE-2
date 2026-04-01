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

// Render dynamic port
ConfigureRenderPort(builder);

// Forward headers for Render proxy
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;

    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// JSON format
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
        Description = "Backend API for EasyWorkTogether"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Paste token like: Bearer {token}",
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

// Database
var connectionString = ResolveConnectionString(builder.Configuration);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

// Services
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton<RequireSessionFilter>();

builder.Services.AddHttpClient();


// ✅ CORS FIX FOR VERCEL
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy
            .WithOrigins(
                "https://ewt-fe-aa9j.vercel.app",
                "http://localhost:5173"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

app.UseForwardedHeaders();


// Swagger
app.UseSwagger(options =>
{
    options.PreSerializeFilters.Add((swagger, httpRequest) =>
    {
        var serverUrl = $"{httpRequest.Scheme}://{httpRequest.Host.Value}{httpRequest.PathBase.Value}".TrimEnd('/');

        swagger.Servers = new List<OpenApiServer>
        {
            new() { Url = serverUrl }
        };
    });
});

app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "EasyWorkTogether API v1");
    options.RoutePrefix = "swagger";
});


// Middleware
app.UseMiddleware<ExceptionHandlingMiddleware>();

// ✅ enable cors
app.UseCors("AllowFrontend");

app.UseDefaultFiles();
app.UseStaticFiles();


// wait for Render DB startup
await Task.Delay(5000);

// DB init
await DbInitializer.InitializeAsync(app.Services);


// Health check
app.MapGet("/api/status", () =>
    Results.Ok(new { message = "Task backend is running" }))
    .WithName("GetApiStatus")
    .WithTags("System");

app.MapGet("/health", () =>
    Results.Ok(new { status = "ok" }))
    .WithName("GetHealth")
    .WithTags("System");


// API endpoints
app.MapAuthEndpoints();
app.MapWorkspaceEndpoints();
app.MapTaskEndpoints();


// fallback if frontend hosted inside backend
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html")))
{
    app.MapFallbackToFile("index.html");
}

app.Run();