# AOS Messenger — Architectural Rules

This file is the authoritative reference for all engineering decisions in this repository.
Every contributor (human or AI agent) **must** read and comply with these rules before making changes.

---

## Solution Structure

```
src/
    AOS.Messenger.Web/            # ASP.NET Core MVC — entry point and composition root
    AOS.Messenger.Application/    # Application services, use cases, DTOs, contracts
    AOS.Messenger.Domain/         # Domain entities, value objects, rules, domain exceptions
    AOS.Messenger.Infrastructure/ # EF Core, SQL Server, Identity, external adapters

tests/
    AOS.Messenger.Tests/          # Unit, architecture, integration, and concurrency tests
```

---

## Dependency Direction (STRICT — never violate)

```
Web
 ├──> Application
 └──> Infrastructure   (composition root only — see rules below)

Infrastructure
 ├──> Application
 └──> Domain

Application
 └──> Domain

Domain
 └──> (nothing — no external dependencies)
```

### Rules

- `Web.Program.cs` (the composition root) may reference Infrastructure to call `AddInfrastructureServices()` and configure EF Core migration tooling. This is the **only** permitted use of Infrastructure from Web.
- All other Web code — controllers, hubs, views, middleware, static assets — must depend only on Application abstractions and Domain types. Web presentation code must **NOT** depend on Infrastructure types.
- Controllers, SignalR hubs, and views must not instantiate or directly reference Infrastructure services or types.
- `Application` must **NOT** reference `Web` or `Infrastructure`.
- `Domain` must **NOT** reference `Application`, `Infrastructure`, or `Web`.
- `Infrastructure` must **NOT** reference `Web`.
- No circular references between any projects.
- Dependency direction must be enforced mechanically via automated architecture tests in `AOS.Messenger.Tests`.

---

## Source of Truth

| Store | Role |
|---|---|
| SQL Server | Authoritative persistent application state and metadata |
| Cloudflare R2 | Authoritative binary object storage (media files) |
| SignalR | Realtime transport — not persistence, not source of truth |
| Browser state | Presentation and local UI state only |
| In-memory server state | Ephemeral runtime state only (presence, connection tracking, session cache) |

- SQL Server is authoritative for all persistent application metadata: messages, conversations, user state, media metadata, outbox records, sessions, blocks, push subscriptions, etc.
- R2 is authoritative for the binary content of media objects. SQL Server stores the corresponding metadata (R2 object key, server-verified MIME type, size, upload lifecycle state).
- In-memory state must be treated as ephemeral and reconstructable. Application correctness must not depend on in-memory state surviving a restart.

---

## Layer Responsibilities

### Domain (`AOS.Messenger.Domain`)

- Domain entities and aggregate roots
- Value objects (including strongly-typed identifiers such as `UserId`)
- Domain rules and invariants
- Domain-specific exceptions (`DomainException`)
- **No** ASP.NET Core, EF Core, or any infrastructure concern
- **No** `[Key]`, `[Required]`, or data annotation attributes from EF Core or Identity
- **No** dependency on `IdentityUser<Guid>`, `ApplicationUser`, `IdentityRole<Guid>`, or any ASP.NET Core Identity type

### Application (`AOS.Messenger.Application`)

- Application services implementing use cases
- DTOs (input/output models scoped to features)
- Contracts/interfaces that define capabilities required by use cases (see Persistence Abstraction Boundary)
- Validation logic and input bounds enforcement
- Authorization / application-level policy enforcement
- Application-specific exceptions (`NotFoundException`, `ForbiddenException`, `ConflictException`)
- Feature-oriented workflows
- References `Domain` and only framework/library abstractions that are genuinely required
- **No** references to `Web`, `Infrastructure`, `ApplicationDbContext`, or any EF Core type

### Infrastructure (`AOS.Messenger.Infrastructure`)

- `ApplicationDbContext` (EF Core) and entity type configurations (`IEntityTypeConfiguration<T>`)
- ASP.NET Core Identity persistence (`ApplicationUser : IdentityUser<Guid>`, `IdentityRole<Guid>`)
- Implementations of Application-owned contracts using EF Core, SQL Server, and external services
- Database transaction ownership for multi-record atomic persistence operations
- Migrations (run from `Web` as startup project)
- Cloudflare R2 adapter (staging promotion, bounded range reads, EXIF stripping)
- Email adapter (HTML-encoded templates, `PublicBaseUrl` link generation)
- Web Push adapter (SSRF-hardened `SocketsHttpHandler.ConnectCallback` + host allowlist)
- `ICurrentUser` implementation (extracts authenticated `UserId` and `SessionId` from `HttpContext`)
- Transactional outbox processor (`BackgroundService`)
- Media orphan cleanup and unverified/expired account cleanup processors (`BackgroundService`)
- **No** domain business rules (belong in Domain) and **no** application use-case decisions (belong in Application)

### Web (`AOS.Messenger.Web`)

- MVC Controllers and JSON endpoints (thin — handle all persistent state mutations, delegate to Application services, publish outbound realtime updates after commit)
- Razor Views (server-rendered, strict encoding)
- SignalR Hubs (thin — server-to-client push and throttled ephemeral upstream events only)
- Authentication/authorization UI
- Static assets (compiled static `wwwroot/css/app.css` via Tailwind CSS v4 standalone CLI and modular vanilla ES modules under `wwwroot/js/`, served via `MapStaticAssets()`)
- Centralized exception-handling middleware, security/cache headers middleware, `ForwardedHeaders` hardening, and rate-limiting policies
- `Program.cs` — composition root; calls `AddApplicationServices()` and `AddInfrastructureServices()`
- **No** business logic in controllers, hubs, or views

---

## Persistence Abstraction Boundary

Application owns the contracts (interfaces) representing the persistence and external-service capabilities it requires. Infrastructure implements those contracts.

### Application-owned contracts (illustrative, not exhaustive)

| Contract category | Examples |
|---|---|
| Persistence | `IMessageReader`, `IMessageWriter`, `IConversationReader`, `IConversationWriter`, `IUserReader` |
| Identity | `ICurrentUser` — returns the authenticated user's domain-neutral `UserId` and `SessionId` without exposing Identity types |
| Media | `IMediaStorage` — presigned upload/download URL generation, bounded verification reads, staging-to-permanent promotion, deletion |
| Notifications | Web Push dispatch abstraction |
| Other | Only where a meaningful boundary exists between Application and Infrastructure |

### Rules

- Application must never reference `ApplicationDbContext`, EF Core, or Infrastructure types.
- Infrastructure queries EF Core directly inside feature-oriented persistence implementations. No generic `IRepository<T>`.
- Introduce an abstraction only when it creates a meaningful architectural boundary or isolates an external dependency. Do **NOT** create interfaces for every entity, simple service, DTO, or helper.
- Feature-scoped contracts are preferred over broad generic contracts (e.g., `IMessageReader` over `IRepository<Message>`).

### Transaction Boundaries

Application does not own `DbContext`, and ceremonial Unit of Work abstractions (`IUnitOfWork`) are prohibited. Atomicity across related writes is guaranteed through **operation-scoped persistence contracts**:

- When a use case requires multiple database writes to commit atomically (e.g., persisting a new message, verifying/attaching media, updating conversation timestamps/un-delete visibility, and inserting a transactional outbox record), expose a single cohesive contract method on the feature's writer interface (e.g., `IMessageWriter.CreateMessageWithOutboxAsync(...)`).
- Infrastructure executes all writes for that method within a single `DbContext` transaction / single `SaveChangesAsync()` call.
- Never split an atomic workflow across separate writer interfaces that each call `SaveChangesAsync()` independently.
- If a cross-feature workflow genuinely requires orchestrating multiple persistence contracts atomically, introduce a minimal `ITransactionExecutor` abstraction in Application (implemented in Infrastructure over `DbContext.Database.BeginTransactionAsync`) rather than leaking `DbContext` or inventing `IUnitOfWork`.

---

## Identity and Domain Boundary

ASP.NET Core Identity is an Infrastructure concern. Domain entities must remain completely independent of Identity types.

### Rules

- `ApplicationUser` must extend **`IdentityUser<Guid>`** (with **`IdentityRole<Guid>`** and `IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>`) so SQL Server `AspNetUsers.Id` is a native `uniqueidentifier` matching domain foreign keys cleanly — never use default string-keyed `IdentityUser`.
- `ApplicationUser` exists only in Infrastructure.
- Domain represents users through a strongly-typed value object: `UserId` (wrapping `Guid`).
- Domain entities (`Message`, `Conversation`, `Block`, etc.) reference `UserId`, not `ApplicationUser`.
- Infrastructure maps between Identity's `ApplicationUser` and `UserId`.
- `ICurrentUser` (owned by Application, implemented by Infrastructure) exposes `UserId` and `SessionId` without leaking Identity types.
- Do **NOT** place Identity types or EF Core/Identity NuGet packages in Domain.

---

## Technology Stack

| Concern | Technology |
|---|---|
| Runtime | .NET 10 |
| Web framework | ASP.NET Core MVC |
| Views | Razor |
| Frontend CSS | Tailwind CSS v4 compiled ahead-of-time via pinned standalone CLI (no Node.js, no runtime CDN) to static `wwwroot/css/app.css` |
| Frontend JS | Modular vanilla ES modules (`wwwroot/js/`), served via `MapStaticAssets()` |
| Realtime | SignalR |
| ORM | Entity Framework Core |
| Database | SQL Server |
| Identity | ASP.NET Core Identity (`IdentityUser<Guid>`) |
| Auth | Cookie authentication (`__Host-` prefixed) |
| Media storage | Cloudflare R2 (private bucket) |
| Email | SMTP or transactional email service |
| Push notifications | Web Push (Push API + Service Worker) |
| Background processing | Hosted `BackgroundService` |

**Not used (do not introduce):**

- MediatR / CQRS
- Redis / Kafka / RabbitMQ
- Event sourcing
- Generic repository pattern (`IRepository<T>`)
- Unit of Work as a ceremonial abstraction
- Microservices
- Distributed locks
- Kubernetes (V1 is single-server)
- Frontend SPA frameworks (React, Angular, Vue), Node.js / npm frontend build chains, jQuery, Bootstrap, or Tailwind Play CDN

---

## Coding Rules

### General

- Use `async`/`await` for all I/O operations — never `.Result` or `.Wait()`
- Pass `CancellationToken` through all I/O call chains
- Enable `<Nullable>enable</Nullable>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in `Directory.Build.props` across all projects
- Use `implicit usings` — do not add redundant `using` directives
- Prefer `sealed` on classes not designed for inheritance
- Do not use `#region`
- Do not add NuGet packages without a clear, documented reason

### Architecture

- Controllers and SignalR hubs must be thin and contain no business, authorization, or data-access logic
- Mutation operations delegate to a single focused Application use case
- SignalR hub publishing must happen only after the authoritative SQL Server transaction commits
- Business logic lives in Application services or Domain, never in controllers, hubs, or views

### Dependency Injection

- Each layer registers its own services via an extension method:
  - `AddApplicationServices()` in `AOS.Messenger.Application`
  - `AddInfrastructureServices(IConfiguration)` in `AOS.Messenger.Infrastructure`
- `Program.cs` calls these; it does not `new` up services manually

### Error Handling

**Domain exceptions** (`AOS.Messenger.Domain`):
- `DomainException` — a domain invariant or business rule was violated

**Application exceptions** (`AOS.Messenger.Application.Common`):
- `NotFoundException` — a requested resource does not exist
- `ForbiddenException` — the authenticated user is not permitted to perform the action
- `ConflictException` — the operation conflicts with existing state (idempotency conflict, uniqueness violation, optimistic concurrency failure)

**Rules:**
- Infrastructure must not leak raw infrastructure exceptions (`SqlException`, `HttpRequestException`) into Application or Web; wrap or translate them into appropriate application exceptions.
- Web uses centralized exception-handling middleware to map domain and application exceptions to HTTP responses. Do not add try/catch blocks in controllers for exception-to-HTTP mapping.
- **Production error scrubbing:** In non-development environments, centralized exception middleware must strip stack traces, inner exceptions, SQL constraint/table names, and internal file paths from all `ProblemDetails` and JSON error responses.
- Never swallow exceptions silently.

### Logging and Redaction

- Use structured logging (`ILogger<T>`) with message templates (never string interpolation inside log calls).
- **Never log sensitive data or user content**, including:
  - Message bodies or reply snippets
  - Passwords, password hashes, reset/recovery tokens, cookies, or session tokens
  - R2 presigned URLs or storage credentials
  - Raw user emails or personal profile fields (log `UserId`, `ConversationId`, `MessageId`, or `SessionId` instead)
- Never serialize entire request/response DTOs into logs (`{@Request}`) when they contain user-generated text or credentials.

### Database, Primary Key Strategy, and Raw SQL Safety

- Migrations are managed from `Web` (startup project) pointing at `Infrastructure` (`DbContext` project).
- Connection strings live in `appsettings.json` (dev: LocalDB) or environment variables / user secrets (production). Never hard-code credentials.
- Queries identified in Application contracts must have corresponding database indexes. Infrastructure owns index design.
- **Clustered Primary Key Strategy (SQL Server–optimized):**
  - **High-volume append-only tables** (`Messages`, `OutboxMessages`): database-generated `long` (`bigint IDENTITY`) as clustered primary key. Guarantees monotonic insertion order, zero page-split fragmentation, and clean deterministic tie-breaking.
  - **`Guid`-keyed entities** (`ApplicationUser`, `Conversation`, `Media`, `UserSession`, `Block`, `PushSubscription`): SQL Server `NEWSEQUENTIALID()` defaults (or a SQL Server–compatible sequential GUID generator) for clustered `uniqueidentifier` keys.
  - **Never use random `Guid.NewGuid()` (UUIDv4) or big-endian RFC 9562 `Guid.CreateVersion7()` as a SQL Server clustered primary key** (neither sorts sequentially under SQL Server's `uniqueidentifier` byte-order rules).
- **Raw SQL parameterization (STRICT):** Wherever raw SQL is required (e.g., Outbox `UPDLOCK, READPAST` claiming), never use `FromSqlRaw` / `ExecuteSqlRaw` with string concatenation or C# `$""` interpolation. Use EF Core `FormattableString` APIs (`FromSqlInterpolated` / `ExecuteSqlInterpolatedAsync`) or explicit `SqlParameter` instances exclusively.

---

## Frontend Conventions

- **Views:** Server-rendered Razor Views with strict Razor HTML encoding (never use `@Html.Raw` on user-generated content).
- **CSS (Compiled Tailwind v4 Standalone CLI):**
  - Use Tailwind CSS v4 compiled ahead-of-time via the standalone CLI binary (no Node.js, no `npm`, no `node_modules`) from `src/AOS.Messenger.Web/Styles/app.css` (`@import "tailwindcss" source("..");`) to a single minified static file `src/AOS.Messenger.Web/wwwroot/css/app.css` served from `'self'`.
  - Pin the exact Tailwind CLI version via a lightweight setup script / MSBuild target storing the binary in a git-ignored `.tools/` directory so builds remain identical across developer machines and CI/CD.
  - Never use the Tailwind Play CDN `<script>` tag, Bootstrap, or competing CSS frameworks.
- **Form Validation (No jQuery):**
  - Delete default `wwwroot/lib/jquery*` and `wwwroot/lib/bootstrap*` directories.
  - Rely on authoritative server-side `ModelState` validation paired with native HTML5 constraint validation attributes (`required`, `minlength`, `maxlength`, `pattern`, `type="email"`) and optional minimal self-hosted vanilla JS for UX feedback.
- **Modular JavaScript:**
  - Use vanilla ES modules organized **one feature per module** under `wwwroot/js/` (e.g., `signalr-client.js`, `message-renderer.js`, `conversation-list.js`, `media-uploader.js`, `voice-recorder.js`) to prevent monolithic scripts. Use JSDoc annotations for DTOs and function signatures where helpful.
  - Do not embed inline `<script>` blocks or inline event handlers (`onclick="..."`) in Razor views. Pass initial view state safely via `data-*` attributes or `<script type="application/json">` read via `JSON.parse(el.textContent)`.
  - Do not introduce React, Angular, Vue, TypeScript build toolchains, or jQuery unless explicitly approved.
- **Static Asset Delivery:** Serve static assets in `wwwroot/` via ASP.NET Core's `MapStaticAssets()` for built-in compression and cache-busting fingerprinting.
- **DOM & State Separation:** Keep SignalR connection handling and client-side state reconciliation cleanly separated from DOM rendering modules.
- **Safe DOM Updates:** Always render user-generated text (messages, display names, usernames, file names) via `textContent` or `document.createTextNode()`. Never assign user-generated content to `innerHTML`, `outerHTML`, or `insertAdjacentHTML()`.
- **No unsafe auto-linkification:** Do not auto-convert URLs in messages into clickable `<a>` elements in V1 (or if explicitly approved later, allowlist strictly `http:` and `https:` schemes parsed via the browser `URL` API with `rel="noopener noreferrer"` and `target="_blank"` — never `javascript:` or `data:`).
- **Service Worker scope:** The Phase 12 Service Worker must handle `push` and `notificationclick` events only. Never cache authenticated HTML pages, JSON API responses, presigned URLs, or media binaries in browser Cache Storage.

---

## Time Handling

All persisted timestamps and time-based business rules use UTC.

### Rules

- All server-generated timestamps must use UTC (`DateTimeOffset` stored with `+00:00` offset / SQL Server `datetimeoffset`).
- Client local time is for presentation only. Client clocks are never trusted for security or business decisions.
- Time-window business rules (3-hour delete-for-everyone window, 30-day account recovery period, session expiration) use server-side UTC exclusively via injected `TimeProvider` (.NET 10) for deterministic testability.
- Never mix local and UTC semantics in persisted data.

---

## Concurrency

Application correctness must hold under concurrent requests, not only sequential requests.

### Rules

- Use database constraints (unique indexes, foreign keys, composite foreign keys, check constraints) as the primary enforcement mechanism for invariants under concurrency.
- Application-level "check then act" patterns are insufficient alone for conversation uniqueness, message idempotency, or media attachment ownership.
- Use optimistic concurrency (`rowversion` / concurrency token) selectively where edit-vs-delete or simultaneous-update races can corrupt data.
- Handle unique-constraint violations gracefully (e.g., returning the existing conversation on duplicate direct-conversation creation).
- The outbox processor must handle concurrent processing attempts and duplicate delivery gracefully.

### Important concurrency scenarios

- Concurrent creation of the same direct conversation
- Duplicate message submission (same idempotency key)
- Simultaneous message edit and delete-for-everyone
- Concurrent attempts to attach the same uploaded media item
- SignalR WebSocket handshake racing against session revocation
- Account deletion or block creation during active message sending
- Concurrent read-state updates from multiple devices
- Outbox processing retries and overlapping background ticks

---

## Realtime Architecture

### Principles

- SignalR is a realtime transport mechanism, not a persistence mechanism.
- SQL Server is the source of truth. Messages and state must be committed to SQL Server before any realtime broadcast.
- V1 uses a single application server instance. Do not introduce Redis or distributed backplanes.

### HTTP vs. SignalR Boundary

- **All persistent state mutations go through HTTP `POST`/`PUT`/`DELETE` endpoints** (send message, edit, delete, reactions, read receipts, conversation actions, blocking, media uploads). This guarantees consistent anti-forgery (CSRF) validation, HTTP rate limiting, and centralized exception middleware. After the SQL transaction commits, the controller publishes outbound realtime events via `IHubContext` (or a thin realtime publisher).
- **SignalR hubs handle server-to-client push and lightweight ephemeral upstream events only** (typing indicators, active conversation focus, presence heartbeat).
- Ephemeral hub invocations must be throttled via an `IHubFilter` so clients cannot flood the server over an open WebSocket.

### SignalR Security and Connection Rules

- SignalR hubs must remain thin and contain no business logic.
- SignalR hub connections require authentication; unauthenticated attempts are rejected.
- **CSWSH protection:** Enforce strict same-origin `Origin` header validation on SignalR negotiate and WebSocket connections. Never configure `AllowAnyOrigin()` with credentials.
- **Register-then-verify (prevents connect-vs-revoke race):** In `OnConnectedAsync`, register the connection under its `(UserId, SessionId)` in the in-memory tracker *first*, then re-verify against SQL Server that the `SessionId` is still active and unexpired. If revoked or expired, unregister and abort immediately.
- **Per-invocation session validation:** Because `OnValidatePrincipal` does not run on subsequent WebSocket frames, the `IHubFilter` must verify active/unexpired `SessionId` status (via the session cache) on every hub invocation and abort expired/revoked connections.
- **No static conversation groups for security-sensitive broadcasts:** Target realtime events to specific recipient `UserId` connections after verifying live conversation membership and live block status, preventing a newly blocked user from receiving typing indicators, receipts, presence, or messages over a stale group subscription.
- Presence and active SignalR connection tracking use in-memory state (single-server V1), capped per user (see Resource Bounds).
- SignalR connections are tied to their originating `SessionId` so revoking an individual session immediately aborts only that session's connections.

### Failure Semantics

If the SQL transaction commits but SignalR broadcast fails:
- SQL Server remains authoritative; the message is durable.
- SignalR delivery is best-effort. Clients reconcile missed events by querying authoritative SQL state on reconnect, page load, or recovery.
- Do **NOT** introduce a message broker or retry queue for failed broadcasts in V1.

---

## Messaging Integrity

### Authorization

- Server-side authorization must verify that the authenticated user is a participant in the target conversation before any read or write, and that neither participant has blocked the other before any write.
- Never trust client-provided sender identity, user identity, or conversation ownership.
- Every message-scoped operation (reply, reaction, `DeleteForMe`, edit, delete-for-everyone, read receipt) must verify `Message.ConversationId == TargetConversationId` and active membership — never look up sequential `bigint` `Message.Id` values globally without conversation scoping.

### Idempotency

Every client-created message must carry a client-generated idempotency key.

- **Scope of uniqueness:** Unique per sender via SQL Server unique constraint on `(SenderId, IdempotencyKey)`.
- **Request equivalence fingerprint:** Evaluated against the full semantic payload — `(ConversationId, Content, ReplyToMessageId, MediaIds)` (or a deterministic hash thereof), not text alone.
- **Behavior:**
  - **First submission:** Created and persisted normally.
  - **Duplicate submission** (same sender, key, and identical semantic payload): Return already-persisted message without re-broadcasting realtime events.
  - **Conflicting submission** (same sender and key, different conversation, text, reply target, or media): Reject with `ConflictException`.
  - **Concurrent duplicate submissions:** Database unique constraint prevents duplicates; the losing request catches the constraint violation, loads the persisted message, verifies equivalence, and returns it (or throws `ConflictException`).
- Never rely solely on application-level "check then insert."

### Message Ordering and Pagination

- Deterministic total ordering key: `(CreatedAtUtc, Id)` where `CreatedAtUtc` is server-assigned UTC and `Id` is the monotonic `bigint IDENTITY` primary key.
- Client timestamps are never trusted for ordering.
- History queries must use cursor-based (keyset) pagination on `(CreatedAtUtc, Id)` with a server-enforced `pageSize` cap (see Resource Bounds). Never use offset-based pagination.

---

## Message Rules

- **Edit**: Permitted only by the sender. Updates text content only (media attachments cannot be added, removed, or swapped) and sets `EditedAtUtc`. Tombstoned messages cannot be edited. No edit history in V1.
- **Delete for me**: Per-user soft deletion; message remains visible to the other participant.
- **Delete for everyone**: Permitted only by the sender within 3 hours of `CreatedAtUtc` (server-side UTC via `TimeProvider`).
- **Delete for everyone — tombstone & SQL scrubbing**: Preserve message ordering with a tombstone row rather than physical row deletion. On execution, **physically `NULL` out `Content` in SQL Server** and update any cached conversation `LastMessagePreview` so deleted text cannot leak through secondary queries, reply snippets, or conversation list previews.
- **Reply (schema-enforced conversation scope)**: A message may reference a parent message within the **same conversation only**. Enforce both in Application validation and in SQL Server via a unique index on `Messages(ConversationId, Id)` and a composite self-referencing foreign key `(ConversationId, ReplyToMessageId)`. If the parent is tombstoned, display a "deleted message" reference.
- **Reactions**: Architectural design deferred to Phase 8 (must enforce conversation-scoped message authorization).
- **Clear chat / Conversation deletion**: Per-user state only (see Per-User Conversation State).
- Messages have no automatic expiration or retention policy in V1.

---

## Conversation Scope and Uniqueness

V1 supports one-to-one (direct) conversations only. Group conversations are out of scope.

### Rules

- At most one direct conversation may exist between any two users A and B, enforced in SQL Server via canonical ordering: store the smaller `UserId` in `Participant1Id` and the larger in `Participant2Id` (using SQL Server `uniqueidentifier` byte comparison order), with a unique constraint on `(Participant1Id, Participant2Id)` and a check constraint `Participant1Id < Participant2Id`.
- On unique-constraint conflict during concurrent creation, return the existing conversation rather than failing.
- **Creation gates:** Forbid creating a new conversation if either user has blocked the other, or if the target account is soft-deleted or email-unverified.

---

## Per-User Conversation State

Per-user behaviors (`Clear`, `Delete`, `Mute`, `Pin`, `Archive`, `LastRead`) must never mutate the shared conversation entity.

### Conceptual model

One record per `(UserId, ConversationId)`:

| Field | Purpose |
|---|---|
| `ConversationId` | The conversation |
| `UserId` | The user |
| `ClearedAtUtc` | Messages before this timestamp are hidden for this user |
| `DeletedAtUtc` | Conversation is hidden from this user's list (and prior messages hidden) until a qualifying new message arrives or the user sends a new message |
| `MutedAtUtc` / mute state | Notification suppression |
| `Pinned` | Conversation pinned to top of list |
| `Archived` | Conversation moved to archive |
| `LastReadMessageId` | Last message read by this user; enforced by composite FK `(ConversationId, LastReadMessageId)` referencing `Messages(ConversationId, Id)` to prevent cross-conversation IDOR |
| `LastReadAtUtc` | `CreatedAtUtc` of `LastReadMessageId` for indexed unread-count range scans without extra joins |

### Clear chat & Conversation deletion

- **Clear chat:** Sets `ClearedAtUtc` for the requesting user only. Messages with `CreatedAtUtc` before `ClearedAtUtc` are excluded from that user's view. New messages appear normally.
- **Conversation deletion:** Does not physically delete the conversation or shared messages. Sets `DeletedAtUtc` for the requesting user (hiding the conversation and messages prior to `DeletedAtUtc`). The conversation reappears for that user when either the other participant sends a new message after `DeletedAtUtc` **or** the deleting user sends a new message to that participant.

---

## Account Rules

### Registration, Login, and Profile

- **Username format:** Enforce a strict ASCII allowlist (`^[a-zA-Z0-9._-]{3,32}$`) at registration to prevent homoglyph/zero-width/BiDi impersonation. Username and email cannot be changed after registration.
- **Password hashing & lockout:** Use ASP.NET Core Identity's default password hashing and enable account lockout (`LockoutOnFailure: true`). Never implement custom crypto.
- **Unverified account expiration:** Unverified accounts cannot log in, cannot be discovered or messaged, and are purged by a background cleanup job after 24–72 hours to prevent email/username squatting.
- **Anti-enumeration & timing defense:**
  - Login failures (bad password, nonexistent user, locked out, unverified, soft-deleted), password-reset requests, and account-recovery requests must return identical, generic responses.
  - On login user-lookup miss, execute a dummy `IPasswordHasher<ApplicationUser>.VerifyHashedPassword` check to mitigate timing-based account enumeration.
- **Safe email links & templates:**
  - Construct all emailed links (email verification, password reset, account recovery) exclusively from a statically configured `PublicBaseUrl` — never from `HttpRequest.Host` or `X-Forwarded-Host`.
  - HTML-encode `Username` and `DisplayName` in all transactional email templates.
  - Email verification and account recovery links must land on a `GET` page that requires clicking a `POST` form submission button (defeats corporate email security link prefetchers and CSRF); email verification must **never** auto-sign the user in.
- **Password reset / change:** Must **not** reactivate a soft-deleted account, and must immediately revoke all `UserSession` records, rotate `SecurityStamp`, disconnect all SignalR connections, and delete all `PushSubscription` records.

### Account Deletion

Soft-delete with a 30-day recovery window:

| Concern | Behavior during 0–30 day deleted state |
|---|---|
| Authentication | Denied — user cannot log in |
| Active sessions & SignalR | All `UserSession`s revoked immediately; all SignalR connections disconnected immediately |
| Push subscriptions | Deleted/deactivated immediately |
| Username & email reservation | Kept reserved for the full 30-day window (cannot be re-registered) |
| User discovery & profile | Hidden from search and profile views (displays "Deleted Account" placeholder in existing chats) |
| Sending / Receiving | Cannot send; other users may still send to existing conversations (visible to sender; visible to deleted user only if recovered) |
| Existing messages & media | Preserved during the 30-day recovery window |
| Recovery | Requires verifying the user's password **and** a short-lived emailed recovery token via `POST` confirmation within 30 days |

After 30 days: account is permanently purged, personal data scrubbed, R2 media cleaned up, and messages attributed to "Deleted Account."

### Session and Device Management

- Included in V1 as part of Phase 2 authentication foundation.
- Users can view active sessions (parsed, normalized device/browser label capped at 100 chars — never store/render raw unbounded `User-Agent` headers — plus creation time and last active time), log out an individual session, or log out all other sessions.
- **Immediate revocation & bounded session lifetime (do NOT rely solely on Identity's default 30-minute `SecurityStampValidator` interval):**
  - Each login creates a persistent `UserSession` record in SQL Server with an explicit **`AbsoluteExpiresAtUtc`** (e.g., 30 days from login — cannot be extended indefinitely by cookie sliding expiration) and embeds `SessionId` as a claim in the auth cookie.
  - `CookieAuthenticationEvents.OnValidatePrincipal` calls `SecurityStampValidator.ValidatePrincipalAsync` and verifies `SessionId` is present, active, and `UtcNow < AbsoluteExpiresAtUtc` (backed by SQL Server, optionally fronted by an in-memory active/revoked session cache updated immediately on revocation).
  - Logging out or remotely revoking an individual session immediately:
    1. Marks `UserSession` revoked in SQL Server and evicts it from the in-memory session cache.
    2. Disconnects all SignalR connections tied to that `SessionId`.
    3. Deletes/deactivates all Web Push subscriptions tied to that `SessionId` (client-side logout also calls `pushSubscription.unsubscribe()`).
  - Rotating Identity's `SecurityStamp` is used for account-wide invalidations (password reset/change, account deletion, and "log out all sessions").

---

## User Discovery, Presence, and Blocking

### User Discovery

- Discovery by username or email (no friendship/contact system in V1).
- **Email privacy:** Email search uses **exact match only** (no partial/prefix matching). Search response DTOs must **never** include another user's email address (`UserId`, `Username`, `DisplayName`, avatar only).
- Blocked users, soft-deleted accounts, and email-unverified accounts do not appear in search results.

### Presence and Last Seen Visibility

- Online presence and `LastSeen` are **not** globally queryable.
- A user may view or receive presence/`LastSeen` updates **only** for users with whom they share an existing conversation and have **no block** in either direction.

### Blocking Rules

- Persisted in SQL Server with a unique constraint on `(BlockerId, BlockedUserId)` and an index on `BlockedUserId`. Does not delete conversation history.
- When user A blocks user B:

| Concern | Effect |
|---|---|
| Messaging & new conversations | A and B cannot message each other or create a new conversation |
| User search | A and B do not appear in each other's search results |
| Typing, delivery & read receipts | Disabled between A and B (checked live on HTTP & hub events) |
| Presence & Last seen | Hidden between A and B |
| Notifications | No SignalR or Web Push notifications between A and B |
| Conversation history | Preserved |

---

## Search Rules

- Conversation search (by participant display name or username) is included in V1.
- Message-content search is NOT included in V1.

---

## Media Rules

### Storage Architecture and Least Privilege

- Cloudflare R2 stores media binaries in a **private bucket** (`r2.dev` public bucket access must remain disabled). Serve presigned URLs via the R2 S3 endpoint or a separate custom domain that shares no cookie scope with the web app.
- R2 API credentials must be scoped strictly to **Object Read & Write on this single bucket only** (never an account-wide token) and never exposed to the browser.
- Two distinct key prefixes:
  - `pending/{objectId}` — untrusted client staging area for presigned `PUT`.
  - `media/{objectId}` — verified, client-immutable storage promoted server-side.
- SQL Server stores metadata: `UploaderId`, `ConversationId`, sanitized filename, server-verified MIME type, size, R2 object key, lifecycle state, and timestamps.

### Upload Architecture (TOCTOU-Safe & Decompression-Bomb-Safe)

**Preferred approach (presigned upload with server-side verification and promotion):**

1. Client requests upload authorization (specifying `ConversationId`, declared MIME type, exact byte size, and filename).
2. Application validates: user is authenticated, authorized for `ConversationId` (and not blocked), below concurrent `Pending` upload cap and **per-user daily upload quota**, and file type/size are within limits.
3. Application sanitizes the filename (strips control chars, path separators, quotes, and Unicode BiDi override characters `U+202A`–`U+202E`, `U+2066`–`U+2069`) and generates `pending/{objectId}`.
4. Application inserts SQL metadata bound to `(UploaderId, ConversationId)` with state `Pending`.
5. Infrastructure issues a **short-lived (2–5 minute)** presigned `PUT` URL scoped strictly to `pending/{objectId}` with `Content-Type` and exact `Content-Length` signed in the request.
6. Client uploads directly to R2.
7. Client notifies Application of upload completion.
8. Application verifies the staging object in R2:
   - Confirms existence and actual `Content-Length` within limits.
   - **Bounded magic-byte verification (videos, documents, voice):** Read only a **bounded HTTP byte range (first 4 KB)** from R2 to verify file signatures — **never buffer full multi-megabyte files into server memory**.
   - **Image decompression-bomb protection & EXIF/GPS stripping:** Read image headers first (before pixel decode) and reject images exceeding strict bounds (**max 8,192 × 8,192 px, max 33 megapixels**, and bounded animation frame count for GIF/WebP). Strip EXIF/GPS metadata (prefer lossless metadata chunk/segment stripping where feasible, and gate image processing with a server-wide concurrency semaphore of max 2–4 jobs and a strict timeout).
9. **Immutable promotion (defeats presigned-PUT overwrite TOCTOU):** Infrastructure writes/copies the verified object to `media/{objectId}` (unwritable by the client's presigned URL) and deletes `pending/{objectId}`.
10. Application updates SQL metadata with key `media/{objectId}`, server-verified MIME type, and state `Uploaded`.

**Fallback:** If presigned uploads are not feasible for a specific scenario, relay through the server using bounded streaming.

**R2 CORS:** Allow only the application's exact origin (never `*`).

### Attachment and Download Authorization (Prevents Media IDOR)

- **Attaching media to a message:** Atomic SQL write must enforce `Media.UploaderId == CurrentUser.Id`, `Media.ConversationId == TargetConversationId`, and `Media.State == MediaState.Uploaded` before transitioning to `Attached`.
- **Issuing presigned `GET` URLs (TTL 5–15 minutes):** Issue **only** when `Media.State == Attached`, the associated message is **not tombstoned**, the requesting user is a participant in that conversation, and the message is visible to them (not `DeletedForMe` and after `ClearedAtUtc`/`DeletedAtUtc`).
- **Safe download headers:** Always override `ResponseContentType` with the **server-verified MIME type**, include `X-Content-Type-Options: nosniff`, and force `Content-Disposition: attachment` (RFC 5987 `filename*` encoded) for **all** documents including PDFs. Active web content (`SVG`, `HTML`, `JS`, `XML`) is strictly prohibited.

### Media Lifecycle and Limits

| State | Meaning |
|---|---|
| `Pending` | Metadata created; upload in progress in `pending/` |
| `Uploaded` | Verified binary promoted to `media/`; not yet attached |
| `Attached` | Associated with a persisted message |
| `Failed` | Upload failed or verification rejected |
| `Deleted` | Marked for R2 object cleanup |

- Stale `Pending`/`Failed` records (and `pending/` objects), orphaned R2 objects without SQL metadata, and `Deleted` media objects (e.g., after delete-for-everyone) are purged by background cleanup jobs.

| Type | Maximum size | Allowed formats |
|---|---|---|
| Images | 15 MB | JPEG, PNG, WebP, GIF (no SVG) |
| Videos | 70 MB | MP4, WebM, MOV |
| Documents | 50 MB | PDF, DOC, DOCX, XLS, XLSX, PPT, PPTX, TXT, CSV, ZIP |
| Voice messages | 25 MB / 20 minutes | Browser native recording format (size enforced server-side; no transcoding in V1) |

---

## Security Rules

### Encryption Claims

V1 does **NOT** implement end-to-end encryption. Never write "End-to-End Encrypted", "End-to-End Encryption", "Encrypted Session", "Verified encrypted session", or any equivalent claim in UI, marketing copy, or code comments.

### Web, Cookie, Header, and Cache Hardening

- **Zero state-changing `GET` endpoints:** All mutations — including Logout, Email Verification confirmation, and Account Recovery confirmation — must be `POST`/`PUT`/`DELETE` with anti-forgery validation.
- **Anti-forgery (CSRF):** Validate ASP.NET Core anti-forgery tokens on every state-changing MVC form and JSON/fetch endpoint.
- **Cookie hardening:** Name the auth cookie with the **`__Host-`** prefix (enforces `Secure`, `Path=/`, host-only with no `Domain`), `HttpOnly = true`, `Secure = CookieSecurePolicy.Always` (production), and `SameSite = SameSiteMode.Lax` (or `Strict`).
- **Open redirect prevention:** Validate every `ReturnUrl` with `Url.IsLocalUrl()` / `LocalRedirect()`.
- **Host header & HTTPS hardening:** Enforce strict `AllowedHosts` in production (never `*`), `UseHsts()`, and HTTPS redirection. Build all external/emailed links exclusively from configured `PublicBaseUrl`.
- **Strict CSP & security headers:** Enforce `Content-Security-Policy` (`default-src 'self'; script-src 'self'; style-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'`, permitting only the configured R2 origin in `connect-src`/`img-src`/`media-src`), `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, and `Referrer-Policy: strict-origin-when-cross-origin`.
- **Authenticated cache prevention:** Emit `Cache-Control: no-store, no-cache` and `Pragma: no-cache` on all **dynamic authenticated Razor views and JSON endpoints** (messages, conversations, profiles, presigned URLs) via a global action filter or post-static-asset middleware. Do **not** apply `no-store` to public static `.css`/`.js` assets served by `MapStaticAssets()`.
- **Data Protection keys:** Persist ASP.NET Core Data Protection keys encrypted at rest (certificate, OS key protection, or encrypted SQL store) — never ephemeral container storage or plaintext disk with loose permissions.

### Reverse Proxy and Rate Limiting

- **Forwarded headers:** Configure `ForwardedHeadersOptions` to trust **only** explicitly configured `KnownProxies` / `KnownNetworks` (never trust arbitrary `X-Forwarded-For` headers).
- **Rate-limit keying:**
  - Unauthenticated endpoints (login, registration): key by trusted client IP (and account identifier where appropriate).
  - Email-triggering endpoints (register, resend verification, forgot password, recovery): rate-limit by **both trusted IP and normalized target email** (e.g., max 1 email per address per 60–120 seconds plus a daily cap) to prevent email bombing.
  - Authenticated endpoints (messaging, uploads, user search): key by authenticated **`UserId`**, not IP alone.
  - Ephemeral SignalR hub invocations: throttle via `IHubFilter`.

### Resource Bounds (Single-Server DoS Protection)

| Resource | Server-Enforced Cap (defaults; tunable via config) |
|---|---|
| Message text length | Max 4,000 characters |
| Username / display name / idempotency key / normalized UA | 32 / 64 / 128 / 100 characters |
| Keyset pagination `pageSize` | Default 30, hard max 100 |
| Concurrent `Pending` media uploads per user | Max 5–10 active `Pending` records |
| Per-user daily upload quota | Max 500 MB – 1 GB / 24h (configurable) |
| Image dimensions & processing concurrency | Max 8,192 × 8,192 px, max 33 MP, max 2–4 concurrent image jobs |
| Concurrent SignalR connections per user | Max 10 active connections |
| SignalR `MaximumReceiveMessageSize` | Max 16 KB (mutations use HTTP) |

### Security Posture

Security is implemented alongside each feature — never deferred to Phase 13 (which is a comprehensive audit/hardening pass):

- **Phase 2:** Identity (`IdentityUser<Guid>`), lockout, timing-safe login, `__Host-` cookie, CSRF, `LocalRedirect`, `PublicBaseUrl`, `AllowedHosts`, HSTS, Data Protection persistence, `ForwardedHeaders`, IP+email rate limiting, `POST`-only logout/verification/recovery, `UserSession` with `AbsoluteExpiresAtUtc`, `Cache-Control: no-store`.
- **Phases 3–4:** Authorization, exact-match email discovery, ASCII usernames, blocking, presence/last-seen scoping, conversation creation gates.
- **Phase 5:** Message authorization, composite-FK scope enforcement, XSS-safe rendering, strict CSP, compiled static CSS, payload bounds, `UserId`-keyed rate limiting.
- **Phases 6–7:** SignalR `Origin` validation, register-then-verify `OnConnectedAsync`, `IHubFilter` session checks + throttling, live block-aware routing.
- **Phase 8:** Composite-FK reply/read-receipt scope, SQL content scrubbing on tombstone, edit restrictions.
- **Phase 9:** R2 least-privilege bucket token, staging-to-permanent promotion, bounded 4 KB magic-byte checks, decompression-bomb bounds, EXIF stripping, filename BiDi sanitization, daily quotas, strict media attachment/download IDOR checks.
- **Phase 12:** IDs-only outbox payload with live SQL re-check, `SocketsHttpHandler.ConnectCallback` SSRF protection + host allowlist, globally unique `Endpoint` ownership reassignment, session-bound push cleanup, non-caching Service Worker.

---

## Notification Architecture

V1 is browser-only. Native mobile push is not in scope.

### Notification Mechanisms

| User state | Mechanism |
|---|---|
| Active SignalR connection viewing the target conversation | Suppressed — no notification |
| Active SignalR connection(s) not viewing the target conversation | Notification delivered via SignalR event |
| No active SignalR connections (offline / all tabs closed) | Web Push notification (Push API + Service Worker) |
| Conversation is muted, sender is blocked, or message tombstoned | No notification regardless of state |

### Multi-Device, Ownership, and SSRF Protection

- Multiple Web Push subscriptions may exist per user across devices. Every `PushSubscription` is bound to its originating `SessionId` so session logout/revocation immediately deletes/deactivates it.
- **Shared-browser ownership (prevents cross-user push hijacking):** Enforce a **globally unique index on `Endpoint`** (or its SHA-256 hash). On registration, atomically reassign/overwrite ownership of that endpoint to the currently authenticated `(UserId, SessionId)` so only one account can own a browser push endpoint at a time.
- Notification suppression for "viewing the active conversation" uses per-connection conversation tracking in server memory (single-server V1).
- **Web Push SSRF & DNS rebinding protection:**
  - Enforce an explicit host allowlist for major browser push services (`fcm.googleapis.com`, `updates.push.services.mozilla.com`, `web.push.apple.com`, `*.notify.windows.com`), require `https://`, cap URL length, and disable automatic HTTP redirects.
  - Validate the endpoint at registration **and** pin IP validation inside **`SocketsHttpHandler.ConnectCallback`** (rejecting loopback `127.0.0.0/8`/`::1`, RFC 1918 private networks, link-local/cloud metadata `169.254.0.0/16`/`fe80::/10`, and IPv6 ULA `fc00::/7` before calling `Socket.ConnectAsync`) so DNS rebinding is impossible.

---

## Transactional Outbox

Critical asynchronous work — particularly Web Push notifications — uses a durable SQL Server-backed outbox.

### Atomicity and Privacy Contract

The outbox record and triggering state change must commit in the same SQL transaction:

```
begin transaction
    persist application state change (e.g., new message)
    create outbox record (IDs only: MessageId, ConversationId, RecipientUserId)
commit
```

- **IDs-only payload (prevents deleted/blocked text leaks):** Never store raw message bodies or previews in the outbox payload. Immediately before dispatching Web Push, re-query SQL Server for live message non-tombstoned state, live block state, live mute state, and active `PushSubscription` records.
- Never commit business data and create the outbox row in a separate transaction, and never use an in-memory queue for durable work.

### Outbox Processor

A hosted `BackgroundService` processes the outbox on a polling interval:
- **Restart-safe:** Unprocessed records are picked up after restart.
- **Retry-safe:** Transient failures retry with exponential backoff up to a max-retry/dead-letter threshold.
- **Duplicate-safe:** Claim batches atomically in SQL Server (`UPDATE ... OUTPUT` with `UPDLOCK, READPAST` or leased `LockedUntilUtc`) using parameterized SQL exclusively.

---

## Testing Strategy

Tests are added alongside every feature, not deferred to Phase 14.

### Test Database Rule (STRICT)

- **Never use EF Core `UseInMemoryDatabase()` or SQLite** for persistence, migration, constraint, or concurrency tests.
- All persistence, integration, and concurrency tests must run against **real SQL Server** (LocalDB or Testcontainers SQL Server).
- Pure Domain unit tests and isolated Application tests may use lightweight in-memory fakes where database semantics are not under test.

### Continuous Testing Matrix

| Category | Phase |
|---|---|
| Project dependency direction & service layer placement (automated architecture tests) | Phase 1 / 6.5 |
| Auth, lockout, timing-safe anti-enumeration, CSRF, `POST`-only logout/confirm, `LocalRedirect`, `Cache-Control: no-store` | Phase 2 / 6.5 |
| Individual & all-other session revocation, `AbsoluteExpiresAtUtc`, soft-delete reservation & recovery | Phase 2 / 6.5 |
| Blocking enforcement, exact-match email discovery, ASCII usernames, presence/last-seen scoping | Phase 3 / 6.5 |
| Conversation membership auth, creation gates & concurrent uniqueness on SQL Server | Phase 4 / 6.5 |
| Message idempotency (concurrent & conflict), deterministic ordering & keyset pagination caps | Phase 5 / 6.5 |
| SignalR `Origin` validation, register-then-verify revoke race, `IHubFilter` session/throttle & live block-aware routing | Phase 6 / 6.5 |
| Composite-FK reply/read-receipt IDOR rejection, delete-for-everyone timing, SQL content scrubbing & edit limits | Phase 8 |
| R2 staging promotion, bounded 4 KB magic-byte check, decompression-bomb caps, EXIF strip, filename BiDi strip & media IDOR checks | Phase 9 |
| Clear chat, conversation deletion visibility & un-deletion | Phase 11 |
| Outbox atomicity, IDs-only live re-check, atomic SQL claiming, SSRF `ConnectCallback` rejection, unique `Endpoint` reassignment & session-bound push cleanup | Phase 12 |

Phase 14 supplements per-feature tests with cross-layer integration, concurrency, E2E, and performance baseline suites.


### Testing Lane Separation & Live Browser Smoke Gate (MANDATORY)
- **C# `tests/AOS.Messenger.Tests` (Backend & Security Lane):** Use xUnit + real SQL Server LocalDB + `WebApplicationFactory` strictly for domain invariants, EF Core migrations/constraints, concurrency, authorization, rate limits, CSRF, JSON API contracts, and static CSP/XSS regex checks. Do **NOT** write brittle C# `Html.Contains(...)` tests to verify visual layout.
- **Chrome DevTools MCP (UI & JS Verification Lane):** Before marking any phase or UI ticket complete, the final verifier MUST start `AOS.Messenger.Web` over HTTPS (`--launch-profile https`), disable browser cache, inspect affected routes on **Desktop (`1440x900`)** and **Mobile (`390x844`)** in **Light and Dark** themes, and pass all 3 automated checks (then stop the server process cleanly):
  1. **Zero Runtime Errors:** `0` JS `console.error`s, `0` CSP violations, and `0` unexpected 4xx/5xx network responses (JSON mutations must return `200`/`201`, never `400`).
  2. **Zero Duplicate DOM IDs:** Evaluate `[...document.querySelectorAll('[id]')].map(e => e.id)` and assert zero duplicates (instantly catches double-rendered sidebars, modals, or partials).
  3. **Valid Responsive Geometry:** Assert `document.documentElement.scrollWidth <= window.innerWidth` (zero horizontal overflow) AND on mobile detail routes (`390x844`) assert `<main>` fills `>= 90%` of viewport width (prevents side-by-side pane crushing).
---

## Phases

| Phase | Status | Description |
|---|---|---|
| Phase 0 | ✅ Complete | Project shell and architecture foundation |
| Phase 1 | ✅ Complete | Domain model, SQL Server schema, and architecture foundation |
| Phase 2 | ✅ Complete | ASP.NET Core Identity, registration, email verification, login, logout, password reset, session/device management |
| Phase 3 | ✅ Complete | User profiles, user discovery, blocking |
| Phase 4 | ✅ Complete | Conversation creation, conversation persistence, per-user conversation state, conversation list |
| Phase 5 | ✅ Complete | Text messaging, message authorization, idempotency, deterministic ordering |
| Phase 6 | ✅ Complete | SignalR realtime delivery, reconnect and state reconciliation |
| Phase 6.5 | ✅ Complete | Remediation & Security Hardening Sprint (Tickets 1–6 closure against `REMEDIATION_AUDIT_V2.md`) |
| Phase 7 | ✅ Complete | Delivery/read receipts, typing indicators, presence, last seen |
| Phase 8 | ✅ Complete | Message operations: edit, delete for me, delete for everyone, reply, reactions |
| Phase 9 | 🔜 Next | Media messages and Cloudflare R2 integration |
| Phase 10 | ⏳ | Voice messages |
| Phase 11 | ⏳ | Conversation features: search, pin, archive, mute, clear, delete |
| Phase 12 | ⏳ | Web Push notifications and transactional outbox |
| Phase 13 | ⏳ | Security hardening, abuse protection, and authorization audit |
| Phase 14 | ⏳ | Comprehensive automated, integration, and concurrency testing |
| Phase 15 | ⏳ | Performance, observability, and production readiness |
| Phase 16 | ⏳ | Production deployment |

### Phase Completion Checklist (Definition of Done)

Before declaring any phase complete or requesting approval to proceed, the contributor (human or AI agent) **must** verify:

1. `dotnet build` completes with **0 errors and 0 warnings** (`TreatWarningsAsErrors` enabled).
2. `dotnet test` passes **100%** (including architecture tests and real SQL Server persistence tests) plus all new tests for the phase.
3. No un-migrated EF Core model changes exist; migrations apply cleanly to a fresh SQL Server database.
4. Project references and service placements strictly obey Dependency Direction rules.
5. No hardcoded secrets, connection strings, or PII/message-content logging were introduced.
6. All state-changing HTTP endpoints enforce authentication, authorization, anti-forgery, `POST`/`PUT`/`DELETE` verbs, input bounds, and `Cache-Control: no-store` on dynamic authenticated responses.

> **Do not implement a later phase before the current phase is explicitly approved.**

---

## Implementation Guardrails

### Dependency Direction & Identity

- Never introduce a new project dependency direction without updating this document.
- Never reference Infrastructure from Application, Web from Application/Infrastructure, or any layer from Domain.
- Never place Application use-case services inside Infrastructure.
- Never place Identity or EF Core types/attributes in Domain.
- Never use default string-keyed `IdentityUser`; always use `IdentityUser<Guid>`.

### Authorization, Trust, and IDOR Prevention

- Never trust client-provided sender identity, user identity, conversation ownership, `Host` headers, or arbitrary `X-Forwarded-For` headers.
- Never look up sequential `bigint` `Message.Id` values without scoping to `ConversationId` and verifying membership.
- Never omit composite foreign keys `(ConversationId, ReplyToMessageId)` and `(ConversationId, LastReadMessageId)`.
- Never expose presence or `LastSeen` to users who do not share an unblocked conversation.
- Never allow clients to write directly to `media/`, select arbitrary R2 keys, attach unowned/cross-chat media, or fetch presigned `GET` URLs for unattached, tombstoned, or hidden media.
- Never expose R2 credentials or enable public `r2.dev` bucket access.

### Data Integrity & Concurrency

- Never bypass message idempotency or skip payload-equivalence checks on duplicate keys.
- Never broadcast through SignalR before the SQL transaction commits, and never re-broadcast on idempotent replay.
- Never split an atomic multi-table write across independent `SaveChangesAsync()` calls or create an outbox record outside the triggering transaction.
- Never store raw message text in the outbox payload (IDs only; re-verify live state before dispatch).
- Never leave deleted text un-scrubbed in SQL `Content` or conversation previews after delete-for-everyone.
- Never use random `Guid.NewGuid()` or big-endian UUIDv7 as a SQL Server clustered primary key.
- Never use string concatenation or `$""` interpolation in `FromSqlRaw` / `ExecuteSqlRaw`.
- Never use EF Core InMemory or SQLite to test SQL Server constraints, concurrency, or outbox locking.

### Architecture & Resource Safety

- Never introduce Redis, Kafka, RabbitMQ, MediatR, CQRS, event sourcing, `IRepository<T>`, `IUnitOfWork`, jQuery, Bootstrap, Tailwind Play CDN, Node.js/npm frontend toolchains, or frontend SPA frameworks without approval.
- Never put business logic in controllers, hubs, or views, or put persistent state mutations on SignalR hubs instead of HTTP endpoints.
- Never use `ApplicationDbContext` from Application.
- Never buffer full multi-megabyte R2 objects in server memory for magic-byte verification, or decode images without header dimension/megapixel bounds and concurrency throttling.
- Never add NuGet packages without a documented reason.

### Time and Ordering

- Never trust client timestamps for ordering, authorization, or business rules.
- Never store timestamps in local time; always store UTC and use `TimeProvider`.
- Never use offset-based pagination for message history.

### Security and Privacy

- Never claim or imply end-to-end encryption anywhere in UI, docs, or comments.
- Never implement state-changing `GET` endpoints (logout, email confirmation, recovery must submit via `POST`).
- Never render user-generated content with `@Html.Raw`, `innerHTML`, `outerHTML`, or `insertAdjacentHTML`, and never auto-link unsafe URL schemes.
- Never omit `Cache-Control: no-store` on dynamic authenticated responses, or cache authenticated HTML/API/media in the Service Worker.
- Never build emailed links from `Request.Host` (always use `PublicBaseUrl`) or redirect to unvalidated `ReturnUrl`s.
- Never log message bodies, passwords, tokens, cookies, raw emails, or presigned URLs, or leak stack traces/SQL errors in production responses.
- Never allow partial-match email discovery, return another user's email in search results, or store raw unbounded `User-Agent` headers.
- Never skip CSRF validation, SignalR `Origin` checks, `OnConnectedAsync` register-then-verify, or `IHubFilter` session validation.
- Never dispatch Web Push without host allowlist + `SocketsHttpHandler.ConnectCallback` SSRF IP validation, globally unique `Endpoint` ownership, or session-bound cleanup on logout/revocation.
- Never hard-code credentials, swallow exceptions silently, or defer security requirements to a later phase.