namespace EasyWorkTogether.Api.Models;

public static class OAuthProviders
{
    public const string Google = "google";
    public const string GitHub = "github";
}

public readonly record struct OAuthProviderSettings(string ClientId, string ClientSecret);
public readonly record struct EmailSettings(bool Enabled, string FromName, string FromAddress, string SmtpHost, int SmtpPort, string Username, string Password, bool UseSsl);
public readonly record struct OAuthProviderEndpoints(string AuthorizationEndpoint, string TokenEndpoint, string UserInfoEndpoint);
public readonly record struct OAuthTokenResult(string AccessToken);
public readonly record struct OAuthUserInfo(string ProviderUserId, string Email, string Name);

public sealed class GoogleTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }
}

public sealed class GoogleUserInfoResponse
{
    [JsonPropertyName("sub")]
    public string? Sub { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("email_verified")]
    public bool? EmailVerified { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class GitHubTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }
}

public sealed class GitHubUserResponse
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("login")]
    public string? Login { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class GitHubEmailResponse
{
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }

    [JsonPropertyName("primary")]
    public bool Primary { get; init; }
}

sealed class InviteSuggestionAggregate
{
    public int? UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
    public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
}


public record RegisterRequest(string Email, string Password, string Name);
public record LoginRequest(string Email, string Password);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string NewPassword);
public record UpdateProfileRequest(string? Name);
public record ChangePasswordRequest(string OldPassword, string NewPassword);
public record CreateWorkspaceRequest(string Name, string? DomainNamespace, string? IndustryVertical, string? WorkspaceLogoData);
public record InviteRequest(string Email, string? Role);
public record AcceptInvitationRequest(string Code);
public record CreateTaskRequest(string Title, string? Description, string? DueDate, string? DueAt, int? AssigneeId, int? StoryPoints, string? Priority, string? Status);
public record UpdateTaskRequest(string? Title, string? Description, string? DueDate, string? DueAt, int? AssigneeId, int? StoryPoints, string? Priority, string? Status);
public record VoteTaskStoryPointsRequest(int Points);

public record ErrorResponse(string Error);
public record MessageResponse(string Message);
public record UserResponse(int Id, string Email, string Name, string CreatedAt);
public record ProfileBackendStatusResponse(string ApiMessage, string HealthStatus, string State, string CheckedAt);
public record ProfileSummaryResponse(UserResponse User, string AvatarLabel, ProfileBackendStatusResponse Backend);
public record LoginUserResponse(int Id, string Email, string Name);
public record LoginResponse(LoginUserResponse User, Guid AccessToken, Guid SessionToken);
public record ImageUploadResponse(string Url, string OriginalFileName, string ContentType, long Size);
public record OAuthProviderAvailability(string Provider, bool Enabled, string StartUrl);
public record OAuthProvidersResponse(List<OAuthProviderAvailability> Providers);
public record ForgotPasswordResponse(string Message, string? ResetToken, string? ResetUrl, int ExpiresInMinutes);
public record RegisterResponse(string Message, bool VerificationEmailSent, string? VerificationToken, string? VerificationUrl, UserResponse User);
public record WorkspaceResponse(int Id, string Name, int OwnerId, string CreatedAt, string? UpdatedAt, string? DomainNamespace, string? IndustryVertical, string? WorkspaceLogoData);
public record WorkspaceUpdateResponse(int Id, string Name, int OwnerId, string UpdatedAt, string? DomainNamespace, string? IndustryVertical, string? WorkspaceLogoData);
public record WorkspaceListItem(int Id, string Name, string Role, string? DomainNamespace, string? IndustryVertical, string? WorkspaceLogoData);
public record WorkspaceListResponse(List<WorkspaceListItem> Workspaces);
public record WorkspaceMemberResponse(int Id, string Name, string Email, string Role, string JoinedAt);
public record WorkspaceMembersResponse(List<WorkspaceMemberResponse> Members);
public record WorkspaceInviteSuggestionResponse(int? UserId, string Email, string Name, int InteractionCount, string Reason);
public record WorkspaceInviteSuggestionListResponse(List<WorkspaceInviteSuggestionResponse> Suggestions);
public record WorkspaceInvitationListItem(int Id, string Code, string InviteeEmail, string? InviteeName, string Status, string Role, string ExpiresAt, string CreatedAt, string? RespondedAt, UserBasicResponse Inviter);
public record WorkspaceInvitationsResponse(List<WorkspaceInvitationListItem> Invitations);
public record PendingInvitationListItem(int Id, int WorkspaceId, string WorkspaceName, string Code, string InviteeEmail, string Role, string ExpiresAt, string CreatedAt, UserBasicResponse Inviter);
public record PendingInvitationsResponse(List<PendingInvitationListItem> Invitations);
public record InvitationResponse(int Id, string Code, string InviteeEmail, string Role, string ExpiresAt);
public record AcceptInvitationResponse(int WorkspaceId, string WorkspaceName, string Role);
public record UserBasicResponse(int Id, string Name);
public record TaskResponse(int Id, int WorkspaceId, string Sku, string Title, string? Description, string? DueDate, string? DueAt, int? StoryPoints, string Priority, string Status, int CreatedBy, UserBasicResponse? CreatedByUser, UserBasicResponse? Assignee, string CreatedAt, int StoryPointVoteCount, double? StoryPointVoteAverage, int? MyStoryPointVote);
public record TaskListItem(int Id, string Sku, string Title, string? Description, string? DueDate, string? DueAt, int? StoryPoints, string Priority, string Status, int CreatedBy, UserBasicResponse? CreatedByUser, UserBasicResponse? Assignee, string CreatedAt, int StoryPointVoteCount, double? StoryPointVoteAverage, int? MyStoryPointVote);
public record TaskListResponse(List<TaskListItem> Tasks, int? NextCursor, bool HasMore);
public record TaskStatsResponse(int Total, int Pending, int InProgress, int Completed, int Overdue);
public record SessionUser(int Id, string Email, string Name, DateTime CreatedAt);
public record TaskInfo(int Id, int WorkspaceId, string Sku, string Title, string? Description, DateTime? DueAt, int? StoryPoints, string Priority, string Status, int CreatedBy, int? AssigneeId);

public record UpdateWorkspaceMemberRoleRequest(string Role);
public record TransferWorkspaceOwnershipRequest(int NewOwnerUserId);
