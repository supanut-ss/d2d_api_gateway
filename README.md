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

## Test

```bash
dotnet test
```

Includes integration tests that run the real API in memory against a temporary SQLite database
(login, refresh rotation, members/roles, per-app databases, LINE webhook and login redirect, JWKS).
"# d2d_api_gateway" 
