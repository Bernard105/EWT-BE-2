namespace EasyWorkTogether.Api.Infrastructure;

public sealed class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await db.OpenConnectionAsync();

        const string sql = """
            CREATE EXTENSION IF NOT EXISTS pgcrypto;

            CREATE TABLE IF NOT EXISTS users (
                id SERIAL PRIMARY KEY,
                email VARCHAR(255) UNIQUE NOT NULL,
                password_hash TEXT NOT NULL,
                name VARCHAR(100) NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                email_verified_at TIMESTAMPTZ
            );
            ALTER TABLE users ADD COLUMN IF NOT EXISTS email_verified_at TIMESTAMPTZ;

            CREATE TABLE IF NOT EXISTS sessions (
                id SERIAL PRIMARY KEY,
                token UUID UNIQUE NOT NULL,
                user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                expires_at TIMESTAMPTZ NOT NULL
            );
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS token UUID;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS user_id INT;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS expires_at TIMESTAMPTZ;
            UPDATE sessions SET token = gen_random_uuid() WHERE token IS NULL;
            UPDATE sessions SET expires_at = TIMESTAMPTZ '9999-12-31 23:59:59+00' WHERE expires_at IS NULL OR expires_at < TIMESTAMPTZ '9999-12-31 23:59:59+00';

            CREATE TABLE IF NOT EXISTS password_reset_tokens (
                id SERIAL PRIMARY KEY,
                user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                token_hash VARCHAR(128) UNIQUE NOT NULL,
                expires_at TIMESTAMPTZ NOT NULL,
                used_at TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            ALTER TABLE password_reset_tokens ADD COLUMN IF NOT EXISTS user_id INT;
            ALTER TABLE password_reset_tokens ADD COLUMN IF NOT EXISTS token VARCHAR(128);
            ALTER TABLE password_reset_tokens ADD COLUMN IF NOT EXISTS token_hash VARCHAR(128);
            ALTER TABLE password_reset_tokens ADD COLUMN IF NOT EXISTS expires_at TIMESTAMPTZ;
            ALTER TABLE password_reset_tokens ADD COLUMN IF NOT EXISTS used_at TIMESTAMPTZ;
            ALTER TABLE password_reset_tokens ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
            UPDATE password_reset_tokens
            SET token_hash = encode(digest(token, 'sha256'), 'hex')
            WHERE token_hash IS NULL AND token IS NOT NULL;

            CREATE TABLE IF NOT EXISTS email_verification_tokens (
                id SERIAL PRIMARY KEY,
                user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                token_hash VARCHAR(128) UNIQUE NOT NULL,
                expires_at TIMESTAMPTZ NOT NULL,
                used_at TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            ALTER TABLE email_verification_tokens ADD COLUMN IF NOT EXISTS user_id INT;
            ALTER TABLE email_verification_tokens ADD COLUMN IF NOT EXISTS token_hash VARCHAR(128);
            ALTER TABLE email_verification_tokens ADD COLUMN IF NOT EXISTS expires_at TIMESTAMPTZ;
            ALTER TABLE email_verification_tokens ADD COLUMN IF NOT EXISTS used_at TIMESTAMPTZ;
            ALTER TABLE email_verification_tokens ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

            CREATE TABLE IF NOT EXISTS external_identities (
                id SERIAL PRIMARY KEY,
                user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                provider VARCHAR(20) NOT NULL,
                provider_user_id VARCHAR(255) NOT NULL,
                provider_email VARCHAR(255),
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                last_login_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (provider, provider_user_id),
                UNIQUE (user_id, provider)
            );
            ALTER TABLE external_identities ADD COLUMN IF NOT EXISTS user_id INT;
            ALTER TABLE external_identities ADD COLUMN IF NOT EXISTS provider VARCHAR(20);
            ALTER TABLE external_identities ADD COLUMN IF NOT EXISTS provider_user_id VARCHAR(255);
            ALTER TABLE external_identities ADD COLUMN IF NOT EXISTS provider_email VARCHAR(255);
            ALTER TABLE external_identities ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
            ALTER TABLE external_identities ADD COLUMN IF NOT EXISTS last_login_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

            UPDATE users u
            SET email_verified_at = COALESCE(u.email_verified_at, u.created_at, NOW())
            WHERE u.email_verified_at IS NULL
              AND EXISTS (
                  SELECT 1
                  FROM external_identities ei
                  WHERE ei.user_id = u.id
              );

            UPDATE users u
            SET email_verified_at = COALESCE(u.email_verified_at, u.created_at, NOW())
            WHERE u.email_verified_at IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM email_verification_tokens evt
                  WHERE evt.user_id = u.id
              );

            CREATE TABLE IF NOT EXISTS workspaces (
                id SERIAL PRIMARY KEY,
                name VARCHAR(100) NOT NULL,
                owner_id INT NOT NULL REFERENCES users(id),
                domain_namespace VARCHAR(80),
                industry_vertical VARCHAR(80),
                workspace_logo_data TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            ALTER TABLE workspaces ADD COLUMN IF NOT EXISTS domain_namespace VARCHAR(80);
            ALTER TABLE workspaces ADD COLUMN IF NOT EXISTS industry_vertical VARCHAR(80);
            ALTER TABLE workspaces ADD COLUMN IF NOT EXISTS workspace_logo_data TEXT;
            ALTER TABLE workspaces ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

            CREATE TABLE IF NOT EXISTS workspace_members (
                workspace_id INT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
                user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                role VARCHAR(10) NOT NULL CHECK (role IN ('owner', 'member')),
                joined_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (workspace_id, user_id)
            );
            ALTER TABLE workspace_members ADD COLUMN IF NOT EXISTS joined_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

            CREATE TABLE IF NOT EXISTS workspace_invitations (
                id SERIAL PRIMARY KEY,
                workspace_id INT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
                inviter_id INT NOT NULL REFERENCES users(id),
                invitee_email VARCHAR(255) NOT NULL,
                code VARCHAR(64) UNIQUE NOT NULL,
                role VARCHAR(80),
                expires_at TIMESTAMPTZ NOT NULL,
                responded_at TIMESTAMPTZ,
                status VARCHAR(20) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'accepted', 'declined', 'revoked')),
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            ALTER TABLE workspace_invitations ADD COLUMN IF NOT EXISTS role VARCHAR(80);
            ALTER TABLE workspace_invitations ADD COLUMN IF NOT EXISTS responded_at TIMESTAMPTZ;
            ALTER TABLE workspace_invitations ADD COLUMN IF NOT EXISTS status VARCHAR(20) NOT NULL DEFAULT 'pending';
            ALTER TABLE workspace_invitations ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
            UPDATE workspace_invitations
            SET role = 'Team Member'
            WHERE role IS NULL OR BTRIM(role) = '';
            ALTER TABLE workspace_invitations DROP CONSTRAINT IF EXISTS workspace_invitations_status_check;
            ALTER TABLE workspace_invitations ADD CONSTRAINT workspace_invitations_status_check CHECK (status IN ('pending', 'accepted', 'declined', 'revoked'));

            CREATE TABLE IF NOT EXISTS tasks (
                id SERIAL PRIMARY KEY,
                workspace_id INT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
                sku VARCHAR(120),
                title VARCHAR(255) NOT NULL,
                description TEXT,
                due_date DATE,
                due_at TIMESTAMPTZ,
                story_points INT,
                priority VARCHAR(20) NOT NULL DEFAULT 'medium' CHECK (priority IN ('low', 'medium', 'high', 'urgent')),
                status VARCHAR(20) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'in_progress', 'completed')),
                created_by INT NOT NULL REFERENCES users(id),
                assignee_id INT REFERENCES users(id),
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            ALTER TABLE tasks ADD COLUMN IF NOT EXISTS sku VARCHAR(120);
            ALTER TABLE tasks ADD COLUMN IF NOT EXISTS story_points INT;
            ALTER TABLE tasks ADD COLUMN IF NOT EXISTS priority VARCHAR(20) NOT NULL DEFAULT 'medium';
            ALTER TABLE tasks ADD COLUMN IF NOT EXISTS due_at TIMESTAMPTZ;
            ALTER TABLE tasks ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
            UPDATE tasks SET priority = 'medium' WHERE priority IS NULL;
            UPDATE tasks
            SET due_at = COALESCE(
                due_at,
                CASE
                    WHEN due_date IS NULL THEN NULL
                    ELSE ((due_date::timestamp + INTERVAL '23 hours 59 minutes') AT TIME ZONE 'UTC')
                END
            )
            WHERE due_at IS NULL AND due_date IS NOT NULL;
            UPDATE tasks SET sku = 'TASK-' || id WHERE sku IS NULL OR BTRIM(sku) = '';
            ALTER TABLE tasks DROP CONSTRAINT IF EXISTS tasks_priority_check;
            ALTER TABLE tasks ADD CONSTRAINT tasks_priority_check CHECK (priority IN ('low', 'medium', 'high', 'urgent'));
            ALTER TABLE tasks DROP CONSTRAINT IF EXISTS tasks_story_points_check;
            ALTER TABLE tasks ADD CONSTRAINT tasks_story_points_check CHECK (story_points IS NULL OR story_points >= 0);

            CREATE TABLE IF NOT EXISTS task_story_point_votes (
                task_id INT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                points INT NOT NULL CHECK (points >= 0),
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (task_id, user_id)
            );
            ALTER TABLE task_story_point_votes ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
            ALTER TABLE task_story_point_votes DROP CONSTRAINT IF EXISTS task_story_point_votes_points_check;
            ALTER TABLE task_story_point_votes ADD CONSTRAINT task_story_point_votes_points_check CHECK (points >= 0);

            CREATE UNIQUE INDEX IF NOT EXISTS uq_external_identities_provider_user_id ON external_identities(provider, provider_user_id);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_external_identities_user_provider ON external_identities(user_id, provider);
            CREATE INDEX IF NOT EXISTS idx_sessions_token ON sessions(token);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_password_reset_tokens_token_hash ON password_reset_tokens(token_hash);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_email_verification_tokens_token_hash ON email_verification_tokens(token_hash);
            CREATE INDEX IF NOT EXISTS idx_email_verification_tokens_token_hash ON email_verification_tokens(token_hash);
            CREATE INDEX IF NOT EXISTS idx_email_verification_tokens_user_id ON email_verification_tokens(user_id);
            CREATE INDEX IF NOT EXISTS idx_password_reset_tokens_token_hash ON password_reset_tokens(token_hash);
            CREATE INDEX IF NOT EXISTS idx_password_reset_tokens_token ON password_reset_tokens(token);
            CREATE INDEX IF NOT EXISTS idx_password_reset_tokens_user_id ON password_reset_tokens(user_id);
            CREATE INDEX IF NOT EXISTS idx_external_identities_provider_user_id ON external_identities(provider, provider_user_id);
            CREATE INDEX IF NOT EXISTS idx_external_identities_user_id ON external_identities(user_id);
            CREATE INDEX IF NOT EXISTS idx_workspace_members_user_id ON workspace_members(user_id);
            CREATE INDEX IF NOT EXISTS idx_workspaces_domain_namespace ON workspaces(domain_namespace);
            CREATE INDEX IF NOT EXISTS idx_workspace_invitations_workspace_id ON workspace_invitations(workspace_id);
            CREATE INDEX IF NOT EXISTS idx_workspace_invitations_invitee_email_status ON workspace_invitations(invitee_email, status, expires_at);
            CREATE INDEX IF NOT EXISTS idx_tasks_workspace_id ON tasks(workspace_id);
            CREATE INDEX IF NOT EXISTS idx_tasks_workspace_id_desc ON tasks(workspace_id, id DESC);
            CREATE INDEX IF NOT EXISTS idx_tasks_assignee_id ON tasks(assignee_id);
            CREATE INDEX IF NOT EXISTS idx_tasks_priority_due_date ON tasks(priority, due_date);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_tasks_workspace_sku ON tasks(workspace_id, sku);
            CREATE INDEX IF NOT EXISTS idx_task_story_point_votes_task_id ON task_story_point_votes(task_id);
            CREATE INDEX IF NOT EXISTS idx_task_story_point_votes_user_id ON task_story_point_votes(user_id);
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

