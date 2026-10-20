# Deployment — Plesk (Windows / IIS)

Target layout (one site, one subdomain):

```
https://api.example.com/             D2D central API   (the site's root application)
https://api.example.com/payyet       payyet            (IIS application)
https://api.example.com/joddaily     joddaily          (IIS application)
https://api.example.com/booksabuy    booksabuy         (IIS application)
```

D2D is the root application and the apps are IIS applications below it. D2D's `web.config` wraps its
settings in `<location path="." inheritInChildApplications="false">`, so the apps do not inherit anything from
D2D, and D2D has no path prefix (`Server:PublicBaseUrl` is just `https://api.example.com`).

Not tested on a real Plesk server — verify each step there.

## 1. Prerequisites

- **.NET 8 Hosting Bundle** on the server (the existing apps may use another version; versions run side by side).
- A SQL Server database for the master DB (Plesk → Databases).
- A writable folder **outside the web root** for secrets, e.g. `...\vhosts\example.com\private\d2d\`.
  The app identity needs read/write there and nobody else should.
- Every app runs **out-of-process**, or in its own app pool (in-process hosting allows one ASP.NET Core app per pool).
  D2D is out-of-process by default.
- Reserved first path segments: `api`, `webhooks`, `health`, `.well-known`, `swagger`, `admin` belong to D2D; no app may use those names.

## 2. Build D2D

```bash
dotnet publish src/D2D.Api -c Release -o publish/d2d
```

The output has a generated `web.config` (`hostingModel="outofprocess"`) and no `appsettings.Development.json`.
Copy `deploy/appsettings.Production.example.json` to `appsettings.Production.json` **on the server** (never commit it):

| Setting | Value |
|---|---|
| `Server:PublicBaseUrl`, `Jwt:Issuer` | `https://api.example.com` |
| `MasterDb:ConnectionString` | SQL Server connection string |
| `Line:LoginCallbackUrl` | `https://api.example.com/api/v1/auth/line/callback` |
| `Admin:ApiKey` | a long random string |
| `Jwt:PrivateKeyPath`, `DataProtection:KeysPath` | folders under `private\d2d\` |
| `DataProtection:ProtectWithDpapi` | `true` (set `false` if the app fails to start: the app pool then lacks a loaded user profile) |

`MasterDb:AutoMigrate=true` applies the EF Core migrations when the app starts: the first start creates every table, and later
versions upgrade the schema in place. Prefer `AutoMigrate=false` plus `./scripts/script-migrations.ps1 SqlServer out.sql` if a DBA
should review the SQL first. The database user needs permission to create and alter tables. Never point two different versions of D2D
(for example a developer machine and production) at the same database.

**Back up** `private\d2d\` (Data Protection keys and `jwt-signing.pem`). Without the key ring every stored
secret (connection strings, LINE secrets and tokens) is unreadable; without the JWT key everyone is signed out.

## 3. Cut-over (payyet and joddaily move to new paths)

payyet moves from `/` to `/payyet` and joddaily from `/jod` to `/joddaily`, so their URLs change.
Do it in one maintenance window outside business hours:

1. **Before the window:** back up all site files and databases. In the LINE console turn on *Webhook redelivery*
   for the bot channels, so events that fail during the window are sent again.
2. Take payyet offline (`app_offline.htm`).
3. Move payyet's files into `httpdocs\payyet` and joddaily's into `httpdocs\joddaily`; in Plesk
   (Virtual Directories) convert each folder to an IIS application. Nothing of payyet may remain in the site root.
4. Upload D2D (section 2) to the site root. Do not overwrite the apps' `web.config` files.
5. Check `https://api.example.com/health` and the two `.well-known` URLs below, then bring payyet and joddaily back
   and check they answer at `/payyet` and `/joddaily`.
6. Register the apps and move their LINE traffic (sections 4–5).

**Admin console.** After the first start open `https://api.example.com/admin/`. While no admin exists it shows a setup form: enter the
`Admin:ApiKey` from `appsettings.Production.json` (used once, not stored) and create the first admin. The session cookie is marked
`Secure` when the request is HTTPS, so serve D2D over HTTPS (Let's Encrypt in Plesk). If `/admin/` shows a blank page, check that
the published `wwwrootdmin` folder was uploaded and that IIS is not blocking `.js`/`.css`.

Checks for D2D:

```
GET  https://api.example.com/health
GET  https://api.example.com/.well-known/openid-configuration   (issuer and jwks_uri must be https://api.example.com...)
GET  https://api.example.com/.well-known/jwks.json
```

If the `.well-known` URLs return an IIS 404 page, the request is intercepted before it reaches ASP.NET Core;
check request filtering / static-file handling for dot-prefixed paths in Plesk.

Each app must work under its path prefix: absolute URLs such as `/api/...` or `/css/...` in code or frontends must
become relative or use the base path (`PathBase`, Vite `base`, `<base href>`). booksabuy is simply a new folder
`httpdocs\booksabuy` converted to an application.

## 4. Connect the apps

1. Register each app with `POST /api/v1/admin/apps` (header `X-Admin-Key`) and store the returned `apiKey` in that app's backend.
2. Add its LINE channels with `PUT /api/v1/admin/apps/{id}/line-channels` (include `providerId`) and set the LINE webhook to the returned `webhookUrl`.
   If the app's backend should fetch the channel's keys from D2D instead of storing them, add `allowCredentialAccess: true`
   and have it call `GET /api/v1/line/channels/{channelId}/credentials` (see ARCHITECTURE.md).
   To keep an app's existing bot code, set `webhookForwardUrl` to the app's new webhook address
   (e.g. `https://api.example.com/payyet/...`); events arrive signed with the channel secret, so its LINE SDK code keeps working.
3. In each app's backend, validate tokens through the JWKS (see ARCHITECTURE.md → *Validating tokens in an app's own backend*)
   and require the app's own `app_code` claim.
4. To keep an app's database connection string in D2D instead of its own config, register it with
   `PUT /api/v1/admin/apps/{id}/database` (`allowCredentialAccess: true`, `verify: false` if D2D cannot reach it) and have the
   app call `GET /api/v1/database/credentials` at startup (see ARCHITECTURE.md → *Two ways an app uses its database*).
5. Add `https://api.example.com/api/v1/auth/line/callback` to every LINE Login channel's callback URLs.
6. Add each app's frontend/callback URLs to its `redirectUris` and its origin to `Cors:AllowedOrigins`.

## 5. Update every place that stores an old URL

LIFF endpoint URLs, LINE webhook and callback URLs, payment-provider callbacks, emails and bookmarks that point to
`api.example.com/` (payyet) or `/jod` (joddaily). LINE does not reliably follow redirects for webhooks, so do not rely
on a redirect from the old paths.

## 6. Redeploying D2D

D2D lives in the site root, so a deploy that deletes files missing from the upload would also delete the app folders.
Here the apps can be re-uploaded, but never let a D2D deploy overwrite or delete `private\d2d\` (outside the web
root, so it is safe by design). A broken `web.config` at the root can make the applications below it return errors too.

## 7. If D2D moves later

Only URLs change: `Server:PublicBaseUrl`, `Jwt:Issuer`, `Line:LoginCallbackUrl`, the LINE console webhook
and callback URLs, and the `Authority` in each backend. Copy the key folder to keep signatures and stored secrets valid.
