# PoliceDataIngestion.Api

The existing .NET 10 controller application now provides **POST /api/ingestion/forces**, **POST /api/ingestion/crimes** and **POST /api/ingestion/stop-searches**, with validated configuration, reusable Police API transport, safe CSV storage, and an xUnit suite. The WeatherForecast template has been removed.

The project remains `Test Proj/Test Proj.csproj`, namespace `Test_Proj`, in `Test Proj.slnx`. It retrieves public UK Police data and exports Forces.csv, Crimes_YYYY-MM.csv and StopSearches_YYYY-MM.csv. There is no database, background job, combined run route, or file download endpoint.

Use Windows with the .NET 10 SDK and a trusted, account-owned output directory on a local fixed drive. Atomic replacement is verified on local NTFS. From the repository root:

```powershell
dotnet restore "Test Proj.slnx"
dotnet test "Test Proj.slnx" --no-restore
dotnet build "Test Proj.slnx" --no-restore
# First-time HTTPS setup, if the development certificate is not already trusted:
dotnet dev-certs https --trust
# Optional explicit root, avoiding dependence on Desktop availability:
$env:Export__OutputRoot = Join-Path $env:LOCALAPPDATA 'PoliceDataIngestion'
dotnet run --project "Test Proj/Test Proj.csproj" --launch-profile https
```

Development resolves the current user's existing Desktop directory programmatically and selects its PoliceDataIngestion child as the output root. If Desktop is unavailable, configure `Export:OutputRoot` explicitly. Production always requires an explicit root. It must be an absolute local directory on a fixed Windows drive, without traversal, device names or links. The exporter creates missing directories and rechecks root components and destination safety before writing and publication. The operator must own the directory and prevent untrusted writes.

An optional `appsettings.Local.json` loads only in Development, before service registration and startup validation. It overrides standard JSON; user secrets, environment variables (for example `Export__OutputRoot`) and command-line arguments retain higher priority. Local configuration is ignored, untracked and excluded from build/publish output. Do not copy it into worktrees. Git authentication belongs to development tooling, not the application.

Options use sections `PoliceApi` and `Export`. PoliceApi defaults/maxima are: BaseUrl `https://data.police.uk/api/`, AttemptTimeoutSeconds 30, TotalOperationTimeoutSeconds 120, MaximumAttempts 3, MaximumResponseBytes 33554432 and MaximumRecords 100000. Positive limits may be reduced; attempt timeout cannot exceed total timeout. Export defaults/maxima are MaximumRequestBodyBytes 4096, MaximumConcurrentOperations exactly 1 and MaximumQueuedOperations exactly 0. OutputRoot has no production default. Options are validated at startup. The HTTP pipeline enforces the input limit and full-operation deadline; transport enforces upstream byte/record bounds, and all services share immediate global admission. Restart after configuration changes; local JSON reload is disabled to avoid changing trusted paths mid-operation.

All automated tests use synthetic configuration and temporary directories. The host suite runs the existing application through WebApplicationFactory with a fake HTTP handler and temporary content/output roots in both Development and Production. No test requires local credentials, the real Desktop or a live Police API connection. The foundation publish probe verifies exclusion using synthetic settings only. Run just the host suite with `dotnet test "tests/PoliceDataIngestion.Api.Tests/PoliceDataIngestion.Api.Tests.csproj" --filter FullyQualifiedName~Integration --no-restore`.

Stage 2 registers `IPoliceApiClient` through the typed HttpClient factory. `GetForcesAsync` returns validated id/name records, including an empty list for an empty array. All requests use the trusted HTTPS origin; redirects, cookies and automatic decompression are disabled. Response bytes are bounded while reading, before JSON parsing, and record counts are checked before DTO materialization. Malformed data and permanent statuses are never retried. Transient GET failures have at most three attempts, 500 ms exponential backoff plus up to 250 ms jitter (5-second cap), and Retry-After support that never retries early. An unfittable retry delay fails with a classified 503; exhausted timeouts classify as 504. Attempt and total retrieval deadlines include body reads and backoff. The HTTP operation deadline also covers request reading, mapping and export I/O.

`LocationMonth.Create` validates nullable finite coordinates and exact ASCII calendar months before query formatting; query numbers use invariant culture. Crime and stop/search retrieval use these reusable values. `PoliceApiException` contains safe codes/statuses only; centralized middleware translates ingestion failures into sanitized ProblemDetails in both Development and Production. Transport logs safe dataset/trace/attempt/status-class/count/elapsed/result fields and suppresses the factory's URL logging. Tests use scripted handlers and a manual clock, never live Police API calls or real retry sleeps.

`ICsvExporter` and a singleton `OperationLease` support ingestion. The forces service acquires a lease before retrieval, passes it to the exporter and disposes it in a `using` scope. Admission rejects contention immediately with `operation_busy` (409); there is no queue. A lease permits only one write at a time. It remains occupied during an active write even if disposed early. Multiple processes sharing a root are unsupported.

CSV uses BOM-free UTF-8, CRLF records and code-owned schemas/filenames. Text containing commas, quotes or line breaks is quoted, with embedded quotes doubled. Dangerous text receives a leading apostrophe before quoting: formula markers after whitespace/control characters, or values starting with tab/CR/LF. This deliberately changes unsafe text values. Typed finite numbers use invariant formatting; negative coordinates remain numeric. Optional nulls are empty cells, booleans lowercase, and timestamps retain their offsets.

The exporter writes a unique same-directory temporary file, flushes and closes it, then uses an atomic move or replacement. Replacement and old-reader behavior are tested on local Windows NTFS. Repeating a dataset/month intentionally replaces its file, including header-only zero-row output; later different-coordinate requests for the same month use the same filename. Errors and cancellation before publication preserve the old file and attempt temporary cleanup. Publication is the commit point; cancellation after that point does not undo success. A changed unsafe root or cleanup I/O failure leaves a safe warning code and may require operator cleanup of the temporary file. No copy/delete publication fallback is used. Tests use isolated temporary directories, synthetic settings and injected I/O faults.

## Forces endpoint

With the HTTPS launch profile running, send a POST with no parameters or body to `https://localhost:7046/api/ingestion/forces` (also provided in `Test Proj/Test Proj.http`). HTTP 200 means `Forces.csv` has already been published with exact columns `id,name`. Both upstream values must be nonblank; any invalid record fails the operation before export. The [official forces contract](https://data.police.uk/docs/method/forces/) was rechecked on 2026-09-16; the endpoint excludes British Transport Police.

Example response (completion time and count vary):

```json
{"success":true,"dataset":"forces","recordCount":1,"filename":"Forces.csv","completedAtUtc":"2026-09-16T10:00:00+00:00"}
```

The result exposes only the filename and records actually exported. Empty results publish a header-only file and return zero. Repeated requests replace Forces.csv. Errors return `application/problem+json` with stable `code` and `traceId`: contention 409; invalid upstream data or exhausted network/server failures 502; rate limiting or insufficient retry budget 503; retrieval timeout 504; local export/configuration or unexpected failure 500. Error responses and application error logs omit raw exception details, upstream bodies and paths. Caller cancellation propagates through retrieval/export and does not create a response to a disconnected caller. Cancellation after atomic publication cannot undo the committed file. The full operation deadline and HTTP resource limits are verified through the host.

## Crimes endpoint

POST JSON to `https://localhost:7046/api/ingestion/crimes` with `Content-Type: application/json`:

```json
{"latitude":53.8008,"longitude":-1.5491,"month":"2024-01"}
```

All three values are required. Coordinates must be finite and within [-90,90] / [-180,180]; month must be an ASCII calendar YYYY-MM (0001-01 through 9999-12). Invalid values return validation ProblemDetails 400 before HTTP or file work. A valid calendar month does not guarantee upstream availability. The route retrieves all crime categories around one point; it accepts no caller path, URL or category selector.

HTTP 200 returns the common result with dataset `crimes`, the published row count and filename `Crimes_2024-01.csv`. Exact columns are `id,persistent_id,category,month,latitude,longitude,street_id,street_name,location_type,context,outcome_category,outcome_date`. Crime id is a positive integer, category is nonblank and month must equal the requested month. Missing/null location, street, outcome and optional members become empty cells. Present coordinates must parse as finite invariant numbers within geographic bounds; street IDs are nonnegative integers. Wrong shapes fail the whole operation with sanitized 502 before export. Text is formula-protected, while negative coordinates remain numeric. Empty results replace the file with its header only. Repeating a month replaces the same file even for different coordinates.

The [official street crime contract](https://data.police.uk/docs/method/crime-street/) was rechecked on 2026-09-16 and its example frozen in tests. Locations are approximate. The service shares the forces admission gate and holds it from retrieval through publication. Cancellation, error and publication semantics are verified through the host as for forces.
## Stop/search endpoint

POST JSON `{"latitude":53.8008,"longitude":-1.5491,"month":"2024-01"}` to `https://localhost:7046/api/ingestion/stop-searches`. Validation and replacement rules match crimes. The code-owned stops-street query retrieves one point/month. HTTP 200 reports dataset `stop-searches`, the published count and filename `StopSearches_2024-01.csv`; no absolute path is returned.

Exact columns: `type,datetime,age_range,gender,self_defined_ethnicity,officer_defined_ethnicity,legislation,object_of_search,outcome,involved_person,operation,operation_name,latitude,longitude,street_id,street_name`. Type must be nonblank. Datetime must be a valid ISO timestamp with seconds and an explicit Z or numeric offset; up to seven fractional digits are retained. Export uses round-trip ISO formatting, preserving the offset without inferring local time. No fabricated identifier or derived demographics are added. Optional strings, location/street members and booleans may be absent/null; boolean cells distinguish true, false and empty. Optional coordinates and street IDs use the same validation as crimes. Invalid shapes fail the whole operation before export. Outcome accepts text, null, or the documented JSON false, exported as `false`; true/numbers/objects are rejected.

The [official area stop/search contract](https://data.police.uk/docs/method/stops-street/) was rechecked on 2026-09-16 and its first example record frozen in tests. Locations are approximate. Timestamps are preserved without imposing an additional returned-month equality rule. The service holds the shared admission lease through publication, writes header-only empty results, and uses the common cancellation and sanitized error behavior. Full operation budgets, input limits and host binding are verified for this route.
## Operational contracts

All ingestion routes buffer at most 4097 incoming bytes to enforce the default 4096-byte limit before model binding or ingestion side effects, including unknown-length/chunked input. Content-Length above the limit is rejected immediately. The request-specific server body limit is also set where supported. Operators may lower the validated limit. Invalid JSON/model binding returns sanitized validation ProblemDetails 400; unsupported content type returns sanitized 415. Framework parser messages, supplied field names and exception text are not reflected.

A TimeProvider-backed deadline starts before input reading and flows through request validation, retrieval/backoff, mapping and file writes/flush. It defaults to 120 seconds and may be reduced. Retry-After is compared against the remaining whole-operation budget, including time already spent on input. Only upstream GETs retry. All three services use the same process-wide lease from retrieval through atomic publication, with immediate 409 and no queue.

| Outcome | HTTP status | Stable code |
| --- | --- | --- |
| Invalid input | 400 | validation_failed |
| Another operation active | 409 | operation_busy |
| Request exceeds byte limit | 413 | request_body_too_large |
| Unsupported request media type | 415 | unsupported_media_type |
| Invalid upstream payload / byte or record limit | 502 | upstream_invalid_payload / upstream_limit_exceeded |
| Permanent upstream status or exhausted transient failures | 502 | upstream_failure |
| Exhausted rate limit / retry delay cannot fit | 503 | upstream_rate_limited / upstream_retry_budget |
| Retrieval timeout / full-operation deadline | 504 | upstream_timeout / operation_timeout |
| File failure / unexpected local failure | 500 | export_failed / internal_error |

Errors contain code and traceId with application/problem+json. Caller cancellation propagates through waits and I/O without attempting a disconnected response. Cancellation is cooperative: synchronous filesystem operations cannot be forcibly interrupted; the exporter checks cancellation before atomic publication. An already committed publication remains successful, and response serialization then uses the caller token rather than the expired operation token. No post-publication rollback or whole-export retry occurs. Cleanup failures preserve the primary result and log export_cleanup_failed; operator cleanup may be needed.

Safe application logs contain dataset, trace, attempt/status class (transport), published record count, elapsed time and result codes. They omit payloads, full query strings, credentials and local paths. Keep framework hosting/model-binding logs at the supplied Warning level; enabling verbose framework or HTTP body logging can disclose request data. OpenAPI declares success, validation and operational failure response types/media types for every route. Repeated successful calls intentionally replace the dataset/month file, including header-only empty results.

With the HTTPS profile running, Development exposes the generated document at `https://localhost:7046/openapi/v1.json`; Production does not expose it. The document declares required JSON bodies for crimes and stop/searches and an integer result count. Request members are nullable at the binding boundary so missing/null values can be rejected safely; the coordinate/month rules above are enforced by validation before upstream calls. There is no Swagger UI package.

The `.http` file contains all three requests. Equivalent PowerShell examples (each performs a real export when you run it):

```powershell
$base = 'https://localhost:7046/api/ingestion'
Invoke-RestMethod -Method Post -Uri "$base/forces"
$body = @{ latitude = 53.8008; longitude = -1.5491; month = '2024-01' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$base/crimes" -ContentType 'application/json' -Body $body
Invoke-RestMethod -Method Post -Uri "$base/stop-searches" -ContentType 'application/json' -Body $body
```

This is a trusted local service. Public hosting, authentication, distributed concurrency, output retention and filesystem permissions remain operator/deployment boundaries described in SECURITY.md. TestServer verifies the application pipeline; TLS and deployment-specific web server limits need verification in the intended deployment. The final isolated suite contains 566 passing cases (120 host integration cases), with requirement evidence in [TEST_STRATEGY.md](docs/TEST_STRATEGY.md) and verified commit/push records in [WORK_PROGRESS.md](docs/WORK_PROGRESS.md).

## Development documentation

- [Agent contract](AGENTS.md)
- [Requirements](docs/REQUIREMENTS.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Implementation plan](docs/IMPLEMENTATION_PLAN.md)
- [Workflow](docs/WORKFLOW.md)
- [Progress](docs/WORK_PROGRESS.md)
- [Test strategy](docs/TEST_STRATEGY.md)
- [Security](docs/SECURITY.md)
