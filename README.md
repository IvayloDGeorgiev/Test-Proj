# PoliceDataIngestion.Api

Planning and workflow setup for an existing ASP.NET Core Web API. **Ingestion is not implemented yet.** The repository still contains the WeatherForecast template.

The working product name is PoliceDataIngestion.Api; the existing project remains `Test Proj/Test Proj.csproj`, namespace `Test_Proj`, in `Test Proj.slnx`. It targets .NET 10 with controllers, nullable C#, and OpenAPI.

The planned application retrieves public [UK Police data](https://data.police.uk/docs/), validates/maps it, and exports Forces.csv, Crimes_YYYY-MM.csv and StopSearches_YYYY-MM.csv. Thin POST controllers call ingestion services, a typed HTTP client and a safe CSV exporter. No database is planned.

Local development will use a configurable export root, optionally resolved from the current user's Desktop. Callers cannot supply paths or upstream URLs. appsettings.Local.json is ignored and must never be committed or disclosed. Git authentication is tooling configuration.

From the repository root, the existing baseline can be built with:

```powershell
dotnet build "Test Proj.slnx"
dotnet run --project "Test Proj/Test Proj.csproj" --launch-profile https
```

These commands currently run the template, not ingestion. Stage 1 adds xUnit tests; later tests use isolated HTTP handlers and temporary directories, with no live API dependency.

## Development documentation

- [Agent contract](AGENTS.md)
- [Requirements](docs/REQUIREMENTS.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Implementation plan](docs/IMPLEMENTATION_PLAN.md)
- [Workflow](docs/WORKFLOW.md)
- [Progress](docs/WORK_PROGRESS.md)
- [Test strategy](docs/TEST_STRATEGY.md)
- [Security](docs/SECURITY.md)
