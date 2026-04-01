using EasyWorkTogether.Api.Filters;
using EasyWorkTogether.Api.Services;

namespace EasyWorkTogether.Api.Endpoints;

public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var authApi = app.MapGroup("/api");
        authApi.AddEndpointFilter<RequireSessionFilter>();

        authApi.MapPost("/workspaces/{workspaceId:int}/tasks", CreateTaskAsync);
        authApi.MapGet("/workspaces/{workspaceId:int}/tasks", ListTasksAsync);
        authApi.MapGet("/tasks/{id:int}", GetTaskByIdAsync);
        authApi.MapPut("/tasks/{id:int}", UpdateTaskAsync);
        authApi.MapPost("/tasks/{id:int}/story-point-votes", VoteTaskStoryPointsAsync);
        authApi.MapDelete("/tasks/{id:int}", DeleteTaskAsync);
        authApi.MapGet("/workspaces/{workspaceId:int}/stats", WorkspaceStatsAsync);
        authApi.MapGet("/workspaces/{workspaceId:int}/my-stats", MyStatsAsync);
    }

    private static async Task<IResult> GetTaskByIdAsync(HttpContext http, int id, NpgsqlDataSource db)
    {
        var currentUser = http.GetCurrentUser();
        var taskInfo = await GetTaskInfoAsync(db, id);
        if (taskInfo is null)
            return Results.NotFound(new ErrorResponse("Task not found"));

        var role = await GetWorkspaceRoleAsync(db, taskInfo.WorkspaceId, currentUser.Id);
        if (role is null)
            return Results.NotFound(new ErrorResponse("Workspace not found"));

        var response = await GetTaskResponseAsync(db, id, currentUser.Id);
        return response is null ? Results.NotFound(new ErrorResponse("Task not found")) : Results.Ok(response);
    }

private static async Task<IResult> CreateTaskAsync(HttpContext http, int workspaceId, CreateTaskRequest request, NpgsqlDataSource db)
{
    var currentUser = http.GetCurrentUser();
    var role = await GetWorkspaceRoleAsync(db, workspaceId, currentUser.Id);

    if (role is null)
        return Results.NotFound(new ErrorResponse("Workspace not found"));

    var title = request.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new ErrorResponse("Title is required"));

    if (!IsValidTaskStatus(request.Status))
        return Results.BadRequest(new ErrorResponse("Status must be pending, in_progress or completed"));

    if (request.AssigneeId.HasValue)
    {
        var assigneeRole = await GetWorkspaceRoleAsync(db, workspaceId, request.AssigneeId.Value);
        if (assigneeRole is null)
            return Results.BadRequest(new ErrorResponse("Assignee must be a workspace member"));

        if (role == "member" && request.AssigneeId.Value != currentUser.Id)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    DateTime? dueAt;
    try
    {
        dueAt = ParseTaskDueAtOrThrow(request.DueAt, request.DueDate);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ErrorResponse(ex.Message));
    }

    if (dueAt.HasValue && dueAt.Value < DateTime.UtcNow)
        return Results.BadRequest(new ErrorResponse("deadline must be now or later"));

    if (request.StoryPoints.HasValue && request.StoryPoints.Value < 0)
        return Results.BadRequest(new ErrorResponse("story_points must be greater than or equal to 0"));

    var priority = string.IsNullOrWhiteSpace(request.Priority)
        ? "medium"
        : request.Priority.Trim().ToLowerInvariant();

    if (!IsValidTaskPriority(priority))
        return Results.BadRequest(new ErrorResponse("Priority must be low, medium, high or urgent"));

    var status = string.IsNullOrWhiteSpace(request.Status)
        ? "pending"
        : request.Status.Trim().ToLowerInvariant();

    var description = string.IsNullOrWhiteSpace(request.Description)
        ? null
        : request.Description.Trim();

    var dueDate = dueAt.HasValue ? DateOnly.FromDateTime(dueAt.Value) : (DateOnly?)null;

    await using var conn = await db.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    var sku = await GenerateUniqueTaskSkuAsync(conn, tx, workspaceId, title);

    const string sql = """
        INSERT INTO tasks (workspace_id, sku, title, description, due_date, due_at, story_points, priority, status, created_by, assignee_id)
        VALUES (@workspace_id, @sku, @title, @description, @due_date, @due_at, @story_points, @priority, @status, @created_by, @assignee_id)
        RETURNING id;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn, tx);
    cmd.Parameters.AddWithValue("workspace_id", workspaceId);
    cmd.Parameters.AddWithValue("sku", sku);
    cmd.Parameters.AddWithValue("title", title);
    cmd.Parameters.AddWithValue("description", (object?)description ?? DBNull.Value);
    cmd.Parameters.AddWithValue("due_date", dueDate.HasValue ? dueDate.Value : (object)DBNull.Value);
    cmd.Parameters.AddWithValue("due_at", dueAt.HasValue ? dueAt.Value : (object)DBNull.Value);
    cmd.Parameters.AddWithValue("story_points", request.StoryPoints.HasValue ? request.StoryPoints.Value : (object)DBNull.Value);
    cmd.Parameters.AddWithValue("priority", priority);
    cmd.Parameters.AddWithValue("status", status);
    cmd.Parameters.AddWithValue("created_by", currentUser.Id);
    cmd.Parameters.AddWithValue("assignee_id", request.AssigneeId.HasValue ? request.AssigneeId.Value : (object)DBNull.Value);

    var taskId = Convert.ToInt32(await cmd.ExecuteScalarAsync());

    if (request.StoryPoints.HasValue)
    {
        const string voteSql = """
            INSERT INTO task_story_point_votes (task_id, user_id, points)
            VALUES (@task_id, @user_id, @points)
            ON CONFLICT (task_id, user_id) DO NOTHING;
            """;

        await using var voteCmd = new NpgsqlCommand(voteSql, conn, tx);
        voteCmd.Parameters.AddWithValue("task_id", taskId);
        voteCmd.Parameters.AddWithValue("user_id", currentUser.Id);
        voteCmd.Parameters.AddWithValue("points", request.StoryPoints.Value);
        await voteCmd.ExecuteNonQueryAsync();

        await RecalculateTaskStoryPointsAsync(conn, tx, taskId);
    }

    await tx.CommitAsync();

    var response = await GetTaskResponseAsync(db, taskId, currentUser.Id);
    if (response is null)
        return Results.NotFound(new ErrorResponse("Task not found"));

    return Results.Created($"/api/tasks/{taskId}", response);
}

private static async Task<IResult> ListTasksAsync(HttpContext http, int workspaceId, [FromQuery] string? status, [FromQuery(Name = "assignee_id")] int? assigneeId, [FromQuery] string? priority, [FromQuery] string? search, [FromQuery] int? limit, [FromQuery] int? cursor, NpgsqlDataSource db)
{
    var currentUser = http.GetCurrentUser();
    var role = await GetWorkspaceRoleAsync(db, workspaceId, currentUser.Id);

    if (role is null)
        return Results.NotFound(new ErrorResponse("Workspace not found"));

    var normalizedStatus = string.IsNullOrWhiteSpace(status)
        ? null
        : status.Trim().ToLowerInvariant();

    if (!IsValidTaskStatus(normalizedStatus))
        return Results.BadRequest(new ErrorResponse("Status must be pending, in_progress or completed"));

    var normalizedPriority = string.IsNullOrWhiteSpace(priority)
        ? null
        : priority.Trim().ToLowerInvariant();

    if (!IsValidTaskPriority(normalizedPriority))
        return Results.BadRequest(new ErrorResponse("Priority must be low, medium, high or urgent"));

    var normalizedSearch = string.IsNullOrWhiteSpace(search)
        ? null
        : search.Trim();

    var take = Math.Clamp(limit ?? 12, 1, 100);
    var hasCursor = cursor.HasValue && cursor.Value > 0;

    await using var conn = await db.OpenConnectionAsync();

    var sql = """
        SELECT t.id, t.sku, t.title, t.description, t.due_date, t.due_at, t.story_points, t.priority, t.status, t.created_by, t.created_at,
               assignee.id AS assignee_id, assignee.name AS assignee_name,
               creator.id AS creator_id, creator.name AS creator_name,
               COALESCE(v.vote_count, 0) AS vote_count,
               v.average_points,
               uv.points AS my_vote_points
        FROM tasks t
        LEFT JOIN users assignee ON assignee.id = t.assignee_id
        LEFT JOIN users creator ON creator.id = t.created_by
        LEFT JOIN (
            SELECT task_id, COUNT(*)::int AS vote_count, ROUND(AVG(points)::numeric, 2) AS average_points
            FROM task_story_point_votes
            GROUP BY task_id
        ) v ON v.task_id = t.id
        LEFT JOIN task_story_point_votes uv ON uv.task_id = t.id AND uv.user_id = @current_user_id
        WHERE t.workspace_id = @workspace_id
        """;

    if (!string.IsNullOrWhiteSpace(normalizedStatus))
        sql += "\nAND t.status = @status";

    if (assigneeId.HasValue)
        sql += "\nAND t.assignee_id = @assignee_id";

    if (!string.IsNullOrWhiteSpace(normalizedPriority))
        sql += "\nAND t.priority = @priority";

    if (!string.IsNullOrWhiteSpace(normalizedSearch))
    {
        sql += """
            
            AND (
                t.sku ILIKE @search_pattern
                OR t.title ILIKE @search_pattern
                OR COALESCE(t.description, '') ILIKE @search_pattern
                OR COALESCE(creator.name, '') ILIKE @search_pattern
                OR COALESCE(assignee.name, '') ILIKE @search_pattern
            )
            """;
    }

    if (hasCursor)
        sql += "\nAND t.id < @cursor";

    sql += """

        ORDER BY t.id DESC
        LIMIT @take_plus_one;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("workspace_id", workspaceId);
    cmd.Parameters.AddWithValue("current_user_id", currentUser.Id);
    cmd.Parameters.AddWithValue("take_plus_one", take + 1);

    if (!string.IsNullOrWhiteSpace(normalizedStatus))
        cmd.Parameters.AddWithValue("status", normalizedStatus);

    if (assigneeId.HasValue)
        cmd.Parameters.AddWithValue("assignee_id", assigneeId.Value);

    if (!string.IsNullOrWhiteSpace(normalizedPriority))
        cmd.Parameters.AddWithValue("priority", normalizedPriority);

    if (!string.IsNullOrWhiteSpace(normalizedSearch))
        cmd.Parameters.AddWithValue("search_pattern", $"%{normalizedSearch}%");

    if (hasCursor)
        cmd.Parameters.AddWithValue("cursor", cursor!.Value);

    var items = new List<TaskListItem>();

    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var dueDateValue = reader.IsDBNull(4) ? (DateOnly?)null : reader.GetFieldValue<DateOnly>(4);
        var dueAtValue = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);

        UserBasicResponse? assignee = null;
        if (!reader.IsDBNull(11))
        {
            assignee = new UserBasicResponse(
                reader.GetInt32(11),
                reader.GetString(12));
        }

        UserBasicResponse? createdByUser = null;
        if (!reader.IsDBNull(13))
        {
            createdByUser = new UserBasicResponse(
                reader.GetInt32(13),
                reader.GetString(14));
        }

        items.Add(new TaskListItem(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? $"TASK-{reader.GetInt32(0)}" : reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            dueAtValue.HasValue ? dueAtValue.Value.ToString("yyyy-MM-dd") : dueDateValue?.ToString("yyyy-MM-dd"),
            dueAtValue.HasValue ? ToIsoString(dueAtValue.Value) : null,
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt32(9),
            createdByUser,
            assignee,
            ToIsoString(reader.GetDateTime(10)),
            reader.GetInt32(15),
            reader.IsDBNull(16) ? null : Convert.ToDouble(reader.GetDecimal(16)),
            reader.IsDBNull(17) ? null : reader.GetInt32(17)));
    }

    var hasMore = items.Count > take;
    if (hasMore)
        items = items.Take(take).ToList();

    var nextCursor = hasMore && items.Count > 0 ? items[^1].Id : (int?)null;
    return Results.Ok(new TaskListResponse(items, nextCursor, hasMore));
}


    private static async Task<IResult> UpdateTaskAsync(HttpContext http, int id, UpdateTaskRequest request, NpgsqlDataSource db)
    {
        var currentUser = http.GetCurrentUser();
        var taskInfo = await GetTaskInfoAsync(db, id);
        if (taskInfo is null)
            return Results.NotFound(new ErrorResponse("Task not found"));

        var role = await GetWorkspaceRoleAsync(db, taskInfo.WorkspaceId, currentUser.Id);
        if (role is null)
            return Results.NotFound(new ErrorResponse("Workspace not found"));

        var canEditContent = role == "owner" || taskInfo.CreatedBy == currentUser.Id;

        if (!IsValidTaskStatus(request.Status))
            return Results.BadRequest(new ErrorResponse("Status must be pending, in_progress or completed"));
        if (!IsValidTaskPriority(request.Priority))
            return Results.BadRequest(new ErrorResponse("Priority must be low, medium, high or urgent"));
        if (request.StoryPoints.HasValue)
            return Results.BadRequest(new ErrorResponse("story_points cannot be updated directly. Use the story point vote endpoint instead"));

        if (!canEditContent)
        {
            if (request.Title is not null || request.Description is not null || request.DueDate is not null || request.DueAt is not null || request.AssigneeId.HasValue || request.Priority is not null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request.Status is null)
                return Results.BadRequest(new ErrorResponse("Nothing to update"));
        }

        DateTime? dueAt = taskInfo.DueAt;
        var dueAtTouched = request.DueAt is not null || request.DueDate is not null;
        if (canEditContent && dueAtTouched)
        {
            if (request.DueAt == string.Empty || request.DueDate == string.Empty)
            {
                dueAt = null;
            }
            else
            {
                try { dueAt = ParseTaskDueAtOrThrow(request.DueAt, request.DueDate); }
                catch (ArgumentException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }
            }
        }

        if (canEditContent && dueAtTouched && dueAt.HasValue && dueAt.Value < DateTime.UtcNow)
        {
            var isSameDeadline = taskInfo.DueAt.HasValue && taskInfo.DueAt.Value == dueAt.Value;
            if (!isSameDeadline)
                return Results.BadRequest(new ErrorResponse("deadline must be now or later"));
        }

        int? assigneeId = taskInfo.AssigneeId;
        if (canEditContent && request.AssigneeId.HasValue)
        {
            var newAssigneeRole = await GetWorkspaceRoleAsync(db, taskInfo.WorkspaceId, request.AssigneeId.Value);
            if (newAssigneeRole is null)
                return Results.BadRequest(new ErrorResponse("Assignee must be a workspace member"));
            if (role == "member" && request.AssigneeId.Value != currentUser.Id)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            assigneeId = request.AssigneeId.Value;
        }

        var newTitle = canEditContent && request.Title is not null ? request.Title.Trim() : taskInfo.Title;
        if (string.IsNullOrWhiteSpace(newTitle))
            return Results.BadRequest(new ErrorResponse("Title is required"));
        var newDescription = canEditContent && request.Description is not null ? (string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()) : taskInfo.Description;
        var newPriority = canEditContent ? (string.IsNullOrWhiteSpace(request.Priority) ? taskInfo.Priority : request.Priority.Trim().ToLowerInvariant()) : taskInfo.Priority;
        var newStatus = string.IsNullOrWhiteSpace(request.Status) ? taskInfo.Status : request.Status.Trim().ToLowerInvariant();
        var dueDate = dueAt.HasValue ? DateOnly.FromDateTime(dueAt.Value) : (DateOnly?)null;

        await using var conn = await db.OpenConnectionAsync();
        const string sql = """
            UPDATE tasks
            SET title = @title, description = @description, due_date = @due_date, due_at = @due_at,
                priority = @priority, assignee_id = @assignee_id, status = @status, updated_at = NOW()
            WHERE id = @id
            RETURNING id;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("title", newTitle);
        cmd.Parameters.AddWithValue("description", (object?)newDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("due_date", dueDate.HasValue ? dueDate.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("due_at", dueAt.HasValue ? dueAt.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("priority", newPriority);
        cmd.Parameters.AddWithValue("assignee_id", assigneeId.HasValue ? assigneeId.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("status", newStatus);
        cmd.Parameters.AddWithValue("id", id);
        var savedId = await cmd.ExecuteScalarAsync();
        if (savedId is null)
            return Results.NotFound(new ErrorResponse("Task not found"));

        var response = await GetTaskResponseAsync(db, id, currentUser.Id);
        return response is null ? Results.NotFound(new ErrorResponse("Task not found")) : Results.Ok(response);
    }

private static async Task<IResult> VoteTaskStoryPointsAsync(HttpContext http, int id, VoteTaskStoryPointsRequest request, NpgsqlDataSource db)
{
    var currentUser = http.GetCurrentUser();
    var taskInfo = await GetTaskInfoAsync(db, id);

    if (taskInfo is null)
        return Results.NotFound(new ErrorResponse("Task not found"));

    var role = await GetWorkspaceRoleAsync(db, taskInfo.WorkspaceId, currentUser.Id);
    if (role is null)
        return Results.NotFound(new ErrorResponse("Workspace not found"));

    if (request.Points < 0)
        return Results.BadRequest(new ErrorResponse("points must be greater than or equal to 0"));

    await using var conn = await db.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    const string sql = """
        INSERT INTO task_story_point_votes (task_id, user_id, points)
        VALUES (@task_id, @user_id, @points)
        ON CONFLICT (task_id, user_id) DO NOTHING
        RETURNING points;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn, tx);
    cmd.Parameters.AddWithValue("task_id", id);
    cmd.Parameters.AddWithValue("user_id", currentUser.Id);
    cmd.Parameters.AddWithValue("points", request.Points);

    var inserted = await cmd.ExecuteScalarAsync();
    if (inserted is null)
        return Results.Conflict(new ErrorResponse("You have already voted for this task"));

    await RecalculateTaskStoryPointsAsync(conn, tx, id);
    await tx.CommitAsync();

    var response = await GetTaskResponseAsync(db, id, currentUser.Id);
    if (response is null)
        return Results.NotFound(new ErrorResponse("Task not found"));

    return Results.Ok(response);
}

private static async Task<IResult> DeleteTaskAsync(HttpContext http, int id, NpgsqlDataSource db)
{
    var currentUser = http.GetCurrentUser();
    var taskInfo = await GetTaskInfoAsync(db, id);

    if (taskInfo is null)
        return Results.NotFound(new ErrorResponse("Task not found"));

    var role = await GetWorkspaceRoleAsync(db, taskInfo.WorkspaceId, currentUser.Id);
    if (role is null)
        return Results.NotFound(new ErrorResponse("Workspace not found"));

    if (role != "owner" && taskInfo.CreatedBy != currentUser.Id)
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    await using var conn = await db.OpenConnectionAsync();

    const string sql = """
        DELETE FROM tasks
        WHERE id = @id;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("id", id);
    await cmd.ExecuteNonQueryAsync();

    return Results.NoContent();
}

private static async Task<IResult> WorkspaceStatsAsync(HttpContext http, int workspaceId, NpgsqlDataSource db)
{
    var currentUser = http.GetCurrentUser();
    var role = await GetWorkspaceRoleAsync(db, workspaceId, currentUser.Id);

    if (role is null)
        return Results.NotFound(new ErrorResponse("Workspace not found"));

    var stats = await GetTaskStatsAsync(db, workspaceId);
    return Results.Ok(stats);
}

private static async Task<IResult> MyStatsAsync(HttpContext http, int workspaceId, NpgsqlDataSource db)
{
    var currentUser = http.GetCurrentUser();
    var role = await GetWorkspaceRoleAsync(db, workspaceId, currentUser.Id);

    if (role is null)
        return Results.NotFound(new ErrorResponse("Workspace not found"));

    var stats = await GetTaskStatsAsync(db, workspaceId, currentUser.Id);
    return Results.Ok(stats);
}

private static async Task RecalculateTaskStoryPointsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int taskId)
{
    const string sql = """
        WITH vote_summary AS (
            SELECT COUNT(*)::int AS vote_count,
                   ROUND(AVG(points)::numeric, 0)::int AS rounded_story_points
            FROM task_story_point_votes
            WHERE task_id = @task_id
        )
        UPDATE tasks t
        SET story_points = CASE WHEN vs.vote_count = 0 THEN NULL ELSE vs.rounded_story_points END,
            updated_at = NOW()
        FROM vote_summary vs
        WHERE t.id = @task_id;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn, tx);
    cmd.Parameters.AddWithValue("task_id", taskId);
    await cmd.ExecuteNonQueryAsync();
}

private static async Task<TaskResponse?> GetTaskResponseAsync(NpgsqlDataSource db, int taskId, int currentUserId)
{
    await using var conn = await db.OpenConnectionAsync();

    const string sql = """
        SELECT t.id, t.workspace_id, t.sku, t.title, t.description, t.due_date, t.due_at, t.story_points, t.priority, t.status, t.created_by,
               creator.id AS creator_id, creator.name AS creator_name,
               assignee.id AS assignee_id, assignee.name AS assignee_name,
               t.created_at,
               COALESCE(v.vote_count, 0) AS vote_count,
               v.average_points,
               uv.points AS my_vote_points
        FROM tasks t
        LEFT JOIN users creator ON creator.id = t.created_by
        LEFT JOIN users assignee ON assignee.id = t.assignee_id
        LEFT JOIN (
            SELECT task_id, COUNT(*)::int AS vote_count, ROUND(AVG(points)::numeric, 2) AS average_points
            FROM task_story_point_votes
            GROUP BY task_id
        ) v ON v.task_id = t.id
        LEFT JOIN task_story_point_votes uv ON uv.task_id = t.id AND uv.user_id = @current_user_id
        WHERE t.id = @id;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("id", taskId);
    cmd.Parameters.AddWithValue("current_user_id", currentUserId);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return null;

    var dueDateValue = reader.IsDBNull(5) ? (DateOnly?)null : reader.GetFieldValue<DateOnly>(5);
    var dueAtValue = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6);

    UserBasicResponse? createdByUser = null;
    if (!reader.IsDBNull(11))
    {
        createdByUser = new UserBasicResponse(
            reader.GetInt32(11),
            reader.GetString(12));
    }

    UserBasicResponse? assignee = null;
    if (!reader.IsDBNull(13))
    {
        assignee = new UserBasicResponse(
            reader.GetInt32(13),
            reader.GetString(14));
    }

    return new TaskResponse(
        reader.GetInt32(0),
        reader.GetInt32(1),
        reader.IsDBNull(2) ? $"TASK-{reader.GetInt32(0)}" : reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        dueAtValue.HasValue ? dueAtValue.Value.ToString("yyyy-MM-dd") : dueDateValue?.ToString("yyyy-MM-dd"),
        dueAtValue.HasValue ? ToIsoString(dueAtValue.Value) : null,
        reader.IsDBNull(7) ? null : reader.GetInt32(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetInt32(10),
        createdByUser,
        assignee,
        ToIsoString(reader.GetDateTime(15)),
        reader.GetInt32(16),
        reader.IsDBNull(17) ? null : Convert.ToDouble(reader.GetDecimal(17)),
        reader.IsDBNull(18) ? null : reader.GetInt32(18));
}

private static async Task<TaskStatsResponse> GetTaskStatsAsync(NpgsqlDataSource db, int workspaceId, int? assigneeId = null)
{
    await using var conn = await db.OpenConnectionAsync();

    var sql = """
        SELECT
            COUNT(*) AS total,
            COUNT(*) FILTER (WHERE status = 'pending') AS pending,
            COUNT(*) FILTER (WHERE status = 'in_progress') AS in_progress,
            COUNT(*) FILTER (WHERE status = 'completed') AS completed,
            COUNT(*) FILTER (WHERE COALESCE(due_at::date, due_date) < CURRENT_DATE AND status <> 'completed') AS overdue
        FROM tasks
        WHERE workspace_id = @workspace_id
        """;

    if (assigneeId.HasValue)
        sql += " AND assignee_id = @assignee_id";

    sql += ";";

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("workspace_id", workspaceId);

    if (assigneeId.HasValue)
        cmd.Parameters.AddWithValue("assignee_id", assigneeId.Value);

    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();

    return new TaskStatsResponse(
        reader.GetInt32(0),
        reader.GetInt32(1),
        reader.GetInt32(2),
        reader.GetInt32(3),
        reader.GetInt32(4));
}

private static async Task<string?> GetWorkspaceRoleAsync(NpgsqlDataSource db, int workspaceId, int userId)
{
    await using var conn = await db.OpenConnectionAsync();

    const string sql = """
        SELECT role
        FROM workspace_members
        WHERE workspace_id = @workspace_id AND user_id = @user_id;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("workspace_id", workspaceId);
    cmd.Parameters.AddWithValue("user_id", userId);

    return (string?)await cmd.ExecuteScalarAsync();
}

private static async Task<string?> GetWorkspaceNameAsync(NpgsqlDataSource db, int workspaceId)
{
    await using var conn = await db.OpenConnectionAsync();

    const string sql = """
        SELECT name
        FROM workspaces
        WHERE id = @id;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("id", workspaceId);

    return (string?)await cmd.ExecuteScalarAsync();
}

private static async Task<UserBasicResponse?> GetUserBasicAsync(NpgsqlDataSource db, int userId)
{
    await using var conn = await db.OpenConnectionAsync();

    const string sql = """
        SELECT id, name
        FROM users
        WHERE id = @id;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("id", userId);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return null;

    return new UserBasicResponse(reader.GetInt32(0), reader.GetString(1));
}

private static async Task<TaskInfo?> GetTaskInfoAsync(NpgsqlDataSource db, int id)
{
    await using var conn = await db.OpenConnectionAsync();

    const string sql = """
        SELECT id, workspace_id, sku, title, description, due_date, due_at, story_points, priority, status, created_by, assignee_id
        FROM tasks
        WHERE id = @id;
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("id", id);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return null;

    var dueDateValue = reader.IsDBNull(5) ? (DateOnly?)null : reader.GetFieldValue<DateOnly>(5);
    var dueAtValue = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6);

    if (!dueAtValue.HasValue && dueDateValue.HasValue)
        dueAtValue = DateTime.SpecifyKind(dueDateValue.Value.ToDateTime(new TimeOnly(23, 59, 0)), DateTimeKind.Utc);

    return new TaskInfo(
        reader.GetInt32(0),
        reader.GetInt32(1),
        reader.IsDBNull(2) ? $"TASK-{reader.GetInt32(0)}" : reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        dueAtValue,
        reader.IsDBNull(7) ? null : reader.GetInt32(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetInt32(11));
}

private static bool IsValidTaskStatus(string? status)
{
    return status is null || status is "pending" or "in_progress" or "completed";
}

private static bool IsValidTaskPriority(string? priority)
{
    return priority is null || priority is "low" or "medium" or "high" or "urgent";
}

private static string ToIsoString(DateTime value)
{
    var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    return utc.ToString("O");
}



}
