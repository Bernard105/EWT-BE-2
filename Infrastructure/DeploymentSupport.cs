using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Npgsql;

namespace EasyWorkTogether.Api.Infrastructure;

public static class DeploymentSupport
{
    public static void ConfigureRenderPort(WebApplicationBuilder builder)
    {
        var portValue = builder.Configuration["PORT"] ?? Environment.GetEnvironmentVariable("PORT");
        if (int.TryParse(portValue, out var port) && port > 0)
        {
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        }
    }

    public static string ResolveConnectionString(IConfiguration configuration)
    {
        var databaseUrl = configuration["DATABASE_URL"] ?? Environment.GetEnvironmentVariable("DATABASE_URL");

        if (!string.IsNullOrWhiteSpace(databaseUrl))
            return ConvertDatabaseUrlToNpgsqlConnectionString(databaseUrl.Trim());

        var configuredConnectionString = configuration.GetConnectionString("DefaultConnection");

        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
            return configuredConnectionString.Trim();

        throw new InvalidOperationException(
            "Database configuration is missing. Set DATABASE_URL or ConnectionStrings__DefaultConnection.");
    }

    public static string[] ResolveCorsOrigins(IConfiguration configuration)
    {
        var csvOrigins = configuration["CORS_ALLOWED_ORIGINS"];

        if (!string.IsNullOrWhiteSpace(csvOrigins))
        {
            return csvOrigins
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsAbsoluteHttpUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var sectionOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();

        if (sectionOrigins is { Length: > 0 })
        {
            return sectionOrigins
                .Where(origin => !string.IsNullOrWhiteSpace(origin) && IsAbsoluteHttpUrl(origin))
                .Select(origin => origin.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private static bool IsAbsoluteHttpUrl(string? value)
    {
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string ConvertDatabaseUrlToNpgsqlConnectionString(string databaseUrl)
    {
        if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
        {
            return databaseUrl;
        }

        var userInfo = uri.UserInfo.Split(':', 2, StringSplitOptions.None);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = uri.AbsolutePath.Trim('/'),
            Username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : string.Empty,
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
            Pooling = true,

            // 🔥 FIX QUAN TRỌNG CHO RENDER
            SslMode = SslMode.Require,
            TrustServerCertificate = true
        };

        foreach (KeyValuePair<string, StringValues> pair in QueryHelpers.ParseQuery(uri.Query))
        {
            var rawValue = pair.Value.ToString();
            if (string.IsNullOrWhiteSpace(rawValue))
                continue;

            switch (NormalizeKey(pair.Key))
            {
                case "sslmode":
                    if (Enum.TryParse<SslMode>(rawValue, ignoreCase: true, out var sslMode))
                        builder.SslMode = sslMode;
                    else
                        builder["SSL Mode"] = rawValue;
                    break;

                case "trustservercertificate":
                    if (bool.TryParse(rawValue, out var trustServerCertificate))
                        builder.TrustServerCertificate = trustServerCertificate;
                    break;

                case "sslrootcert":
                    if (!string.Equals(rawValue, "system", StringComparison.OrdinalIgnoreCase))
                        TryAssign(builder, pair.Key, rawValue);
                    break;

                default:
                    TryAssign(builder, pair.Key, rawValue);
                    break;
            }
        }

        return builder.ConnectionString;
    }

    private static string NormalizeKey(string key)
    {
        return key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static void TryAssign(NpgsqlConnectionStringBuilder builder, string key, string value)
    {
        try
        {
            builder[key] = value;
        }
        catch (ArgumentException)
        {
            // Ignore unknown parameters
        }
    }
}