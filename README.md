# PoliceDataIngestion.Api

The existing .NET 10 controller application now has validated configuration, reusable Police API transport and request validation, safe CSV storage, and an xUnit suite. **Ingestion is not implemented yet.** WeatherForecast remains until Stage 4.

The project remains `Test Proj/Test Proj.csproj`, namespace `Test_Proj`, in `Test Proj.slnx`. No database is planned. Later stages retrieve public UK Police data and export Forces.csv, Crimes_YYYY-MM.csv and StopSearches_YYYY-MM.csv.

From the repository root:

```powershell
dotnet restore "Test Proj.slnx"
dotnet test "Test Proj.slnx" --no-restore
dotnet build "Test Proj.slnx" --no-restore
dotnet run --project "Test Proj/Test Proj.csproj" --launch-profile https
```

Development resolves the current user's existing Desktop directory programmatically and selects its PoliceDataIngestion child as the output root. If Desktop is unavailable, configure `Export:OutputRoot` explicitly. Production always requires an explicit root. It must be an absolute local directory on a fixed Windows drive, without traversal, device names or links. The exporter creates missing directories and rechecks root components and destination safety before writing and publication. The operator must own the directory and prevent untrusted writes.

An optional `appsettings.Local.json` loads only in Development, before service registration and startup validation. It overrides standard JSON; user secrets, environment variables (for example `Export__OutputRoot`) and command-line arguments retain higher priority. Local configuration is ignored, untracked and excluded from build/publish output. Do not copy it into worktrees. Git authentication belongs to development tooling, not the application.

Options use sections `PoliceApi` and `Export`. PoliceApi defaults/maxima are: BaseUrl `https://data.police.uk/api/`, AttemptTimeoutSeconds 30, TotalOperationTimeoutSeconds 120, MaximumAttempts 3, MaximumResponseBytes 33554432 and MaximumRecords 100000. Positive limits may be reduced; attempt timeout cannot exceed total timeout. Export defaults/maxima are MaximumRequestBodyBytes 4096, MaximumConcurrentOperations exactly 1 and MaximumQueuedOperations exactly 0. OutputRoot has no production default. Options are validated at startup. Transport and CSV storage enforce their limits now; incoming request limits and endpoint admission wiring arrive in their planned stages. Restart after configuration changes; local JSON reload is disabled to avoid changing trusted paths mid-operation.

Foundation tests use synthetic configuration, temporary directories and a synthetic publish probe. They do not read local credentials, use the real Desktop or contact the Police API.

Stage 2 registers `IPoliceApiClient` through the typed HttpClient factory. `GetForcesAsync` returns validated id/name records, including an empty list for an empty array. All requests use the trusted HTTPS origin; redirects, cookies and automatic decompression are disabled. Response bytes are bounded while reading, before JSON parsing, and record counts are checked before DTO materialization. Malformed data and permanent statuses are never retried. Transient GET failures have at most three attempts, 500 ms exponential backoff plus up to 250 ms jitter (5-second cap), and Retry-After support that never retries early. An unfittable retry delay fails with a classified 503; exhausted timeouts classify as 504. Attempt and total retrieval deadlines include body reads and backoff. Stage 7 will extend the operation deadline across export I/O.

`LocationMonth.Create` validates nullable finite coordinates and exact ASCII calendar months before query formatting; query numbers use invariant culture. These reusable values do not yet expose crime or stop/search calls. `PoliceApiException` contains safe codes/statuses only; HTTP ProblemDetails translation arrives with ingestion. Transport logs safe dataset/trace/attempt/status-class/count/elapsed/result fields and suppresses the factory's URL logging. Tests use scripted handlers and a manual clock, never live Police API calls or real retry sleeps.

Stage 3 registers `ICsvExporter` and a singleton `OperationLease`. Future ingestion services acquire a lease before retrieval, pass it to the exporter and dispose it in a `using`/`finally` scope. Admission rejects contention immediately with `operation_busy` (409); there is no queue. A lease permits only one write at a time. It remains occupied during an active write even if disposed early. Multiple processes sharing a root are unsupported.

CSV uses BOM-free UTF-8, CRLF records and code-owned schemas/filenames. Text containing commas, quotes or line breaks is quoted, with embedded quotes doubled. Dangerous text receives a leading apostrophe before quoting: formula markers after whitespace/control characters, or values starting with tab/CR/LF. This deliberately changes unsafe text values. Typed finite numbers use invariant formatting; negative coordinates remain numeric. Optional nulls are empty cells, booleans lowercase, and timestamps retain their offsets.

The exporter writes a unique same-directory temporary file, flushes and closes it, then uses an atomic move or replacement. Replacement and old-reader behavior are tested on local Windows NTFS. Repeating a dataset/month intentionally replaces its file, including header-only zero-row output; later different-coordinate requests for the same month use the same filename. Errors and cancellation before publication preserve the old file and attempt temporary cleanup. Publication is the commit point; cancellation after that point does not undo success. A changed unsafe root or cleanup I/O failure leaves a safe warning code and may require operator cleanup of the temporary file. No copy/delete publication fallback is used. Tests use isolated temporary directories, synthetic settings and injected I/O faults.

## Development documentation

- [Agent contract](AGENTS.md)
- [Requirements](docs/REQUIREMENTS.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Implementation plan](docs/IMPLEMENTATION_PLAN.md)
- [Workflow](docs/WORKFLOW.md)
- [Progress](docs/WORK_PROGRESS.md)
- [Test strategy](docs/TEST_STRATEGY.md)
- [Security](docs/SECURITY.md)
