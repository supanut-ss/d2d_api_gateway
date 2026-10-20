# D2D Central API

Shared API for many apps: authentication (email/password, LINE Login, LIFF), multi-app tenancy,
per-app databases on different engines, LINE webhooks and the full LINE Messaging API
with automatic channel access tokens.

Design: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — Plesk/IIS deployment: [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)

## Run locally

Requires .NET 8 SDK. Development settings use SQLite, so no database server is needed.

```bash
dotnet run --project src/D2D.Api --launch-profile http
```

Swagger UI: http://localhost:5080/swagger — health: http://localhost:5080/health

Admin console: http://localhost:5080/admin/ — on the first visit it asks for the admin key (`dev-admin-key` in
`appsettings.Development.json`) and lets you create the first admin.

## Try it

```bash
# 1. Register an app (admin key from appsettings.Development.json). Save the apiKey from the response.
curl -X POST http://localhost:5080/api/v1/admin/apps -H "X-Admin-Key: dev-admin-key" -H "Content-Type: application/json" \
  -d '{"code":"shop-a","name":"Shop A","redirectUris":["http://localhost:3000/callback"]}'

# 2. Register a user in that app
curl -X POST http://localhost:5080/api/v1/auth/register -H "X-App-Id: shop-a" -H "Content-Type: application/json" \
  -d '{"email":"alice@example.com","password":"P@ssw0rd!","displayName":"Alice"}'

# 3. Give the app its own database (any supported provider)
curl -X PUT http://localhost:5080/api/v1/admin/apps/{appId}/database -H "X-Admin-Key: dev-admin-key" -H "Content-Type: application/json" \
  -d '{"provider":"Sqlite","connectionString":"Data Source=shop_a.db","provision":true}'

# 4. Connect LINE channels
curl -X PUT http://localhost:5080/api/v1/admin/apps/{appId}/line-channels -H "X-Admin-Key: dev-admin-key" -H "Content-Type: application/json" \
  -d '{"type":"Messaging","channelId":"<id>","providerId":"<line-provider-id>","channelSecret":"<secret>","accessToken":"<token>"}'
# then set the LINE webhook URL to https://<host>/webhooks/line/<id>
```

## Database migrations

The master database schema is created and upgraded by EF Core migrations (one project per engine, see
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#migrations)). `MasterDb:AutoMigrate=true` (on in development) applies them at startup.
After changing an entity run `./scripts/add-migration.ps1 <Name>`; `dotnet test` fails until every provider has the migration.
A local SQLite file created before migrations existed must be deleted once (`src/D2D.Api/d2d_master.db*`).

## Test

```bash
dotnet test
```

Includes integration tests that run the real API in memory against a temporary SQLite database
(login, refresh rotation, members/roles, per-app databases, LINE webhook and login redirect, JWKS).

To prove a real engine works, run the same suite against an **empty** database (the tests create their own apps and users
there, so drop the tables afterwards):

```bash
D2D_TEST_DB_PROVIDER=SqlServer D2D_TEST_DB_CONNECTION="Server=host,1433;Database=empty_db;User Id=...;Password=...;TrustServerCertificate=True"   dotnet test --filter "FullyQualifiedName~Integration"
```
"# d2d_api_gateway" 
