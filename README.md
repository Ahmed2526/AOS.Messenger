# AOS Messenger

A production-quality real-time messaging application built on .NET 10 and ASP.NET Core MVC, following Clean Architecture principles within a modular monolith.

---

## Architecture

### Style: Modular Monolith + Clean Architecture

The solution is a single deployable unit with clear layer separation. It is **not** a microservices system. Complexity is managed through explicit boundaries between layers, not through distributed infrastructure.

### Dependency Graph

```
┌─────────────────────┐
│   AOS.Messenger.Web │  ← ASP.NET Core MVC, Razor Views, SignalR Hubs (future)
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
| Views | Razor |
| ORM | Entity Framework Core |
| Database | SQL Server |
| Identity | ASP.NET Core Identity |
| Authentication | Cookie authentication |
| Realtime | SignalR *(future)* |
| Media storage | Cloudflare R2 *(future)* |
| Push notifications | Web Push *(future)* |
| Email | TBD *(future)* |
| Testing | xUnit |

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- SQL Server or SQL Server LocalDB (for development)

### Running Locally

```bash
# Restore and build
dotnet build

# Run the web application
dotnet run --project src/AOS.Messenger.Web

# Run tests
dotnet test
```

### Database Setup (future — after Phase 1)

```bash
# Apply migrations (run from solution root)
dotnet ef database update --project src/AOS.Messenger.Infrastructure --startup-project src/AOS.Messenger.Web
```

### Connection String

The development connection string in `appsettings.json` uses LocalDB:

```
Server=(localdb)\mssqllocaldb;Database=AOS.Messenger;Trusted_Connection=True;MultipleActiveResultSets=true
```

For production, supply the connection string via environment variable or user secrets — **never commit credentials**.

---

## Project Structure

```
AOS.Messenger/
├── AGENTS.md                         # Architectural rules (read before contributing)
├── README.md                         # This file
├── AOS.Messenger.sln
│
├── src/
│   ├── AOS.Messenger.Web/
│   │   ├── Controllers/              # Thin MVC controllers
│   │   ├── Views/                    # Razor views
│   │   ├── Extensions/               # Web-layer helper extensions
│   │   ├── wwwroot/                  # Static assets
│   │   └── Program.cs                # Composition root
│   │
│   ├── AOS.Messenger.Application/
│   │   ├── Common/                   # Shared exceptions, utilities
│   │   ├── Features/                 # Feature-oriented application services
│   │   └── DTOs/                     # Data transfer objects
│   │
│   ├── AOS.Messenger.Domain/
│   │   ├── Common/                   # Entity<T>, ValueObject base classes
│   │   ├── Entities/                 # Domain entities (added per phase)
│   │   ├── ValueObjects/             # Value objects
│   │   └── Rules/                    # Domain invariant classes
│   │
│   └── AOS.Messenger.Infrastructure/
│       ├── Persistence/              # DbContext, entity configs, migrations
│       └── DependencyInjection/      # InfrastructureServiceRegistration
│
└── tests/
    └── AOS.Messenger.Tests/          # xUnit tests
```

---

## Development Phases

| Phase | Status | Description |
|---|---|---|
| 0 — Foundation | ✅ Complete | Project shell, architecture, build green |
| 1 — Identity | 🔜 Next | User model, registration, login, cookie auth |
| 2 — Contacts | ⏳ | Friend requests, contact list |
| 3 — Messaging | ⏳ | Direct messages via SignalR |
| 4 — Media | ⏳ | File/image uploads via Cloudflare R2 |
| 5 — Notifications | ⏳ | Email, web push |

> See [AGENTS.md](./AGENTS.md) for full engineering rules and constraints.
