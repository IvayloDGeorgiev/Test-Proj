# Implementation plan

Planning is separate from the eight implementation stages below. Runtime status is authoritative in WORK_PROGRESS.md; the table here records the planning baseline. Each stage runs in a fresh task and includes all earlier commits. Security/resilience belongs with the feature; Stage 7 completes host wiring, not deferred transport safety.

| Stage | Name | Branch | Status |
| --- | --- | --- | --- |
| 1 | Project and test foundation | stage/01-project-foundation | NOT STARTED |
| 2 | Police API transport and validation | stage/02-police-api-client | NOT STARTED |
| 3 | Safe CSV and file export | stage/03-csv-export | NOT STARTED |
| 4 | Forces ingestion endpoint | stage/04-forces-ingestion | NOT STARTED |
| 5 | Crime ingestion endpoint | stage/05-crime-ingestion | NOT STARTED |
| 6 | Stop-and-search ingestion endpoint | stage/06-stop-search-ingestion | NOT STARTED |
| 7 | API limits and operational contracts | stage/07-api-hardening | NOT STARTED |
| 8 | Integration and final verification | stage/08-integration-verification | COMPLETED |
| 9 | PostgreSQL persistence, data sync and simple frontend | stage/09-postgres-sync-frontend | NOT STARTED |

## Common definition of done

Every stage must satisfy ALL of WORKFLOW.md's completion gate. Its acceptance criteria below are additional, not substitutes. Required commands run from repository root: restore existing solution, targeted stage group, full solution tests, solution build; test discovery must be nonzero. Record real results. Each stage updates progress/decisions and relevant documentation, reviews secrets/diff/status, commits and verifies its push, then publishes the completion receipt. No stage is complete with unimplemented requirements in its scope or unresolved relevant failures.

## Stage 1: Project and test foundation

- Status at planning: NOT STARTED (current status: WORK_PROGRESS.md).
- Objective: Establish real configuration behaviour and the necessary test project without ingestion.
- Branch: `stage/01-project-foundation`.
- Prerequisites: planning branch pushed with all nine consistent documents; clean or safely isolated checkout; no competing owner.
- Relevant components/files: Program.cs; existing csproj/slnx; Options; tests/.../Foundation.
- Implementation tasks: Move local provider before build; preserve environment/CLI precedence; exclude local JSON from build/publish; remove unused GitHub placeholder; add validated options and one xUnit project to existing solution.
- Required AAA unit tests: Synthetic configuration timing/precedence, Development-only loading, unsafe origin/root/limit validation, local file publish exclusion.
- Edge/error tests: Missing optional local file; missing Desktop outside supported fallback; invalid options fail startup.
- Acceptance criteria: Existing app still builds; actual configuration behaviours have meaningful tests; no ingestion routes.
- Expected coherent commit: `test: establish configuration and xUnit foundation`; optional separate meaningful regression increment, then documentation receipt.
- Completion criteria / definition of done: stage acceptance plus the entire common gate, verified remote receipt and durable evidence. Only then dispatch Stage 2.

Verification commands:

```powershell
dotnet restore "Test Proj.slnx"
dotnet test "tests/PoliceDataIngestion.Api.Tests/PoliceDataIngestion.Api.Tests.csproj" --filter "FullyQualifiedName~Foundation" --no-restore
dotnet test "Test Proj.slnx" --no-restore
dotnet build "Test Proj.slnx" --no-restore
```

## Stage 2: Police API transport and validation

- Status at planning: NOT STARTED (current status: WORK_PROGRESS.md).
- Objective: Provide safe reusable transport and request validation.
- Branch: `stage/02-police-api-client`.
- Prerequisites: Stage 1 COMPLETED with verified implementation and receipt commits; clean or safely isolated checkout; no competing owner.
- Relevant components/files: Clients/PoliceApi; Validation; Errors; tests/.../PoliceApi.
- Implementation tasks: Add typed client, forces DTO and reusable array reader; finite coordinates/month validation; bounded retries, time budgets, trusted origin and disabled redirects; classify errors.
- Required AAA unit tests: URI/culture/deserialization; bounds/month; all status/retry/Retry-After cases in TEST_STRATEGY; token propagation.
- Edge/error tests: Malformed/null/empty/wrong-shaped body; DNS/network; 408/429/5xx; cancellation; oversized responses.
- Acceptance criteria: No live calls in tests; correct permanent/transient distinction and at most three attempts; invalid input has no side effects.
- Expected coherent commit: `feat: add validated Police API transport`; optional separate meaningful regression increment, then documentation receipt.
- Completion criteria / definition of done: stage acceptance plus the entire common gate, verified remote receipt and durable evidence. Only then dispatch Stage 3.

Verification commands:

```powershell
dotnet restore "Test Proj.slnx"
dotnet test "tests/PoliceDataIngestion.Api.Tests/PoliceDataIngestion.Api.Tests.csproj" --filter "FullyQualifiedName~PoliceApi" --no-restore
dotnet test "Test Proj.slnx" --no-restore
dotnet build "Test Proj.slnx" --no-restore
```

## Stage 3: Safe CSV and file export

- Status at planning: NOT STARTED (current status: WORK_PROGRESS.md).
- Objective: Provide independently tested CSV encoding and atomic storage.
- Branch: `stage/03-csv-export`.
- Prerequisites: Stage 2 COMPLETED with verified implementation and receipt commits; clean or safely isolated checkout; no competing owner.
- Relevant components/files: Export; ExportOptions; tests/.../Export.
- Implementation tasks: Implement deterministic encoding, formula protection, code-owned filenames, root containment/link checks, singleton nonqueued lease, atomic replace and cleanup.
- Required AAA unit tests: Escaping/Unicode/null/formula matrix; validated numeric formatting; filenames/root boundaries; temp and replace behaviour.
- Edge/error tests: Write/permission failure; cancellation; existing final preservation; competing requests; reparse points.
- Acceptance criteria: Only root-contained complete files publish; prior exports survive failures; lease always released.
- Expected coherent commit: `feat: add safe atomic CSV exports`; optional separate meaningful regression increment, then documentation receipt.
- Completion criteria / definition of done: stage acceptance plus the entire common gate, verified remote receipt and durable evidence. Only then dispatch Stage 4.

Verification commands:

```powershell
dotnet restore "Test Proj.slnx"
dotnet test "tests/PoliceDataIngestion.Api.Tests/PoliceDataIngestion.Api.Tests.csproj" --filter "FullyQualifiedName~Export" --no-restore
dotnet test "Test Proj.slnx" --no-restore
dotnet build "Test Proj.slnx" --no-restore
```

## Stage 4: Forces ingestion endpoint

- Status at planning: NOT STARTED (current status: WORK_PROGRESS.md).
- Objective: Deliver the first complete dataset slice.
- Branch: `stage/04-forces-ingestion`.
- Prerequisites: Stage 3 COMPLETED with verified implementation and receipt commits; clean or safely isolated checkout; no competing owner.
- Relevant components/files: Services/Forces; Controllers; Contracts; Errors; template files; tests/.../Forces.
- Implementation tasks: Implement mapping/service/controller POST forces; common result and sanitized error mapper; header-only empty output; remove WeatherForecast template and update .http example.
- Required AAA unit tests: Client-to-row mapping, id/name checks, exact CSV schema/count/result; controller service interaction.
- Edge/error tests: Empty list; invalid required fields; upstream/write error; cancellation; repeated export replacement.
- Acceptance criteria: POST produces Forces.csv only after success, exposes filename only, errors follow R11.
- Expected coherent commit: `feat: implement forces ingestion`; optional separate meaningful regression increment, then documentation receipt.
- Completion criteria / definition of done: stage acceptance plus the entire common gate, verified remote receipt and durable evidence. Only then dispatch Stage 5.

Verification commands:

```powershell
dotnet restore "Test Proj.slnx"
dotnet test "tests/PoliceDataIngestion.Api.Tests/PoliceDataIngestion.Api.Tests.csproj" --filter "FullyQualifiedName~Forces" --no-restore
dotnet test "Test Proj.slnx" --no-restore
dotnet build "Test Proj.slnx" --no-restore
```

## Stage 5: Crime ingestion endpoint

- Status at planning: NOT STARTED (current status: WORK_PROGRESS.md).
- Objective: Add location/month crime retrieval and export.
- Branch: `stage/05-crime-ingestion`.
- Prerequisites: Stage 4 COMPLETED with verified implementation and receipt commits; clean or safely isolated checkout; no competing owner.
- Relevant components/files: Clients/PoliceApi crime DTOs; Services/Crimes; Controllers; tests/.../Crimes.
- Implementation tasks: Implement all-crime query, explicit nullable nested mapping, request validation and Crimes_YYYY-MM.csv schema.
- Required AAA unit tests: Boundary coordinates/months, invariant URI, required fields and optional location/outcome, record counts, CSV headers.
- Edge/error tests: Missing location/outcome; wrong returned month; empty response; malformed data; upstream failure/cancellation.
- Acceptance criteria: Invalid request makes no HTTP/file calls; valid request publishes expected deterministic schema safely.
- Expected coherent commit: `feat: implement crime ingestion`; optional separate meaningful regression increment, then documentation receipt.
- Completion criteria / definition of done: stage acceptance plus the entire common gate, verified remote receipt and durable evidence. Only then dispatch Stage 6.

Verification commands:

```powershell
dotnet restore "Test Proj.slnx"
dotnet test "tests/PoliceDataIngestion.Api.Tests/PoliceDataIngestion.Api.Tests.csproj" --filter "FullyQualifiedName~Crimes" --no-restore
dotnet test "Test Proj.slnx" --no-restore
dotnet build "Test Proj.slnx" --no-restore
```

## Stage 6: Stop-and-search ingestion endpoint

- Status at planning: NOT STARTED (current status: WORK_PROGRESS.md).
- Objective: Complete the third dataset slice.
- Branch: `stage/06-stop-search-ingestion`.
- Prerequisites: Stage 5 COMPLETED with verified implementation and receipt commits; clean or safely isolated checkout; no competing owner.
- Relevant components/files: Clients/PoliceApi stop DTOs; Services/StopSearches; Controllers; tests/.../StopSearches.
- Implementation tasks: Implement area query, optional demographics/location, timestamps/nullable booleans, POST and StopSearches_YYYY-MM.csv.
- Required AAA unit tests: Schema order and mapping, timestamp offset preservation, bool/null distinction, controller result/count.
- Edge/error tests: Optional absent nested objects; required timestamp/type failure; empty array; timeout/cancellation.
- Acceptance criteria: All three independent dataset routes satisfy shared validation, transport and export contracts.
- Expected coherent commit: `feat: implement stop and search ingestion`; optional separate meaningful regression increment, then documentation receipt.
- Completion criteria / definition of done: stage acceptance plus the entire common gate, verified remote receipt and durable evidence. Only then dispatch Stage 7.

Verification commands:

```powershell
dotnet restore "Test Proj.slnx"
dotnet test "tests/PoliceDataIngestion.Api.Tests/PoliceDataIngestion.Api.Tests.csproj" --filter "FullyQualifiedName~StopSearches" --no-restore
dotnet test "Test Proj.slnx" --no-restore
dotnet build "Test Proj.slnx" --no-restore
```

## Stage 7: API limits and operational contracts

- Status at planning: NOT STARTED (current status: WORK_PROGRESS.md).
- Objective: Complete host-facing resource and error contracts.
- Branch: `stage/07-api-hardening`.
- Prerequisites: Stage 6 COMPLETED with verified implementation and receipt commits; clean or safely isolated checkout; no competing owner.
- Relevant components/files: Program.cs middleware/options; Errors; Controllers OpenAPI metadata; logging; tests/.../Api.
- Implementation tasks: Enforce input limit and full-operation timeout including file I/O; audit shared admission across all routes; document OpenAPI responses; safe structured logs and consistent ProblemDetails.
- Required AAA unit tests: Error/status table, global contention, timeout distinction, sanitization, budget limits and response metadata.
- Edge/error tests: Oversized input/upstream/rows; delay beyond budget; cleanup failures; cancelled response; synthetic secret/path strings.
- Acceptance criteria: R7/R11/R12 consistent across endpoints; no raw internals or unbounded work; docs describe replacement semantics.
- Expected coherent commit: `feat: enforce ingestion API operational limits`; optional separate meaningful regression increment, then documentation receipt.
- Completion criteria / definition of done: stage acceptance plus the entire common gate, verified remote receipt and durable evidence. Only then dispatch Stage 8.

Verification commands:

```powershell
dotnet restore "Test Proj.slnx"
dotnet test "tests/PoliceDataIngestion.Api.Tests/PoliceDataIngestion.Api.Tests.csproj" --filter "FullyQualifiedName~Api" --no-restore
dotnet test "Test Proj.slnx" --no-restore
dotnet build "Test Proj.slnx" --no-restore
```

## Stage 8: Integration and final verification

- Status at planning: NOT STARTED (current status: WORK_PROGRESS.md).
- Objective: Verify the complete service through the ASP.NET host and finalize usage documentation.
- Branch: `stage/08-integration-verification`.
- Prerequisites: Stage 7 COMPLETED with verified implementation and receipt commits; clean or safely isolated checkout; no competing owner.
- Relevant components/files: tests/.../Integration; partial Program if required; README; all docs.
- Implementation tasks: Add WebApplicationFactory in existing test project; replace network/config with fakes; end-to-end three POST routes, OpenAPI, file safety and model binding; audit requirements/security traceability.
- Required AAA unit tests: Retain every unit suite; host valid/invalid requests, sanitized failures and concurrency with temporary roots.
- Edge/error tests: HTTP 400/409/413/502/503/504/500; real binding of malformed bodies; cancellation and atomic output preservation.
- Acceptance criteria: Every requirement has evidence; full suite/build pass; README contains accurate setup/examples; no ingestion gaps hidden by tests.
- Expected coherent commit: `test: verify ingestion contracts end to end`; optional separate meaningful regression increment, then documentation receipt.
- Completion criteria / definition of done: stage acceptance plus the entire common gate, verified remote receipt and durable evidence. Only then dispatch no successor (project stages finished).

## Stage 9: PostgreSQL persistence, data sync and simple frontend

- Status at planning: NOT STARTED (requested after Stage 8 completion).
- Objective: Add PostgreSQL persistence through EF Core migrations, synchronize the existing Police API data into the database, expose persisted-data and synchronization APIs, and add a lightweight static single-page frontend.
- Branch: `stage/09-postgres-sync-frontend`.
- Prerequisites: Stage 8 COMPLETED with verified implementation and receipt pushes; local PostgreSQL and User Secrets are operator prerequisites and must never be exposed or read for proof.
- Relevant components/files: EF Core/Npgsql registration, persistence DbContext/entities/migrations; synchronization service and API contracts/controllers; static frontend assets; persistence/integration tests; README, architecture, workflow, progress and security docs.
- Implementation tasks: Inspect existing contracts; add compatible EF Core/Npgsql packages; configure `ConnectionStrings:DefaultConnection`; create/apply migrations without manual tables; implement idempotent insert/update synchronization with safe metadata; add persisted-data and sync endpoints; add static HTML/CSS/JavaScript with disabled repeated sync, loading/error/empty states and refresh after success; preserve existing ingestion routes and security controls.
- Required AAA tests: Model/migration constraints; synchronization insert/update/no-duplicate behavior with isolated test infrastructure that never uses private User Secrets; API contracts and frontend asset/state behavior where testable.
- Edge/error tests: Missing/invalid configuration without disclosure; database unavailable/timeout/constraint failures; partial upstream data and repeated sync; concurrent sync admission; empty data; no credentials in static assets, logs, docs or staged files.
- Acceptance criteria: EF Core/Npgsql configured; schema represented by migrations and applied successfully to the configured local database; external data synchronizes idempotently; persisted data is retrievable; frontend displays and refreshes it safely; all prior tests/build pass; no secrets enter tests or Git.
- Expected coherent commit: `feat: add PostgreSQL persistence and sync frontend`, followed by the documentation-only completion receipt.
- Completion criteria / definition of done: Common WORKFLOW gate plus migration/runtime verification, implementation and receipt pushes. Stage 9 is the current final requested stage unless explicitly extended.

Verification commands:

```powershell
dotnet restore "Test Proj.slnx"
dotnet test "tests/PoliceDataIngestion.Api.Tests/PoliceDataIngestion.Api.Tests.csproj" --filter "FullyQualifiedName~Integration" --no-restore
dotnet test "Test Proj.slnx" --no-restore
dotnet build "Test Proj.slnx" --no-restore
```
