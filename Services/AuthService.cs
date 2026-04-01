namespace EasyWorkTogether.Api.Services;

public static class AuthTokenHelper
{
    public static Guid? GetBearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var tokenText = header["Bearer ".Length..].Trim();
        return Guid.TryParse(tokenText, out var token) ? token : null;
    }
}


public sealed class AuthService
{
    public const string HttpContextUserKey = "CurrentUser";
    private readonly NpgsqlDataSource _db;

    public AuthService(NpgsqlDataSource db)
    {
        _db = db;
    }

    public async Task<SessionUser?> GetCurrentUserAsync(HttpContext http)
    {
        var token = AuthTokenHelper.GetBearerToken(http);
        if (token is null)
            return null;

        await using var conn = await _db.OpenConnectionAsync();

        const string sql = """
            SELECT u.id, u.email, u.name, u.created_at
            FROM sessions s
            JOIN users u ON u.id = s.user_id
            WHERE s.token = @token AND s.expires_at > NOW();
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("token", token.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new SessionUser(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetDateTime(3));
    }
}


public static class HttpContextExtensions
{
    public static SessionUser GetCurrentUser(this HttpContext http)
    {
        return (SessionUser)http.Items[AuthService.HttpContextUserKey]!;
    }
}

