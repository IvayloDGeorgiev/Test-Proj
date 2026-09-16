# PoliceDataIngestion.Api

The existing .NET 10 controller application now has validated configuration and an xUnit foundation. **Ingestion is not implemented yet.** WeatherForecast remains until Stage 4.

The project remains `Test Proj/Test Proj.csproj`, namespace `Test_Proj`, in `Test Proj.slnx`. No database is planned. Later stages retrieve public UK Police data and export Forces.csv, Crimes_YYYY-MM.csv and StopSearches_YYYY-MM.csv.

From the repository root:

```powershell
dotnet restore "Test Proj.slnx"
dotnet test "Test Proj.slnx" --no-restore
dotnet build "Test Proj.slnx" --no-restore
dotnet run --project "Test Proj/Test Proj.csproj" --launch-profile https
```

Development resolves the current user's existing Desktop directory programmatically and selects its PoliceDataIngestion child as the output root. If Desktop is unavailable, configure `Export:OutputRoot` explicitly. Production always requires an explicit root. It must be an absolute local directory on a fixed Windows drive, without traversal, device names or links. The directory may be created later by the exporter; Stage 1 does not write exports or probe write permissions. The operator must own the directory and prevent untrusted writes. Stage 3 must recheck filesystem safety at write time.

An optional `appsettings.Local.json` loads only in Development, before service registration and startup validation. It overrides standard JSON; user secrets, environment variables (for example `Export__OutputRoot`) and command-line arguments retain higher priority. Local configuration is ignored, untracked and excluded from build/publish output. Do not copy it into worktrees. Git authentication belongs to development tooling, not the application.

Options use sections `PoliceApi` and `Export`. PoliceApi defaults/maxima are: BaseUrl `https://data.police.uk/api/`, AttemptTimeoutSeconds 30, TotalOperationTimeoutSeconds 120, MaximumAttempts 3, MaximumResponseBytes 33554432 and MaximumRecords 100000. Positive limits may be reduced; attempt timeout cannot exceed total timeout. Export defaults/maxima are MaximumRequestBodyBytes 4096, MaximumConcurrentOperations exactly 1 and MaximumQueuedOperations exactly 0. OutputRoot has no production default. Options are validated at startup, but transport, admission, request limits and export enforcement arrive in their planned stages. Restart after configuration changes; local JSON reload is disabled to avoid changing trusted paths mid-operation.

Foundation tests use synthetic configuration, temporary directories and a synthetic publish probe. They do not read local credentials, use the real Desktop or contact the Police API.

## Development documentation

- [Agent contract](AGENTS.md)
- [Requirements](docs/REQUIREMENTS.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Implementation plan](docs/IMPLEMENTATION_PLAN.md)
- [Workflow](docs/WORKFLOW.md)
- [Progress](docs/WORK_PROGRESS.md)
- [Test strategy](docs/TEST_STRATEGY.md)
- [Security](docs/SECURITY.md)
