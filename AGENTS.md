# AOS Messenger — Architectural Rules

This file is the authoritative reference for all engineering decisions in this repository.
Every contributor (human or AI agent) **must** read and comply with these rules before making changes.

---

## Solution Structure

```
src/
    AOS.Messenger.Web/            # ASP.NET Core MVC — entry point
    AOS.Messenger.Application/    # Application services, use cases, DTOs
    AOS.Messenger.Domain/         # Domain entities, value objects, rules
    AOS.Messenger.Infrastructure/ # EF Core, SQL Server, Identity, external adapters

tests/
    AOS.Messenger.Tests/          # Unit and integration tests
```

---

## Dependency Direction (STRICT — never violate)

```
Web
 └─> Application
      └─> Domain

Infrastructure
 ├─> Application
 └─> Domain

Application
 └─> Domain

Domain
 └─> (nothing — no external dependencies)
```

**Forbidden cross-dependencies:**
- `Application` must NOT reference `Web`
- `Domain` must NOT reference `Infrastructure`, `Application`, or `Web`
- `Infrastructure` must NOT reference `Web`
- No circular references

---

## Source of Truth

SQL Server is the source of truth for persistent application state.

SignalR, in-memory presence state, browser state, and object storage must not replace SQL Server as the source of truth for their respective persistent metadata.

---

## Layer Responsibilities

### Domain (`AOS.Messenger.Domain`)
- Domain entities and aggregate roots
- Value objects
- Domain rules and invariants
- Domain-specific exceptions
- **No** ASP.NET Core, EF Core, or any infrastructure concern
- **No** `[Key]`, `[Required]`, or data annotation attributes from EF/Identity

### Application (`AOS.Messenger.Application`)
- Application services implementing use cases
- DTOs (input/output models scoped to features)
- Validation logic
- Authorization / application-level policy enforcement
- Feature-oriented workflows
- References `Domain` and only the framework/library abstractions that are genuinely required by the Application layer.
- **No** references to `Web` or `Infrastructure`
- **No** EF Core `DbContext` — never query the database directly

### Infrastructure (`AOS.Messenger.Infrastructure`)
- `ApplicationDbContext` (EF Core)
- Entity type configurations (`IEntityTypeConfiguration<T>`)
- ASP.NET Core Identity persistence
- Migrations (run from `Web` as startup project)
- Cloudflare R2 adapter
- Email adapter
- Web Push adapter
- Transactional outbox processor
- Contains infrastructure-specific implementations and technical orchestration such as EF Core persistence, Identity persistence, external service adapters, storage adapters, and background processing.
- **No** domain business rules — those belong in Domain
- **No** application use-case decisions — those belong in Application

### Web (`AOS.Messenger.Web`)
- MVC Controllers (thin — delegate immediately to Application services)
- Razor Views
- SignalR Hubs (thin — delegate to Application services)
- Authentication/authorization UI
- Static assets
- `Program.cs` — composition root; calls `AddApplicationServices()` and `AddInfrastructureServices()`
- **No** business logic in controllers, hubs, or views

---

## Coding Rules

### General
- Use `async`/`await` for all I/O operations — never `.Result` or `.Wait()`
- Pass `CancellationToken` through all I/O call chains
- Enable nullable reference types (`<Nullable>enable</Nullable>`) in all projects
- Use `implicit usings` — do not add redundant `using` directives
- Prefer `sealed` on classes that are not designed for inheritance
- Do not use `#region`

### Architecture
- Controllers must be thin: one call to an application service, then return a view or redirect
- SignalR Hubs must be thin: one call to an application service, then broadcast
- Business logic lives in Application services or Domain, never in controllers, hubs, or views
- Do not create a generic repository (`IRepository<T>`) — query EF Core directly inside Infrastructure services/repositories scoped to a feature
- Do not introduce MediatR, CQRS, event sourcing, or event bus unless explicitly approved
- Do not introduce Redis, Kafka, RabbitMQ, or any distributed infrastructure
- Do not add NuGet packages without a clear, documented reason

### Dependency Injection
- Each layer registers its own services via an extension method:
  - `AddApplicationServices()` in `AOS.Messenger.Application`
  - `AddInfrastructureServices(IConfiguration)` in `AOS.Messenger.Infrastructure`
- `Program.cs` calls these; it does not `new` up any services manually

### Error Handling
- Use typed exceptions defined in `Application.Common`: `NotFoundException`, `ForbiddenException`, `DomainException`
- Prefer centralized exception handling middleware to translate application and domain exceptions into appropriate HTTP responses — do not require individual controllers to contain repetitive try/catch blocks
- Controllers must remain thin; exception translation is not a controller responsibility
- Never swallow exceptions silently

### Database
- Migrations are managed from the `Web` project (startup project) pointing at `Infrastructure` (DbContext project)
- Connection strings live in `appsettings.json` (dev: LocalDB) or environment variables / user secrets (production)
- Never hard-code credentials

---

## Technology Stack

| Concern | Technology |
|---|---|
| Runtime | .NET 10 |
| Web framework | ASP.NET Core MVC |
| Views | Razor |
| Realtime | SignalR |
| ORM | Entity Framework Core |
| Database | SQL Server |
| Identity | ASP.NET Core Identity |
| Auth | Cookie authentication |
| Media storage | Cloudflare R2 |
| Push notifications | Web Push |
| Background processing | Hosted `BackgroundService` |

**Not used (do not introduce):**
- MediatR / CQRS
- Redis / Kafka / RabbitMQ
- Event sourcing
- Generic repository pattern
- Microservices

---

## Realtime Architecture

- SignalR is a realtime transport mechanism, not a persistence mechanism.
- SQL Server is the source of truth. Messages and application state must be persisted to SQL Server before any realtime broadcast.
- Messages must be successfully persisted and committed to the database before being broadcast through SignalR.
- SignalR hubs must remain thin and delegate all application operations to Application services.
- V1 uses a single application instance/server.
- Presence and active SignalR connection tracking may use in-memory application state because a single server instance is assumed.
- Do not introduce Redis or another distributed coordination mechanism.

---

## Messaging Integrity

- Every client-created message must carry a client-generated idempotency identifier.
- Message creation must be idempotent: duplicate requests using the same idempotency identifier must not create duplicate messages.
- Server-side authorization must verify that the authenticated user is a participant in the target conversation before any read or write operation.
- Never trust client-provided sender identity, user identity, or conversation ownership information.
- Message ordering must be deterministic and server-controlled.
- Client timestamps must never be treated as authoritative for message ordering.
- The server/database must assign the authoritative message creation timestamp.
- Message queries must use a deterministic ordering strategy when timestamps are equal.

---

## Message Rules

- **Edit**: stores the current content and an `EditedAt` timestamp. No edit history is kept in V1.
- **Delete for me**: per-user soft deletion; the message remains visible to other participants.
- **Delete for everyone**: permitted only by the message sender, and only within 3 hours of the message's creation timestamp.
- **Delete for everyone — tombstone**: deleted messages must preserve conversation ordering using a tombstone/deleted-message representation rather than immediate physical deletion.
- **Clear chat**: per-user clear boundary; does not affect other participants.
- **Conversation deletion**: per-user soft deletion. A conversation deleted by one user reappears for that user when the other participant sends a new message.
- Messages have no automatic expiration or retention policy in V1.

---

## Account Rules

- Account deletion is a soft-delete operation.
- Deleted accounts have a 30-day recovery period before permanent deletion.
- Username cannot be changed after registration.
- Email cannot be changed after registration.
- Session/device management is included in V1.
- Users can view their active sessions and log out individual sessions or all other sessions.

---

## User Discovery

User discovery is performed by username or email. There is no friendship or contact system in V1.

---

## Conversation Scope

V1 supports one-to-one (direct) conversations only. Group conversations are not in scope for V1.

---

## Blocking Rules

When user A blocks user B:
- A and B cannot send messages to each other.
- A and B do not appear in each other's user search results.
- Typing indicators are disabled between A and B.
- Delivery and read receipts are disabled between A and B.
- Presence and online status are hidden between A and B.
- Last seen is hidden between A and B.
- Existing conversations and messages remain stored; blocking does not delete history.

---

## Search Rules

- Conversation search (by participant name or username) is included in V1.
- Message-content search is NOT included in V1.

---

## Media Rules

### Storage
- Cloudflare R2 stores actual media objects.
- SQL Server stores media metadata (file name, MIME type, size, R2 key, upload status, etc.).
- R2 credentials and secrets must never be exposed to the browser.
- R2 objects use private access; access is controlled via server-signed URLs where appropriate.

### Limits and Allowed Types

| Type | Maximum size | Allowed formats |
|---|---|---|
| Images | 15 MB | JPEG, PNG, WebP, GIF |
| Videos | 70 MB | MP4, WebM, MOV |
| Documents | 50 MB | PDF, DOC, DOCX, XLS, XLSX, PPT, PPTX, TXT, CSV, ZIP |
| Voice messages | 20 minutes | Browser native recording format |

- Media limits and MIME types must be validated server-side. Client-side validation is supplementary only.
- Voice messages use the browser's native recording format. No server-side audio transcoding in V1.

---

## Security Rules

V1 does **NOT** implement end-to-end encryption.

The following claims must **never** appear anywhere in the application UI, marketing copy, or code comments:
- "End-to-End Encrypted"
- "End-to-End Encryption"
- "Encrypted Session"
- "Verified encrypted session"
- Any equivalent claim implying end-to-end encryption

The application uses HTTPS/TLS in transit, secure cookie authentication, server-side authorization, protected Cloudflare R2 storage with private access, and appropriate secret management.

---

## Notification Architecture

V1 is browser-only. Native mobile push is not in scope.

Use Web Push for offline and background notifications.

**Notification behavior by user state:**

| User state | Notification behavior |
|---|---|
| Viewing the active conversation | No push notification |
| Elsewhere in the application | Browser notification |
| Offline | Web Push |
| Conversation is muted | No notification |
| Sender is blocked | No notification |

Multiple Web Push subscriptions may exist for one user (multiple devices/browsers). All active subscriptions for a user must be targeted.

---

## Transactional Outbox

- Critical asynchronous work — in particular Web Push notifications — must use a durable SQL Server-backed outbox.
- Outbox records are written in the same database transaction as the triggering operation.
- Do not rely on an in-memory queue for work that must survive application restarts.
- Do not introduce Kafka, RabbitMQ, Redis, or another message broker.
- A hosted `BackgroundService` processes the outbox on a polling interval.

---

## Phases

| Phase | Status | Description |
|---|---|---|
| Phase 0 | ✅ Complete | Project shell and architecture foundation |
| Phase 1 | 🔜 Next | Domain model and database foundation |
| Phase 2 | ⏳ | ASP.NET Core Identity, registration, email verification, login, logout, password reset |
| Phase 3 | ⏳ | User profiles, user discovery, blocking, and conversation creation |
| Phase 4 | ⏳ | Conversation persistence and conversation list |
| Phase 5 | ⏳ | Text messaging and message idempotency |
| Phase 6 | ⏳ | SignalR realtime delivery |
| Phase 7 | ⏳ | Delivery/read receipts, typing, presence, and last seen |
| Phase 8 | ⏳ | Message operations: edit, delete for me, delete for everyone, reply, reactions |
| Phase 9 | ⏳ | Media messages and Cloudflare R2 |
| Phase 10 | ⏳ | Voice messages |
| Phase 11 | ⏳ | Conversation features: search, pin, archive, mute, clear, delete |
| Phase 12 | ⏳ | Web Push notifications and transactional outbox |
| Phase 13 | ⏳ | Session/device management |
| Phase 14 | ⏳ | Security hardening and authorization review |
| Phase 15 | ⏳ | Automated testing and integration testing |
| Phase 16 | ⏳ | Performance, observability, and production readiness |
| Phase 17 | ⏳ | Production deployment |

> **Do not implement a later phase before the current phase is explicitly approved.**
