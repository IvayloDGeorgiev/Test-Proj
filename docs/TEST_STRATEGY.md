# Test strategy

## Rules and commands

Use xUnit net10.0 in tests/PoliceDataIngestion.Api.Tests, referenced by the existing solution. Name tests Behaviour_Condition_ExpectedResult. Arrange meaningful inputs/dependencies, Act on the real system under test, Assert observable output and relevant side effects; AAA comments are encouraged. A test must fail when its behaviour breaks. Never Assert.True(true), mock the SUT, weaken assertions, remove valid failing tests, swallow failures or disable tests without a documented legitimate reason.

From repository root after Stage 1:

```powershell
dotnet restore "Test Proj.slnx"
dotnet test "tests/PoliceDataIngestion.Api.Tests/PoliceDataIngestion.Api.Tests.csproj" --filter "FullyQualifiedName~Foundation" --no-restore
dotnet test "Test Proj.slnx" --no-restore
dotnet build "Test Proj.slnx" --no-restore
```

Replace Foundation with the stage's named test group. Review test counts and discovery: an exit code with zero discovered tests is not a passing suite. Capture command, date, exit code, counts and material warnings in WORK_PROGRESS.md. Full tests include all prior stages. Investigate the implementation when a valid test fails; add regression coverage for meaningful bugs.

## Boundaries and coverage

- Foundation: synthetic configuration proves Development-only provider timing and environment/CLI precedence; options validation rejects unsafe values. Never read actual local JSON in tests.
- Validation: inclusive latitude/longitude boundaries; values just outside, omitted, NaN/infinity, negative zero; ASCII month shape, valid/invalid calendar month/year; invalid input makes zero HTTP/file calls. Test with a non-English current culture too.
- HTTP: scripted fake HttpMessageHandler records request URI/method/token and returns queued responses/errors; assert deserialization, escaped invariant query values, relative paths and disposal. Exercise 200 array, empty array, null/empty body, malformed JSON, unexpected shapes, missing required fields and unknown extra fields.
- Resilience: 400/404 no retry; 408/429/500/502/503/504 bounded attempts; nonretryable status no retry; Retry-After delta/date/past/malformed/beyond budget; transport/DNS HttpRequestException; retry exhaustion; attempt vs total timeout; cancellation before send, during body and during backoff. Fake time/delay avoids real sleeps. Assert attempt count and delay bounds, not merely exception type.
- CSV: exact headers/order and UTF-8/CRLF; comma, quotes, CR, LF, multiline, empty/null and Unicode. Assert complete expected records. Formula markers with whitespace/control prefixes are neutralized; validated negative numbers stay numeric.
- Filesystem: unique temp directory per test; disposable cleanup; generated filename allowlist, traversal/absolute/UNC/drive paths, sibling-prefix containment traps, links/reparse points (platform-capability skips need a reason). Simulate permission/disk/write failure; old file stays intact, no final partial file, temp cleanup and lease release on cancellation/failure. Simultaneous requests prove contention and no interleaving. Never use the real Desktop.
- Services: fake client/export boundaries, real mapping/service; record counts, zero rows, optional nested fields, required field failure, no write after failure, cancellation propagation, no false success, intended replacement semantics.
- Controllers: direct controller tests prove result/service interaction; host integration tests prove model binding, status, media type, ProblemDetails and OpenAPI. Direct controller calls alone do not test automatic ApiController validation.
- Logging/errors: capture logging sink; ensure credentials, synthetic sensitive strings, paths and upstream body never appear in response or permitted log output.

## Integration and live API policy

Stage 8 adds WebApplicationFactory host tests (Microsoft.AspNetCore.Mvc.Testing compatible with net10.0) within the existing test project. They replace upstream handlers/configuration and use temporary output roots. Integration tests supplement unit tests, never replace them.

Unit and required integration suites never call the live Police API. Optional manual smoke checks are explicitly labeled, bounded, outside the automated gate, and cannot turn a failing isolated test green. No live service availability is a completion prerequisite.

## Acceptance traceability

R1/R18: Stage 1; R5 and R8-R12 transport: Stage 2; R13-R17: Stage 3; R2-R4 and schema mapping: Stages 4-6; R7 and HTTP-facing R11/R12: Stage 7; R19/R20 apply to every stage. Stage 8 verifies all requirements through the host. SECURITY.md threats must have tests where enforceable, or an explicit operator boundary where OS ownership is required.
