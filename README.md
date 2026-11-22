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
| Media storage | Cloudflare R2 *(Phase 9)* |
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

---

## Project Structure

```
AOS.Messenger/
├── AGENTS.md                         # Authoritative architectural rules & phase progress (read before contributing)
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
│       ├── Persistence/              # ApplicationDbContext, entity configs, migrations, repositories
│       └── DependencyInjection/      # InfrastructureServiceRegistration.cs
│
└── tests/
    └── AOS.Messenger.Tests/          # Unit, integration, architecture, and LocalDB persistence tests
```

---

## Development Roadmap & Status

Architectural invariants, phase definitions, and implementation requirements are maintained authoritatively in **[AGENTS.md](./AGENTS.md)**.

- **Current Status:** Phase 8 ✅ Complete (Message operations: edit, delete for me, delete for everyone, reply, reactions)
- **Next Up:** Phase 9 🔜 Next (Media messages and Cloudflare R2 integration)

