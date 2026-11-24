# AOS Messenger

A production-quality real-time messaging application built on .NET 10 and ASP.NET Core MVC, following Clean Architecture principles within a modular monolith.

---

## Architecture

### Style: Modular Monolith + Clean Architecture

The solution is a single deployable unit with clear layer separation. It is **not** a microservices system. Complexity is managed through explicit boundaries between layers, not through distributed infrastructure.

### Dependency Graph

```
┌─────────────────────┐
│   AOS.Messenger.Web │  ← ASP.NET Core MVC, Razor Views, SignalR Hubs
└─────────┬───────────┘
          │  references
          ▼
┌──────────────────────────────┐
│ AOS.Messenger.Application    │  ← Application services, DTOs, use cases, validation
└──────────────┬───────────────┘
               │  references
               ▼
┌──────────────────────────────┐
│   AOS.Messenger.Domain       │  ← Entities, value objects, domain rules (no dependencies)
└──────────────────────────────┘

┌──────────────────────────────┐
│ AOS.Messenger.Infrastructure │  ← EF Core, SQL Server, Identity, external adapters
└──────┬───────────────────────┘
       │  references
       ▼
  Application + Domain

Web also references Infrastructure (composition root only — to wire DI)
```

### Project Responsibilities

| Project | Role |
|---|---|
| `AOS.Messenger.Domain` | Pure domain model — entities, value objects, invariants. No framework dependencies. |
| `AOS.Messenger.Application` | Business workflows and use cases. Orchestrates domain. No EF Core, no HTTP. |
| `AOS.Messenger.Infrastructure` | EF Core DbContext, Identity, SQL Server, migrations, external service adapters. |
| `AOS.Messenger.Web` | Thin MVC controllers, Razor views, SignalR hubs. Composition root (`Program.cs`). |
| `AOS.Messenger.Tests` | xUnit tests covering domain logic and application services. |

---

## Technology Stack

| Concern | Technology |
|---|---|
| Runtime | .NET 10 |
| Web framework | ASP.NET Core MVC |
| Views | Razor (strict HTML encoded) |
| Frontend CSS | Tailwind CSS v4 (AOT compiled via pinned standalone CLI binary) |
| Frontend JS | Modular vanilla ES modules (`wwwroot/js/`) served via `MapStaticAssets()` |
| ORM | Entity Framework Core |
| Database | SQL Server |
| Identity | ASP.NET Core Identity (`IdentityUser<Guid>`) |
| Authentication | Cookie authentication (`__Host-` prefixed, persistent sessions) |
| Realtime | SignalR |
| Media storage | Cloudflare R2 (private bucket, pure S3 API; local-disk fallback in Development) |
| Push notifications | Web Push *(Phase 12)* |
| Testing | xUnit + real SQL Server LocalDB |

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- SQL Server or SQL Server LocalDB (`(localdb)\mssqllocaldb` for local development and test suite)

### Frontend CSS Compilation (Tailwind CSS v4 Standalone CLI)

Tailwind CSS v4 is compiled ahead-of-time using a pinned standalone CLI binary (no Node.js, `npm`, or `node_modules` required):

```powershell
# Windows (PowerShell)
./scripts/build-css.ps1

# Linux / macOS (Bash)
./scripts/build-css.sh
```

The script automatically downloads the pinned CLI binary into `.tools/` (git-ignored) and compiles `src/AOS.Messenger.Web/Styles/app.css` into `src/AOS.Messenger.Web/wwwroot/css/app.css`.

### Running Locally

```bash
# Restore and build
dotnet build

# Apply database migrations
dotnet ef database update --project src/AOS.Messenger.Infrastructure --startup-project src/AOS.Messenger.Web

# Run the web application
dotnet run --project src/AOS.Messenger.Web
```

### Running Tests

All architecture, unit, concurrency, and persistence tests run against real SQL Server LocalDB:

```bash
dotnet test
```

### Connection String

The development connection string in `appsettings.json` uses LocalDB:

```
Server=(localdb)\mssqllocaldb;Database=AOS.Messenger;Trusted_Connection=True;MultipleActiveResultSets=true
```

For production, supply the connection string via environment variables or user secrets — **never commit credentials**.

### Media Storage & Cloudflare R2 Setup (Pure S3 API)

AOS Messenger stores chat media in a **private** Cloudflare R2 bucket via the pure S3 API (`AWSSDK.S3`) in non-Development environments, with an automatic zero-config local-disk simulator in `Development`.

#### 1. Development Fallback (Zero-Config Local Mode)

When all `R2:*` settings are left unset in `Development`, `InfrastructureServiceRegistration` automatically registers `LocalDiskMediaStorage` storing files under `.dev-media/` (git-ignored; overrideable via `Media:LocalStoragePath`) and mounts AEAD `IDataProtectionProvider`-signed `/dev-media/upload` and `/dev-media/download` endpoints via `DevMediaStartupFilter`. Outside `Development`, `R2StorageOptionsValidator` fails fast on startup if any required `R2` key is missing or invalid.

#### 2. Cloudflare R2 Bucket & Least-Privilege API Token

1. Create a **private** R2 bucket in your Cloudflare dashboard.
2. Keep `r2.dev` public bucket access **disabled**, and do **not** attach a public R2 Custom Domain — S3 SigV4 presigned `PUT` / `GET` URLs validate against the S3 API endpoint (`https://<accountId>.r2.cloudflarestorage.com`), not a public custom domain.
3. Create an R2 API token scoped strictly to **Object Read & Write** on **this single bucket only** (never an account-wide Admin token).
4. AOS Messenger isolates uploads and verified objects under two bucket prefixes:
   - `pending/{storageObjectId}` — untrusted client staging area targeted by short-lived presigned `PUT` URLs.
   - `media/{storageObjectId}` — verified, immutable storage promoted server-side after magic-byte inspection (`Magick.NET` decompression-bomb checks + EXIF/GPS stripping on images; 4 KB header inspection on videos and documents).
5. **CSP & Active-Content Hardening:** `SecurityHeadersOptions` derives the allowed R2 origin for `connect-src`, `img-src`, and `media-src` strictly from `R2:ServiceUrl` (must be an exact `https://` origin with no path, query, fragment, userinfo, or wildcards). Active-content execution risks are mitigated via strict extension/MIME allowlists (SVG/HTML/JS/XML/executables are prohibited), server-side `Magick.NET` image re-encoding, and forced `Content-Disposition: attachment` on all documents.

#### 3. Exact-Origin Bucket CORS Policy

Configure CORS on the private R2 bucket to permit only your application's exact HTTPS origin (never `"*"`):

```json
[
  {
    "AllowedOrigins": ["https://messenger.example.com"],
    "AllowedMethods": ["PUT", "GET", "HEAD"],
    "AllowedHeaders": ["Content-Type", "Content-Length"],
    "ExposeHeaders": ["ETag"],
    "MaxAgeSeconds": 3600
  }
]
```

#### 4. `R2` and `Media` Configuration Reference

Supply `R2` credentials via environment variables (`R2__ServiceUrl`, `R2__BucketName`, `R2__AccessKeyId`, `R2__SecretAccessKey`) or user secrets — **never commit credentials to `appsettings.json`**.

```json
{
  "R2": {
    "ServiceUrl": "https://<accountId>.r2.cloudflarestorage.com",
    "BucketName": "aos-messenger-media",
    "AccessKeyId": "<single-bucket-access-key-id>",
    "SecretAccessKey": "<single-bucket-secret-access-key>"
  },
  "Media": {
    "MaxConcurrentPendingPerUser": 10,
    "DailyUploadQuotaBytes": 1073741824,
    "PresignedPutTtlMinutes": 5,
    "PresignedGetTtlMinutes": 10,
    "MaxImageProcessingConcurrency": 3,
    "ImageProcessingTimeoutSeconds": 15,
    "MaxAnimationFrames": 300,
    "MaxTotalDecodedImagePixels": 40000000,
    "StalePendingMinutes": 30,
    "UnattachedUploadedHours": 2,
    "OrphanGracePeriodHours": 1,
    "PurgedMetadataRetentionHours": 48,
    "CleanupIntervalMinutes": 15
  }
}
```

| Key | Default / Requirement | Description |
|---|---|---|
| `R2:ServiceUrl` | Required outside Dev | Exact `https://<accountId>.r2.cloudflarestorage.com` S3 API origin (used for S3 signing and CSP `connect-src`/`img-src`/`media-src`). |
| `R2:BucketName` | Required outside Dev | Private R2 bucket name (3–63 chars, no whitespace or slashes). |
| `R2:AccessKeyId` | Required outside Dev | S3 access key ID scoped to Object Read & Write on `R2:BucketName` only. |
| `R2:SecretAccessKey` | Required outside Dev | S3 secret access key paired with `R2:AccessKeyId`. |
| `Media:MaxConcurrentPendingPerUser` | `10` | Maximum simultaneous `Pending` upload records per user (messages are fixed at max 5 attachments in Domain `MediaConstraints`). |
| `Media:DailyUploadQuotaBytes` | `1073741824` | Rolling 24-hour per-user upload quota in bytes (1 GB default). |
| `Media:PresignedPutTtlMinutes` | `5` | Presigned staging `PUT` URL lifetime in minutes. |
| `Media:PresignedGetTtlMinutes` | `10` | Presigned download/stream `GET` URL lifetime in minutes. |
| `Media:MaxImageProcessingConcurrency` | `3` | Maximum concurrent server-side `Magick.NET` image sanitization jobs. |
| `Media:ImageProcessingTimeoutSeconds` | `15` | Timeout in seconds for image decode/re-encode sanitization. |
| `Media:MaxAnimationFrames` | `300` | Maximum animation frames permitted for GIF/WebP images. |
| `Media:MaxTotalDecodedImagePixels` | `40000000` | Maximum total decoded pixels (across frames) for images. |
| `Media:StalePendingMinutes` | `30` | Age threshold before abandoned `Pending` uploads expire to `Failed`. |
| `Media:UnattachedUploadedHours` | `2` | Age threshold before unattached `Uploaded` drafts transition to `Deleted`. |
| `Media:OrphanGracePeriodHours` | `1` | Grace period before untracked R2 objects are swept as orphans. |
| `Media:PurgedMetadataRetentionHours` | `48` (min `24`) | Retention window before purged `Media` SQL rows are hard-deleted (must remain >= 24h for daily quota accuracy). |
| `Media:CleanupIntervalMinutes` | `15` | Background interval for `MediaCleanupService` sweeps. |
| `Media:LocalStoragePath` | `<ContentRoot>/.dev-media` | Optional override for the Development local-disk media directory. |

---

## Project Structure

```
AOS.Messenger/
├── README.md                         # This file
├── AOS.Messenger.slnx                # Solution file
├── scripts/                          # Tailwind v4 standalone CLI compilation scripts
│   ├── build-css.ps1
│   └── build-css.sh
│
├── src/
│   ├── AOS.Messenger.Web/
│   │   ├── Controllers/              # Thin MVC controllers (delegate to Application)
│   │   ├── Views/                    # Razor views
│   │   ├── Hubs/                     # SignalR hubs (thin realtime push)
│   │   ├── Middleware/               # Exception handling, CSWSH protection
│   │   ├── wwwroot/                  # Static assets (MapStaticAssets)
│   │   └── Program.cs                # Composition root (wires DI, middleware)
│   │
│   ├── AOS.Messenger.Application/
│   │   ├── Common/                   # Exceptions, interfaces, contracts (IIdentityService, etc.)
│   │   ├── Services/                 # Application use-case services
│   │   ├── DTOs/                     # Feature-scoped Data Transfer Objects
│   │   └── ApplicationServiceRegistration.cs
│   │
│   ├── AOS.Messenger.Domain/
│   │   ├── Common/                   # Entity<T>, ValueObject base classes
│   │   ├── Entities/                 # Domain entities (Message, Conversation, UserBlock, etc.)
│   │   ├── ValueObjects/             # Strongly-typed IDs (UserId, ConversationId, MessageId)
│   │   ├── Exceptions/               # DomainException
│   │   └── Rules/                    # Domain invariant specifications
│   │
│   └── AOS.Messenger.Infrastructure/
│       ├── Identity/                 # ApplicationUser, IdentityService, CurrentUser, cleanup services
│       ├── Media/                    # Cloudflare R2 & Dev local-disk storage, Magick.NET verifier, cleanup worker
│       ├── Persistence/              # ApplicationDbContext, entity configs, migrations, repositories
│       └── DependencyInjection/      # InfrastructureServiceRegistration.cs
│
└── tests/
    └── AOS.Messenger.Tests/          # Unit, integration, architecture, and LocalDB persistence tests
```

---

## Development Roadmap & Status

- **Current Status:** Phase 9 ✅ Complete (Media messages and Cloudflare R2 integration)
- **Next Up:** Phase 10 🔜 Next (Voice messages)

