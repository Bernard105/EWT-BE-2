using EasyWorkTogether.Api.Filters;
using EasyWorkTogether.Api.Services;

namespace EasyWorkTogether.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var publicApi = app.MapGroup("/api");
        publicApi.MapPost("/register", RegisterAsync);
        publicApi.MapPost("/login", LoginAsync);
        publicApi.MapPost("/forgot-password", ForgotPasswordAsync);
        publicApi.MapPost("/reset-password", ResetPasswordAsync);
        publicApi.MapGet("/verify-email", VerifyEmailAsync);
        publicApi.MapGet("/oauth/providers", GetOAuthProvidersAsync);
        publicApi.MapGet("/oauth/{provider}/start", StartOAuthAsync);
        publicApi.MapGet("/oauth/{provider}/callback", CompleteOAuthAsync);

        var authApi = app.MapGroup("/api");
        authApi.AddEndpointFilter<RequireSessionFilter>();
        authApi.MapPost("/logout", LogoutAsync);
        authApi.MapGet("/profile", GetProfileAsync);
        authApi.MapGet("/profile/summary", GetProfileSummaryAsync);
        authApi.MapPut("/profile", UpdateProfileAsync);
        authApi.MapPost("/change-password", ChangePasswordAsync);
        // Temporarily disabled until UploadImageAsync is implemented
        // authApi.MapPost("/uploads/images", UploadImageAsync);
    }

private static async Task<IResult> RegisterAsync(
    HttpContext http,
    RegisterRequest request,
    NpgsqlDataSource db,
    PasswordService passwordService,
    EmailService emailService,
    IConfiguration config)
{
    var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
    var name = NormalizeHumanName(request.Name ?? string.Empty);

    if (string.IsNullOrWhiteSpace(email))
        return Results.BadRequest(new ErrorResponse("Email is required"));

    var nameError = ValidateHumanName(name);
    if (nameError is not null)
        return Results.BadRequest(new ErrorResponse(nameError));

    var passwordError = ValidateStrongPassword(request.Password ?? string.Empty);
    if (passwordError is not null)
        return Results.BadRequest(new ErrorResponse(passwordError));

    var passwordHash = passwordService.HashPassword(request.Password ?? string.Empty);
    var verificationToken = CreateUrlSafeToken(32);
    var verificationTokenHash = ComputeSha256(verificationToken);
    var verificationExpiresAt = DateTime.UtcNow.AddHours(24);

    await using var conn = await db.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    const string sql = """
        INSERT INTO users (email, password_hash, name)
        VALUES (@email, @password_hash, @name)
        RETURNING id, email, name, created_at;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn, tx);
    cmd.Parameters.AddWithValue("email", email);
    cmd.Parameters.AddWithValue("password_hash", passwordHash);
    cmd.Parameters.AddWithValue("name", name);

    try
    {
        UserResponse response;

        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            await reader.ReadAsync();

            response = new UserResponse(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                ToIsoString(reader.GetDateTime(3)));
        }

        const string verificationSql = """
            INSERT INTO email_verification_tokens (user_id, token_hash, expires_at)
            VALUES (@user_id, @token_hash, @expires_at);
            """;

        await using (var verificationCmd = new NpgsqlCommand(verificationSql, conn, tx))
        {
            verificationCmd.Parameters.AddWithValue("user_id", response.Id);
            verificationCmd.Parameters.AddWithValue("token_hash", verificationTokenHash);
            verificationCmd.Parameters.AddWithValue("expires_at", verificationExpiresAt);
            await verificationCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();

        var verificationUrl = BuildVerifyEmailUrl(http, verificationToken);
        var emailSent = await emailService.TrySendEmailVerificationAsync(email, name, verificationUrl, verificationExpiresAt);

        return Results.Created($"/api/users/{response.Id}", new RegisterResponse(
            emailSent
                ? "Account created. Please check your email to verify your account before logging in."
                : "Account created. SMTP is not configured or email sending failed, so the verification link is returned directly.",
            emailSent,
            emailSent ? null : verificationToken,
            emailSent ? null : verificationUrl,
            response));
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(new ErrorResponse("Email already exists"));
    }
}

private static async Task<IResult> LoginAsync(LoginRequest request, NpgsqlDataSource db, PasswordService passwordService)
{
    var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
    var password = request.Password ?? string.Empty;

    await using var conn = await db.OpenConnectionAsync();

    const string userSql = """
        SELECT id, email, name, password_hash, email_verified_at
        FROM users
        WHERE email = @email;
        """;

    await using var userCmd = new NpgsqlCommand(userSql, conn);
    userCmd.Parameters.AddWithValue("email", email);

    await using var reader = await userCmd.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
        return Results.BadRequest(new ErrorResponse("Invalid email or password"));

    var userId = reader.GetInt32(0);
    var userEmail = reader.GetString(1);
    var userName = reader.GetString(2);
    var passwordHash = reader.GetString(3);
    var emailVerifiedAt = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4);

    if (!passwordService.VerifyPassword(password, passwordHash))
        return Results.BadRequest(new ErrorResponse("Invalid email or password"));

    if (emailVerifiedAt is null)
        return Results.Json(new ErrorResponse("Please verify your email before logging in."), statusCode: StatusCodes.Status403Forbidden);

    await reader.CloseAsync();

    var sessionToken = Guid.NewGuid();
    var expiresAt = GetPersistentSessionExpiryUtc();

    const string sessionSql = """
        INSERT INTO sessions (token, user_id, expires_at)
        VALUES (@token, @user_id, @expires_at);
        """;

    await using var sessionCmd = new NpgsqlCommand(sessionSql, conn);
    sessionCmd.Parameters.AddWithValue("token", sessionToken);
    sessionCmd.Parameters.AddWithValue("user_id", userId);
    sessionCmd.Parameters.AddWithValue("expires_at", expiresAt);
    await sessionCmd.ExecuteNonQueryAsync();

    return Results.Ok(new LoginResponse(
        new LoginUserResponse(userId, userEmail, userName),
        sessionToken,
        sessionToken));
}

private static async Task<IResult> ForgotPasswordAsync(HttpContext http, ForgotPasswordRequest request, NpgsqlDataSource db, EmailService emailService, IConfiguration config)
{
    var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();

    if (string.IsNullOrWhiteSpace(email))
        return Results.BadRequest(new ErrorResponse("Email is required"));

    await using var conn = await db.OpenConnectionAsync();

    const string userSql = """
        SELECT id
        FROM users
        WHERE email = @email;
        """;

    await using var userCmd = new NpgsqlCommand(userSql, conn);
    userCmd.Parameters.AddWithValue("email", email);

    var userIdValue = await userCmd.ExecuteScalarAsync();
    if (userIdValue is null)
    {
        return Results.Ok(new ForgotPasswordResponse(
            "If the account exists, a reset link has been prepared.",
            null,
            null,
            60));
    }

    var userId = (int)userIdValue;
    var resetToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    var tokenHash = ComputeSha256(resetToken);
    var expiresAt = DateTime.UtcNow.AddMinutes(60);

    await using var tx = await conn.BeginTransactionAsync();

    const string invalidateSql = """
        UPDATE password_reset_tokens
        SET used_at = NOW()
        WHERE user_id = @user_id AND used_at IS NULL;
        """;

    await using (var invalidateCmd = new NpgsqlCommand(invalidateSql, conn, tx))
    {
        invalidateCmd.Parameters.AddWithValue("user_id", userId);
        await invalidateCmd.ExecuteNonQueryAsync();
    }

    const string insertSql = """
        INSERT INTO password_reset_tokens (user_id, token, token_hash, expires_at)
        VALUES (@user_id, @token, @token_hash, @expires_at);
        """;

    await using (var insertCmd = new NpgsqlCommand(insertSql, conn, tx))
    {
        insertCmd.Parameters.AddWithValue("user_id", userId);
        insertCmd.Parameters.AddWithValue("token", resetToken);
        insertCmd.Parameters.AddWithValue("token_hash", tokenHash);
        insertCmd.Parameters.AddWithValue("expires_at", expiresAt);
        await insertCmd.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();

    var resetUrl = BuildResetPasswordUrl(http, config, resetToken);
    var emailSent = await emailService.TrySendResetPasswordEmailAsync(email, resetUrl, expiresAt);

    return emailSent
        ? Results.Ok(new ForgotPasswordResponse(
            "If the account exists, a password reset email has been sent.",
            null,
            null,
            60))
        : Results.Ok(new ForgotPasswordResponse(
            "Reset link created. Gmail SMTP is not configured or email sending failed, so the link is returned directly.",
            resetToken,
            resetUrl,
            60));
}

private static async Task<IResult> ResetPasswordAsync(ResetPasswordRequest request, NpgsqlDataSource db, PasswordService passwordService)
{
    var token = (request.Token ?? string.Empty).Trim();

    if (string.IsNullOrWhiteSpace(token))
        return Results.BadRequest(new ErrorResponse("Reset token is required"));

    var passwordError = ValidateStrongPassword(request.NewPassword ?? string.Empty, "New password");
    if (passwordError is not null)
        return Results.BadRequest(new ErrorResponse(passwordError));

    var tokenHash = ComputeSha256(token);

    await using var conn = await db.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    const string tokenSql = """
        SELECT id, user_id
        FROM password_reset_tokens
        WHERE (token_hash = @token_hash OR token = @raw_token)
          AND used_at IS NULL
          AND expires_at > NOW()
        FOR UPDATE;
        """;

    await using var tokenCmd = new NpgsqlCommand(tokenSql, conn, tx);
    tokenCmd.Parameters.AddWithValue("token_hash", tokenHash);
    tokenCmd.Parameters.AddWithValue("raw_token", token);

    int resetTokenId;
    int userId;

    await using (var reader = await tokenCmd.ExecuteReaderAsync())
    {
        if (!await reader.ReadAsync())
            return Results.BadRequest(new ErrorResponse("Reset token is invalid or expired"));

        resetTokenId = reader.GetInt32(0);
        userId = reader.GetInt32(1);
    }

    const string updateUserSql = """
        UPDATE users
        SET password_hash = @password_hash, updated_at = NOW()
        WHERE id = @id;
        """;

    await using (var updateUserCmd = new NpgsqlCommand(updateUserSql, conn, tx))
    {
        updateUserCmd.Parameters.AddWithValue("password_hash", passwordService.HashPassword(request.NewPassword ?? string.Empty));
        updateUserCmd.Parameters.AddWithValue("id", userId);
        await updateUserCmd.ExecuteNonQueryAsync();
    }

    const string useTokenSql = """
        UPDATE password_reset_tokens
        SET used_at = NOW()
        WHERE id = @id;
        """;

    await using (var useTokenCmd = new NpgsqlCommand(useTokenSql, conn, tx))
    {
        useTokenCmd.Parameters.AddWithValue("id", resetTokenId);
        await useTokenCmd.ExecuteNonQueryAsync();
    }

    const string invalidateOtherSql = """
        UPDATE password_reset_tokens
        SET used_at = NOW()
        WHERE user_id = @user_id AND used_at IS NULL;
        """;

    await using (var invalidateOtherCmd = new NpgsqlCommand(invalidateOtherSql, conn, tx))
    {
        invalidateOtherCmd.Parameters.AddWithValue("user_id", userId);
        await invalidateOtherCmd.ExecuteNonQueryAsync();
    }

    const string deleteSessionsSql = """
        DELETE FROM sessions
        WHERE user_id = @user_id;
        """;

    await using (var deleteSessionsCmd = new NpgsqlCommand(deleteSessionsSql, conn, tx))
    {
        deleteSessionsCmd.Parameters.AddWithValue("user_id", userId);
        await deleteSessionsCmd.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();

    return Results.Ok(new MessageResponse("Password reset successfully"));
}


private static async Task<IResult> VerifyEmailAsync(HttpContext http, string token, NpgsqlDataSource db, IConfiguration config)
{
    token = token.Trim();

    if (string.IsNullOrWhiteSpace(token))
        return Results.Redirect(QueryHelpers.AddQueryString(BuildLoginUrl(http, config), "verify_error", "Verification token is missing."));

    var tokenHash = ComputeSha256(token);

    await using var conn = await db.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    const string tokenSql = """
        SELECT id, user_id
        FROM email_verification_tokens
        WHERE token_hash = @token_hash AND used_at IS NULL AND expires_at > NOW()
        FOR UPDATE;
        """;

    await using var tokenCmd = new NpgsqlCommand(tokenSql, conn, tx);
    tokenCmd.Parameters.AddWithValue("token_hash", tokenHash);

    int verificationTokenId;
    int userId;

    await using (var reader = await tokenCmd.ExecuteReaderAsync())
    {
        if (!await reader.ReadAsync())
            return Results.Redirect(QueryHelpers.AddQueryString(BuildLoginUrl(http, config), "verify_error", "Verification link is invalid or expired."));

        verificationTokenId = reader.GetInt32(0);
        userId = reader.GetInt32(1);
    }

    const string verifyUserSql = """
        UPDATE users
        SET email_verified_at = COALESCE(email_verified_at, NOW()), updated_at = NOW()
        WHERE id = @id;
        """;

    await using (var verifyUserCmd = new NpgsqlCommand(verifyUserSql, conn, tx))
    {
        verifyUserCmd.Parameters.AddWithValue("id", userId);
        await verifyUserCmd.ExecuteNonQueryAsync();
    }

    const string useTokenSql = """
        UPDATE email_verification_tokens
        SET used_at = NOW()
        WHERE id = @id;
        """;

    await using (var useTokenCmd = new NpgsqlCommand(useTokenSql, conn, tx))
    {
        useTokenCmd.Parameters.AddWithValue("id", verificationTokenId);
        await useTokenCmd.ExecuteNonQueryAsync();
    }

    const string invalidateOtherSql = """
        UPDATE email_verification_tokens
        SET used_at = NOW()
        WHERE user_id = @user_id AND used_at IS NULL;
        """;

    await using (var invalidateOtherCmd = new NpgsqlCommand(invalidateOtherSql, conn, tx))
    {
        invalidateOtherCmd.Parameters.AddWithValue("user_id", userId);
        await invalidateOtherCmd.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();

    return Results.Redirect(QueryHelpers.AddQueryString(BuildLoginUrl(http, config), "verified", "1"));
}


private static IResult GetOAuthProvidersAsync(HttpContext http, IConfiguration config)
{
    var requestOrigin = $"{http.Request.Scheme}://{http.Request.Host}";
    var providers = new List<OAuthProviderAvailability>();

    foreach (var provider in new[] { OAuthProviders.Google, OAuthProviders.GitHub })
    {
        var settings = GetOAuthProviderSettings(config, provider);
        providers.Add(new OAuthProviderAvailability(
            provider,
            IsOAuthProviderConfigured(settings),
            $"{GetApiBaseUrl(http)}/api/oauth/{provider}/start?frontend_origin={Uri.EscapeDataString(requestOrigin)}"));
    }

    return Results.Ok(new OAuthProvidersResponse(providers));
}

private static IResult StartOAuthAsync(string provider, HttpContext http, IConfiguration config)
{
    provider = NormalizeOAuthProvider(provider);

    if (!TryGetOAuthEndpoints(provider, out _))
        return Results.NotFound(new ErrorResponse("OAuth provider is not supported"));

    var settings = GetOAuthProviderSettings(config, provider);
    if (!IsOAuthProviderConfigured(settings))
        return Results.BadRequest(new ErrorResponse($"{provider} OAuth is not configured"));

    var frontendOrigin = SanitizeFrontendOrigin(http.Request.Query["frontend_origin"].ToString(), http);
    var returnPath = SanitizeFrontendPath(http.Request.Query["return_path"].ToString());

    var state = CreateUrlSafeToken(32);
    var cookiePrefix = $"ewt_oauth_{provider}_";

    var cookieOptions = new CookieOptions
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = http.Request.IsHttps,
        Expires = DateTimeOffset.UtcNow.AddMinutes(10)
    };

    http.Response.Cookies.Append($"{cookiePrefix}state", state, cookieOptions);
    http.Response.Cookies.Append($"{cookiePrefix}frontend", frontendOrigin, cookieOptions);
    http.Response.Cookies.Append($"{cookiePrefix}return_path", returnPath, cookieOptions);

    var redirectUri = BuildOAuthRedirectUri(http, provider);
    var authorizationUrl = provider switch
    {
        OAuthProviders.Google => BuildGoogleAuthorizationUrl(settings, redirectUri, state),
        OAuthProviders.GitHub => BuildGitHubAuthorizationUrl(settings, redirectUri, state),
        _ => throw new InvalidOperationException("Unsupported OAuth provider")
    };

    return Results.Redirect(authorizationUrl);
}

private static async Task<IResult> CompleteOAuthAsync(
    string provider,
    HttpContext http,
    IConfiguration config,
    IHttpClientFactory httpClientFactory,
    NpgsqlDataSource db,
    PasswordService passwordService,
    ILoggerFactory loggerFactory)
{
    provider = NormalizeOAuthProvider(provider);
    var logger = loggerFactory.CreateLogger("OAuthEndpoints");

    if (!TryGetOAuthEndpoints(provider, out _))
        return Results.NotFound(new ErrorResponse("OAuth provider is not supported"));

    var settings = GetOAuthProviderSettings(config, provider);
    if (!IsOAuthProviderConfigured(settings))
        return Results.BadRequest(new ErrorResponse($"{provider} OAuth is not configured"));

    var cookiePrefix = $"ewt_oauth_{provider}_";
    var expectedState = http.Request.Cookies[$"{cookiePrefix}state"];
    var frontendOrigin = SanitizeFrontendOrigin(http.Request.Cookies[$"{cookiePrefix}frontend"], http);
    var returnPath = SanitizeFrontendPath(http.Request.Cookies[$"{cookiePrefix}return_path"]);

    ClearOAuthCookies(http, provider);

    if (!string.IsNullOrWhiteSpace(http.Request.Query["error"]))
    {
        var providerError = http.Request.Query["error_description"].ToString();
        if (string.IsNullOrWhiteSpace(providerError))
            providerError = http.Request.Query["error"].ToString();

        return Results.Redirect(BuildFrontendOAuthErrorUrl(frontendOrigin, returnPath, providerError));
    }

    var state = http.Request.Query["state"].ToString();
    if (string.IsNullOrWhiteSpace(expectedState) || !CryptographicEquals(expectedState, state))
        return Results.Redirect(BuildFrontendOAuthErrorUrl(frontendOrigin, returnPath, "OAuth state is invalid or expired. Please try again."));

    var code = http.Request.Query["code"].ToString();
    if (string.IsNullOrWhiteSpace(code))
        return Results.Redirect(BuildFrontendOAuthErrorUrl(frontendOrigin, returnPath, "Authorization code is missing."));

    var redirectUri = BuildOAuthRedirectUri(http, provider);
    var client = httpClientFactory.CreateClient();

    OAuthTokenResult tokenResult;
    try
    {
        tokenResult = provider switch
        {
            OAuthProviders.Google => await ExchangeGoogleCodeAsync(client, settings, code, redirectUri),
            OAuthProviders.GitHub => await ExchangeGitHubCodeAsync(client, settings, code, redirectUri),
            _ => throw new InvalidOperationException("Unsupported OAuth provider")
        };
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "OAuth token exchange failed for provider {Provider}", provider);
        return Results.Redirect(BuildFrontendOAuthErrorUrl(frontendOrigin, returnPath, ex.Message));
    }

    OAuthUserInfo userInfo;
    try
    {
        userInfo = provider switch
        {
            OAuthProviders.Google => await GetGoogleUserInfoAsync(client, tokenResult.AccessToken),
            OAuthProviders.GitHub => await GetGitHubUserInfoAsync(client, tokenResult.AccessToken),
            _ => throw new InvalidOperationException("Unsupported OAuth provider")
        };
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "OAuth user info request failed for provider {Provider}", provider);
        return Results.Redirect(BuildFrontendOAuthErrorUrl(frontendOrigin, returnPath, ex.Message));
    }

    if (string.IsNullOrWhiteSpace(userInfo.Email))
        return Results.Redirect(BuildFrontendOAuthErrorUrl(frontendOrigin, returnPath, "The provider did not return a usable email address."));

    try
    {
        var loginResponse = await LoginOrProvisionOAuthUserAsync(db, passwordService, provider, userInfo);
        return Results.Redirect(BuildFrontendOAuthSuccessUrl(frontendOrigin, returnPath, loginResponse));
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "OAuth login or provisioning failed for provider {Provider}", provider);
        return Results.Redirect(BuildFrontendOAuthErrorUrl(frontendOrigin, returnPath, ex.Message));
    }
}

private static async Task<IResult> LogoutAsync(HttpContext http, NpgsqlDataSource db)
{
    var token = AuthTokenHelper.GetBearerToken(http);
    if (token is null)
        return Results.Unauthorized();

    await using var conn = await db.OpenConnectionAsync();

    const string sql = """
        DELETE FROM sessions
        WHERE token = @token;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("token", token.Value);
    await cmd.ExecuteNonQueryAsync();

    return Results.NoContent();
}



private static IResult GetProfileAsync(HttpContext http)
{
    var user = http.GetCurrentUser();

    return Results.Ok(new UserResponse(
        user.Id,
        user.Email,
        user.Name,
        ToIsoString(user.CreatedAt)));
}

private static IResult GetProfileSummaryAsync(HttpContext http)
{
    var user = http.GetCurrentUser();
    var profile = new UserResponse(
        user.Id,
        user.Email,
        user.Name,
        ToIsoString(user.CreatedAt));

    return Results.Ok(new ProfileSummaryResponse(
        profile,
        BuildAvatarLabel(user.Name, user.Email),
        new ProfileBackendStatusResponse(
            "Task backend is running",
            "ok",
            "Connected",
            ToIsoString(DateTime.UtcNow))));
}

private static async Task<IResult> UpdateProfileAsync(HttpContext http, UpdateProfileRequest request, NpgsqlDataSource db)
{
    var currentUser = http.GetCurrentUser();
    var newName = NormalizeHumanName(request.Name);

    var nameError = ValidateHumanName(newName);
    if (nameError is not null)
        return Results.BadRequest(new ErrorResponse(nameError));

    await using var conn = await db.OpenConnectionAsync();

    const string sql = """
        UPDATE users
        SET name = @name, updated_at = NOW()
        WHERE id = @id
        RETURNING id, email, name, created_at;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("name", newName);
    cmd.Parameters.AddWithValue("id", currentUser.Id);

    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();

    var response = new UserResponse(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        ToIsoString(reader.GetDateTime(3)));

    return Results.Ok(response);
}

private static async Task<IResult> ChangePasswordAsync(HttpContext http, ChangePasswordRequest request, NpgsqlDataSource db, PasswordService passwordService)
{
    var passwordError = ValidateStrongPassword(request.NewPassword ?? string.Empty, "New password");
    if (passwordError is not null)
        return Results.BadRequest(new ErrorResponse(passwordError));

    var currentUser = http.GetCurrentUser();

    await using var conn = await db.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    const string getSql = """
        SELECT password_hash
        FROM users
        WHERE id = @id;
        """;

    await using var getCmd = new NpgsqlCommand(getSql, conn, tx);
    getCmd.Parameters.AddWithValue("id", currentUser.Id);

    var currentHash = (string?)await getCmd.ExecuteScalarAsync();
    if (currentHash is null || !passwordService.VerifyPassword(request.OldPassword ?? string.Empty, currentHash))
        return Results.BadRequest(new ErrorResponse("Old password is incorrect"));

    const string updateSql = """
        UPDATE users
        SET password_hash = @password_hash, updated_at = NOW()
        WHERE id = @id;
        """;

    await using var updateCmd = new NpgsqlCommand(updateSql, conn, tx);
    updateCmd.Parameters.AddWithValue("password_hash", passwordService.HashPassword(request.NewPassword ?? string.Empty));
    updateCmd.Parameters.AddWithValue("id", currentUser.Id);
    await updateCmd.ExecuteNonQueryAsync();

    const string deleteSessionsSql = """
        DELETE FROM sessions
        WHERE user_id = @user_id;
        """;

    await using var deleteCmd = new NpgsqlCommand(deleteSessionsSql, conn, tx);
    deleteCmd.Parameters.AddWithValue("user_id", currentUser.Id);
    await deleteCmd.ExecuteNonQueryAsync();

    await tx.CommitAsync();

    return Results.Ok(new MessageResponse("Password changed successfully"));
}



}
