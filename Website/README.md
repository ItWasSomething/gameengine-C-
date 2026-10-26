# MyEngine Games — the website

A site where people post games made with MyEngine: screenshots, a description,
tags, and a downloadable Windows build (`.zip`). Visitors browse, search,
like, comment and download; creators upload new versions over time — from the
browser, or straight from the editor with **File → Publish to MyEngine Games**.

```
Website/
├─ Server/   ASP.NET Core API (.NET 10) + SQLite (EF Core) + file uploads
└─ Client/   React + TypeScript (Vite) — the actual web pages
```

## Run it locally

You need the .NET 10 SDK and Node.js 20+.

**Terminal 1 — the API** (http://localhost:5080):

```powershell
dotnet run --project Website/Server
```

**Terminal 2 — the site** (http://localhost:5173, hot reload):

```powershell
cd Website/Client
npm install        # first time only
npm run dev
```

Open http://localhost:5173. Vite forwards `/api`, `/auth`, `/media` and
`/signin-github` to the API, so the browser sees one site and the login cookie
just works.

Until GitHub login is set up (below), the sign-in page shows a **dev-only
sign-in** that takes any username — enough to try posting, liking and
commenting. It only exists when the server runs in Development.

## Set up "Sign in with GitHub"

1. GitHub → **Settings → Developer settings → OAuth Apps → New OAuth App**.
   - Homepage URL: `http://localhost:5173`
   - Authorization callback URL: `http://localhost:5173/signin-github`
2. Copy the **Client ID**, generate a **Client secret**.
3. Store them with .NET user-secrets (kept outside the repo, so they can't be
   committed by accident):

   ```powershell
   cd Website/Server
   dotnet user-secrets set "GitHub:ClientId" "<client id>"
   dotnet user-secrets set "GitHub:ClientSecret" "<client secret>"
   ```
4. Restart the server. The sign-in page now shows **Continue with GitHub**.

## Publishing from the editor

In the editor: **File → Publish to MyEngine Games…** → **Sign in in browser**.
The editor opens `/device?code=XXXX-XXXX`; you approve it there (signed in with
GitHub), and the editor receives its own API token. It never sees your GitHub
password or session. Then **Build & Publish** builds the game and uploads it —
as a new game the first time, as a new version after that.

Signed-in editors are listed (and can be revoked) on the site's **Settings** page.
The editor's website URL defaults to `http://localhost:5080`; change it in the
Publish window once the site is deployed.

## How it works

| Piece | Where |
|---|---|
| Tables: users, games, tags, screenshots, versions, likes, comments, API tokens | `Server/Data/Models.cs` |
| Browse/search/sort, create, edit, versions, downloads, likes, comments | `Server/Api/GameEndpoints.cs` |
| Sign in/out, profiles, settings, editor device login | `Server/Api/UserEndpoints.cs`, `Server/Auth/` |
| Uploads (type checked by file content, random names) | `Server/Storage/FileStore.cs` |
| Pages | `Client/src/pages/` |

Security choices worth knowing:
- **Cookies** are HttpOnly + SameSite=Lax, and every cookie-authenticated
  write must carry an `X-MyEngine: 1` header (other websites can't add custom
  headers, so they can't forge requests — CSRF protection).
- **API tokens** for the editor are stored as SHA-256 hashes only.
- **Uploads**: images must really be PNG/JPEG/GIF/WebP and builds must really
  be zips (checked by their first bytes), with size limits (8 MB per image,
  1 GB per build) and random file names.
- **Rate limits** on uploads, comments and device-login starts.
- Downloads are counted with a single SQL `UPDATE`, so simultaneous downloads
  don't lose counts.

Data lives in `Server/App_Data/` (`site.db` + `uploads/`). It's git-ignored;
back up that folder to back up the site.

## Deploying (later)

1. `cd Website/Client && npm run build` — writes the site into `Server/wwwroot/`,
   so the ASP.NET server serves pages and API from one origin.
2. `dotnet publish Website/Server -c Release -o out` and run it behind HTTPS
   (a reverse proxy like Caddy or nginx; forwarded headers are already handled).
3. Configure `Site:PublicUrl` (your https URL), `Site:DataPath` (a persistent
   folder), and `GitHub:ClientId`/`GitHub:ClientSecret` via environment variables
   (`Site__PublicUrl`, `GitHub__ClientId`, …). Make a separate GitHub OAuth app
   whose callback is `https://your-domain/signin-github`.

The database is created on first run (`EnsureCreated`). Before changing the
schema on a live site, switch to EF Core migrations.
