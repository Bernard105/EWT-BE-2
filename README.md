# EasyWorkTogether API

This package is prepared for:

- local development with .NET 8+
- Swagger UI at `/swagger`
- OpenAPI JSON at `/swagger/v1/swagger.json`
- automatic Postman import from Swagger/OpenAPI
- deployment to Render with Docker and Render Postgres

## What was fixed

- Added Render-ready port binding via `PORT`
- Added support for `DATABASE_URL` in Render's PostgreSQL URL format
- Added forwarded-header handling so HTTPS/proxy URLs work correctly on Render
- Kept Swagger UI and OpenAPI JSON enabled in production
- Added a Dockerfile for Render Docker deploys
- Added `render.yaml` so Render can provision both the web service and PostgreSQL
- **Fixed `postgresMajorVersion` from `"18"` → `"16"` in `render.yaml`** — v18 is not a stable release and caused Blueprint provisioning to fail
- **Switched `property: connectionString` → `property: internalConnectionString` in `render.yaml`** — uses Render's private network for lower latency; no SSL overhead needed
- **Added SSL defaults in `DeploymentSupport.cs`** — `SslMode=Prefer` and `TrustServerCertificate=true` applied when not present in DATABASE_URL; also handles `sslrootcert=system` that some Render URL formats append
- Removed hardcoded local database password from `appsettings.json` (replaced with placeholder)
- Added support for `FRONTEND_BASE_URL` and `CORS_ALLOWED_ORIGINS` environment variables

## Local run

From the folder that contains `EasyWorkTogether.Api.csproj`:

```bash
dotnet restore
dotnet run --project EasyWorkTogether.Api.csproj
```

Default local config expects PostgreSQL on:

- host: `localhost`
- port: `5432`
- database: `easyworktogether`
- username: `postgres`
- password: `postgres`

If your local PostgreSQL differs, edit `appsettings.Development.json` or set:

```bash
ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=YOUR_DB;Username=YOUR_USER;Password=YOUR_PASSWORD"
```

## Local verification

After startup, verify:

- Home page: `http://localhost:5000/`
- Health: `http://localhost:5000/health`
- Status: `http://localhost:5000/api/status`
- Swagger UI: `http://localhost:5000/swagger`
- OpenAPI JSON: `http://localhost:5000/swagger/v1/swagger.json`

## Postman auto-import from Swagger

### Local

In Postman:

1. Click **Import**
2. Choose **Link**
3. Paste:

```text
http://localhost:5000/swagger/v1/swagger.json
```

Postman will generate a collection automatically from the OpenAPI document.

### After deploy on Render

Replace `YOUR-SERVICE` with your Render service hostname:

```text
https://YOUR-SERVICE.onrender.com/swagger/v1/swagger.json
```

You can import that URL directly into Postman the same way.

Swagger UI will be here:

```text
https://YOUR-SERVICE.onrender.com/swagger
```

## Deploy to Render

This repository includes a `render.yaml` Blueprint and a `Dockerfile`.

### Option A: deploy with `render.yaml`

1. Push this project to GitHub.
2. In Render, create a new **Blueprint** and point it at the repo.
3. Render will create:
   - a web service named `easyworktogether-api`
   - a PostgreSQL database named `easyworktogether-db`
4. During setup, provide:
   - `FRONTEND_BASE_URL` → your frontend URL, for example `https://your-frontend.onrender.com`
   - `CORS_ALLOWED_ORIGINS` → comma-separated origins, for example `https://your-frontend.onrender.com,http://localhost:5173`
5. Deploy.

### Option B: manual Render setup

Create a **Web Service** using Docker and a **Render Postgres** database.

Set these environment variables on the web service:

```text
PORT=10000
ASPNETCORE_ENVIRONMENT=Production
DATABASE_URL=<Render private PostgreSQL connection string>
FRONTEND_BASE_URL=https://your-frontend-domain
CORS_ALLOWED_ORIGINS=https://your-frontend-domain,http://localhost:5173
```

Then deploy.

## OAuth callback URLs on Render

If you configure Google or GitHub OAuth, use your deployed backend URL:

- Google: `https://YOUR-SERVICE.onrender.com/api/oauth/google/callback`
- GitHub: `https://YOUR-SERVICE.onrender.com/api/oauth/github/callback`

## Notes

- The app auto-creates required PostgreSQL tables at startup.
- `pgcrypto` is required and the app enables it with `CREATE EXTENSION IF NOT EXISTS pgcrypto;`.
- If email SMTP is not configured, related endpoints still work and return the verification/reset links directly.
- For local development, `appsettings.Development.json` is used first.
- For Render, `DATABASE_URL` is supported directly from the private Postgres connection string.
