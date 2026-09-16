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

Stage 8 adds 120 WebApplicationFactory host cases (Microsoft.AspNetCore.Mvc.Testing 10.0.12, compatible with net10.0) within the existing test project. They replace upstream handlers/configuration and use temporary output roots. Integration tests supplement unit tests, never replace them.

Unit and required integration suites never call the live Police API. Optional manual smoke checks are explicitly labeled, bounded, outside the automated gate, and cannot turn a failing isolated test green. No live service availability is a completion prerequisite.

## Acceptance traceability

R1/R18: Stage 1; R5 and R8-R12 transport: Stage 2; R13-R17: Stage 3; R2-R4 and schema mapping: Stages 4-6; R7 and HTTP-facing R11/R12: Stage 7; R19/R20 apply to every stage. Stage 8 verifies all requirements through the host. SECURITY.md threats must have tests where enforceable, or an explicit operator boundary where OS ownership is required.

## Final requirement audit — 2026-09-16

The full isolated suite passes 566 cases, none skipped: 446 predecessor cases plus 120 Integration cases. Build has zero warnings/errors. Exact commands and issue/push evidence are in WORK_PROGRESS.md. Test groups below are under `tests/PoliceDataIngestion.Api.Tests/`; the checks supplement each other rather than claiming every OS/transport condition is a host test.

| Requirement | Concrete implementation and evidence |
| --- | --- |
| R1 | Original net10.0 csproj/slnx/namespace retained; Foundation startup/publish checks; Integration generated OpenAPI and real controller host; full build. |
| R2 | Integration `Post_ValidThenEmpty` runs all three POST routes with actual JSON binding; OpenAPI asserts exactly the three ingestion paths and no forces body. |
| R3 | Integration successful requests assert exact HTTPS origin, GET method, dataset path and invariant query; PoliceApi Registration verifies trusted origin and disabled redirects/cookies/decompression. |
| R4 | Integration exact result properties/count/time, exact published bytes, header-only replacement and publication-expired-deadline response; Forces/Crimes/StopSearches service count and commit-point tests. |
| R5 | Integration malformed/missing/null/nonfinite/out-of-range/invalid calendar/ASCII binding cases and inclusive boundaries; PoliceApi Validation exercises complete finite/month/culture matrix; invalid inputs make zero HTTP/file calls. |
| R6 | Request contract exposes only point/month; generated request schema property set asserted; code-owned query and filenames checked end-to-end. No batch/polygon/URL/path/category API. Unknown JSON members are ignored by normal binding and cannot select upstream or output. |
| R7 | Integration known/unknown-length 413 and exact 4096-byte acceptance, reduced upstream byte/row rejection, cross-route 409 and operation deadline; Api tests verify reduced limits and request-size server feature; Foundation rejects unsafe options. |
| R8 | PoliceApi transport tests verify System.Text.Json array/shape validation, body and response disposal, bounds, token propagation, typed DI and invariant queries; all dataset/Integration mappings use the real transport. |
| R9 | PoliceApi Transport/Cancellation covers retryable/permanent status and network matrix, three-attempt maximum, attempt/total/caller cancellation; Integration transient-transient-success asserts three GETs and one publication. |
| R10 | PoliceApi RetryAfter delta/date/past/malformed/minimum/backoff/jitter tests; Integration oversized Retry-After returns 503 with one GET; input consumes 60 seconds, making a 70-second Retry-After inadmissible in the remaining budget. |
| R11 | Integration status/code/media-type table covers 400/409/413/415/502/503/504/500, both environments, caller cancellation and no false publication; Api/ForcesError tests cover already-started/cancelled response writes and safe arbitrary exceptions. |
| R12 | Integration captures logging and rejects synthetic private marker, payload/query coordinates and root paths; checks published and safe failure codes; PoliceApi/Api log tests cover allowed structured metadata and absence of exception objects. |
| R13 | Integration exact UTF-8/CRLF rows, Unicode/quotes, null cells and timestamp offset; Export and dataset suites exhaustively verify schemas, typed numeric/date/boolean formatting and escaping. |
| R14 | Integration formula-bearing force text and negative numeric crime longitude; Export full formula/leading-control/escaping matrix; README describes intentional protective changes. |
| R15 | Export FilesystemSafety/Resolve and Foundation cover traversal, containment, device names, ancestor/root/destination links and write-time checks; Integration filenames expose no path. Account-owned local root is an operator boundary. |
| R16 | Integration publication failure/write cancellation preserve old file and clean temps; cleanup failure retains primary error and safe warning. Export tests verify exclusive same-directory temps, close/flush ordering, actual NTFS atomic replacement/old readers and locked destination failure. |
| R17 | Integration all nine active/contender route pairs and all contenders during writes, then successful readmission; empty replacement tested on every route. OperationLease tests cover cross-scope singleton, foreign/disposed leases and premature release. Multi-process shared roots unsupported. |
| R18 | Foundation synthetic provider timing/precedence, Desktop fallback and safe startup validation/publish exclusion; PoliceApi Registration origin/redirect controls; Integration verifies temporary content/output roots in both environments. |
| R19 | Genuine xUnit AAA tests retained and extended; restore, exact Integration filter, full suite, build and dependency audit recorded. Workflow requires the claim and two-commit completion receipt to be independently verified remotely before completion. |
| R20 | Security controls are implemented across Stages 1–7 and exercised in final Integration tests. SECURITY.md maps enforceable controls and explicit operator boundaries. No deferred implementation control or live API dependency. |

Integration test sources: [host harness](../tests/PoliceDataIngestion.Api.Tests/Integration/IngestionHost.cs), [binding/output contracts](../tests/PoliceDataIngestion.Api.Tests/Integration/HostContractsTests.cs), [failures/OpenAPI](../tests/PoliceDataIngestion.Api.Tests/Integration/HostFailureTests.cs), [contention/cancellation](../tests/PoliceDataIngestion.Api.Tests/Integration/HostCancellationTests.cs).

The final host suite uses TestServer, so it verifies the ASP.NET application pipeline, not real socket/TLS behavior, IIS/reverse-proxy limits or production filesystem permissions. No live API smoke test was run or required. Safe production deployment, output retention/access control and unsupported shared-root use remain documented operator concerns rather than fabricated test coverage.
