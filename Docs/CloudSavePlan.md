# JANOARG — Cloud Save Plan

Status: **draft / decisions open** · Last updated: 2026-10-03

Long-term, automatic backup and cross-device retention, gated behind a TapTap
account. This is the *bigger* project and depends on the shared save blob defined
in `Docs/SaveImportExportPlan.md`. It intentionally records *why* choices were
made, so later changes can be judged against the original constraints.

---

## 1. Goal

Players sign in with TapTap and their progress follows them across devices and
reinstalls, with a **Phigros-style conflict prompt** when local and cloud both
changed. Must stay free to operate, and self-hostable for an open-source project.

## 2. Locked decisions

| Area | Decision |
| --- | --- |
| Conflict model | **Whole encrypted blob + revision + user prompt**, *not* per-key merge. |
| Backend compute | **Cloudflare Worker (TypeScript)** — free, serverless, reliable, global. |
| Save storage | **Cloudflare D1** (SQLite). 5 GB, 5M reads + 100k writes/day free. |
| Save blob | Reuses the shared AEAD container (see `SaveImportExportPlan.md` §4) — obscurity, not real anti-cheat. |
| Identity | **TapTap Login** → key on `unionid` (not `openid`), mapped to our own session. |
| Asset storage | **Cloudflare R2** (charts/covers), staged later. No egress fees. |
| Single endpoint | One host: `/api/*` handled by the Worker, `/v1/*` proxied to SpacetimeDB Maincloud (future realtime). |
| Local dev / OSS | `wrangler dev` (workerd + Miniflare) runs everything, no account, offline. |
| Contracts | Pure-C# contracts in a submodule; server stays TS. |

### Why not C# on the server
Cloudflare Workers' first-class runtimes are JS/TS, Python, Rust — no .NET (Mono
needs a JS shim, won't fit the isolate model). Once the conflict-prompt model is
chosen, the server has almost no shared logic (get/put/revision/409), so keeping
it in C# buys little. The C# side stays C# (the client); only the server is TS.

## 3. Non-goals (for now)

- Server-authoritative scores / real anti-cheat.
- Real-time / subscriber-mandatory features (planned, not v1).
- Chart distribution via R2 (planned, not v1).
- Manual file transfer — that is **Save Import/Export**, a separate plan.

## 4. Repository layout

```
Assets/JANOARG.Network/                 # git submodule — CONTRACTS ONLY
  Contracts/
    SyncMeta.cs                         # { Revision, UpdatedAt }
    SaveBlob.cs                         # shared encrypted container
    SaveSummary.cs                      # fields for the conflict prompt
    ISaveTransport.cs                   # abstraction (clipboard / file / http)
  JANOARG.Network.csproj                # netstandard2.1, no Unity, no ASP.NET
  JANOARG.Network.asmdef                # Unity-safe; Assembly-CSharp references it

Server/                                 # Cloudflare Worker (TS), repo root
  wrangler.toml                         # D1 + R2 bindings, routes
  package.json
  src/
    index.ts                            # router: /api/* local, /v1/* proxy
    routes/save.ts                      # GET/PUT/DELETE save
    routes/auth.ts                      # POST /api/auth/taptap
    proxy/spacetimedb.ts                # /v1/* -> maincloud (WS upgrade aware)
    db.ts                               # D1 helpers, revision CAS
  migrations/0001_init.sql

Assets/JANOARG.Client/Scripts/Network/  # Assembly-CSharp (needs Storage/ScoreStore)
  CloudSaveClient.cs                    # UnityWebRequest; HttpSaveTransport
  TapTapLoginBridge.cs
  CloudSaveConflictUI.cs                # shared with import's replace prompt
```

Notes:
- The repo currently has **no asmdefs** (everything is `Assembly-CSharp`).
  `Assembly-CSharp` auto-references asmdef assemblies, so the game can consume
  `JANOARG.Network`, but an asmdef **cannot** reference `Assembly-CSharp` — hence
  the client glue stays in the main game assembly because it touches `Storage`.
- The Worker must live **outside** `Assets/`, or Unity will try to compile the TS
  project's `.ts`/config (and any `.cs`) into the player build.

## 5. Server API

All under the Worker. Auth = `Authorization: Bearer <session>` (except `/auth`).

| Method | Path | Body | Response |
| --- | --- | --- | --- |
| POST | `/api/auth/taptap` | TapTap access token fields | `{ session, player }` |
| GET | `/api/save` | — | `200 {revision, updatedAt, data}` · `404` |
| PUT | `/api/save` | `{baseRevision, data}` | `200 {revision}` · `409 {revision, updatedAt, data}` |
| DELETE | `/api/save` | — | `204` |
| DELETE | `/api/account` | — | `204` (purge player + save) |
| GET | `/api/charts` | — | *(later)* catalog; blobs via R2 |
| POST | `/api/rt/token` | — | *(later)* signed SpacetimeDB connection info |
| `*` | `/v1/*` | — | reverse proxy → `https://maincloud.spacetimedb.com` |

- `PUT` uses optimistic concurrency on `revision`; a mismatch is **not an error to
  retry blindly** — it means another device wrote, so return `409` with the
  current blob and let the client show the prompt.
- Reserve `/v1/*` for SpacetimeDB: the SDK hardcodes `/v1/database/{db}/subscribe`
  relative to the connection URI, so our own REST must not live there.
- Maincloud's raw HTTP API reportedly returns `403`; the WebSocket subscribe path
  is the supported one. C# does not require `/v1/identity` (only the TS SDK does).

## 6. Data model (D1)

```sql
CREATE TABLE players (
  id         TEXT PRIMARY KEY,          -- server uuid
  unionid    TEXT UNIQUE,               -- TapTap: per developer (the player key)
  openid     TEXT,                      -- TapTap: per game
  created_at INTEGER NOT NULL
);

CREATE TABLE saves (
  player_id  TEXT PRIMARY KEY REFERENCES players(id),
  revision   INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  data       BLOB    NOT NULL,          -- shared encrypted blob
  bytes      INTEGER
);

-- Only if we go with revocable sessions instead of stateless JWTs.
CREATE TABLE sessions (
  token      TEXT PRIMARY KEY,
  player_id  TEXT NOT NULL,
  expires_at INTEGER NOT NULL
);
```

`PUT` flow: `UPDATE saves SET revision = revision + 1, ... WHERE player_id = ? AND revision = ?`
→ 1 row = success, 0 rows = conflict.

## 7. Client sync & conflict prompt

Local `Storage` metadata (new keys):

| Key | Meaning |
| --- | --- |
| `SYNC:BaseRevision` | last cloud revision we synced with |
| `SYNC:LastSyncUtc` | last successful sync |
| `SYNC:Dirty` | local changed since last sync (set in `Storage.Save()`) |
| `AUTH:SessionToken` | our server session |
| `AUTH:UnionId` | linked TapTap id (or unlinked = anonymous) |

Flow (after `Storage.Load()`, before menus):

1. `404` → upload local as revision 1.
2. `cloud.revision == BaseRevision` → push if `Dirty`, else no-op.
3. `cloud.revision > BaseRevision` and **not** dirty → fast-forward pull
   (snapshot local first).
4. cloud changed **and** dirty → **conflict prompt**. A `409` on push lands here too.

Conflict prompt:
- Show **both sides**: `updatedAt`, `INFO:Name`, `INFO:Level`, `CURR:Coins`,
  cleared-song count, top RKS (`ScoreStore`). Phigros's problem is players
  guessing on timestamps alone — give them real data.
- Options: Keep Cloud / Keep Local. Reuses the same UI component as import's
  replace prompt.
- **Snapshot the losing blob** to `save.conflict-<utc>.jas` before applying.

## 8. TapTap integration

- OAuth: interactive login button + silent login on launch. Scopes:
  `basic_info` (openid/unionid) or `public_profile` (name/avatar).
- **Server-side validation**: client sends the TapTap access token to
  `/api/auth/taptap`; the server calls
  `https://open.tapapis.com/account/basic-info/v1` with a MAC/HMAC-SHA1 signed
  request. **Never trust a client-supplied openid/unionid.**
- **Key on `unionid`, not `openid`.** A test build and the game may be two
  separate TapTap apps (different Client IDs) → different `openid`s for the same
  player, which would defeat cross-build retention. `unionid` is stable across all
  apps under one developer account, so **both variants must live under the same
  TapTap developer**.
- Unpublished development is allowed: become a **Verified Developer** (~2 business
  days), register Android package name + signing MD5 and/or iOS Bundle ID, use
  **Internal Test** with test TapTap IDs. The login UI is only reviewed when the
  app is listed. Mobile login requires the TapTap client installed.

## 9. Hosting, limits, self-hosting

Free-tier budgets (verify periodically — they change):

| Service | Free |
| --- | --- |
| Workers | 100k requests/day, 10 ms CPU/invocation |
| D1 | 5M rows read/day, 100k rows written/day, 5 GB total, no egress |
| R2 | 10 GB storage, 1M Class A + 10M Class B ops/month, no egress |
| Maincloud | ~1 GB storage, pauses after ~1 week idle (auto-resumes <1 s); Pro $25/mo adds backups |

Local testing (no account needed):
```
cd Server
npm i
npx wrangler d1 migrations apply janoarg-save --local
npx wrangler dev            # http://localhost:8787
```

Production self-hosting: `workerd` is open source and dockerable, but D1/R2 are
managed services — put storage behind `SaveStore`/`ChartStore` interfaces (D1/R2
impl + SQLite/S3 impl) if running off Cloudflare is a requirement.

## 10. Phases

0. **Contracts + local state** — `SyncMeta`, `SaveSummary`, `ISaveTransport`,
   `SYNC:*` keys. Depends on the shared blob from Save Import/Export.
1. **Worker + D1** — `/api/save` with a stub bearer token; verified against
   `wrangler dev`.
2. **TapTap auth** — `/api/auth/taptap`, real identity, anonymous→linked upgrade.
3. **Conflict UI + sync** — full end-to-end sync + prompt + backups.
4. **R2 charts** — catalog metadata in D1, blobs in R2.
5. **SpacetimeDB** — `/v1/*` proxy and/or `/api/rt/token` for direct realtime.

## 11. Open questions / risks

- **Session model**: stateless JWT vs `sessions` table (revocation needed for
  account deletion / `access_denied`).
- **Anonymous→link upgrade**: existing players have local progress and no
  account; first login must offer to bind/upload it, or the fix only helps new
  players.
- **Client-authoritative / tamper**: the embedded key makes the blob
  tamper-*evident*, not tamper-*proof* (see `SaveImportExportPlan.md` §4). Fine
  for single-player; revisit before any leaderboard.
- **Realtime path**: proxying `/v1/*` adds a hop and inherits Maincloud's
  shared-host limits; a dedicated subdomain (self-hosted SpacetimeDB behind
  Nginx) is the alternative if latency matters.
- **Free-tier drift**: Cloudflare/Maincloud terms change; keep storage abstracted.
- **Account deletion**: required for GDPR/PIPL; ensure `DELETE /api/account`
  purges identity + save.
