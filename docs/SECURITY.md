# Security

## Stage 9 database and browser controls

DefaultConnection is supplied through trusted normal configuration; no actual connection values or credentials belong in examples, source, logs, tests or static assets. The design-time factory reads only the environment and does not inspect User Secrets/local JSON. Database errors return fixed codes; EF uses NullLoggerFactory and disables sensitive data/detailed errors; Npgsql parameter/error-detail logging is disabled. Connection and command timeouts are bounded. Database reads are paginated and historical updates load only matching keys. Schema changes are explicit EF migrations, separate from normal application startup.

PostgreSQL composite primary/unique source keys prevent duplicate identities; constraints protect metadata. Sync uses one transaction with a nonblocking advisory lock plus existing process admission, upstream bounds, token propagation and whole-operation deadline. Failure rolls back; a commit acknowledgment failure may be uncertain and must be checked by read/repeat. Stop/search query snapshots do not claim globally unique event identity. Stored public records may still be sensitive: database ACLs, encryption, backup and retention belong to the operator.

Static assets contain no database access or connection configuration. Stored values are rendered with textContent, never HTML execution. Same-origin fetch, restrictive CSP, nosniff, a required custom sync header and Origin checks prevent ordinary cross-origin browser sync requests. No permissive CORS or external scripts/fonts are used. These browser controls are not authentication and do not authorize public hosting. Data GETs use no-store. Operators must keep loopback/trusted local deployment and prevent untrusted local clients/host routing.

Automated tests launch a disposable PostgreSQL 16 cluster on loopback with a random port and generated per-test databases, using test-only local trust authentication and no passwords. Only generated application tables from EF migrations are used; a test-only added constraint injects a rollback failure. The fixture stops the cluster and deletes only its validated GUID sandbox. Tests do not reuse the operator's PostgreSQL service or access private configuration. Node tests execute frontend behavior with synthetic DOM/fetch boundaries and no network.

## Secrets and Git

Never read/display appsettings.Local.json merely to prove credentials exist. Keep it ignored and untracked, including nested copies; before staging/committing run git check-ignore "Test Proj/appsettings.Local.json" from the root and inspect staged filenames. Stage explicit intended paths only. Never include credentials in examples, tests, source, docs, logs, chat or command lines. Do not copy the local file into isolated worktrees or published output. Stage 1 explicitly excludes it from build/publish content.

Git uses the existing credential manager/SSH/tool authentication. A development token is not an application dependency; no GitHub services or API keys for the public Police API. If tracking/exposure is discovered, stop disclosure, remove it from the proposed index, record only the affected filename, and request credential rotation through the owner; do not rewrite shared history autonomously.

## Threats and controls

| Threat | Required control and evidence |
| --- | --- |
| SSRF or redirect escape | Startup validates exact HTTPS Police origin/default port; paths code-owned; redirects disabled; handler tests prove no caller URL reaches transport |
| Invalid queries | Finite bounded coordinates and exact calendar month; reject before side effects; no polygon/batch/category expansion |
| Traversal/arbitrary writes | No path input; generated names; canonical root-plus-separator containment; reject links/reparse components |
| Filesystem race | Root owned by process account, inaccessible to untrusted writers; local single-process supported boundary; no claim that lexical containment alone prevents symlink attacks |
| Partial/corrupt export | Same-directory temp + atomic publication; old output survives error; reject competing operation; failure/cancellation tests |
| Spreadsheet formula execution | Apostrophe-prefix dangerous text after inspecting leading whitespace/control characters, then CSV escaping; numeric fields separately validated |
| Resource exhaustion | 4 KiB input; 32 MiB upstream; 100,000 rows; one operation, zero queue; 120s total, 30s attempts, three attempts maximum |
| Retry storm/rate abuse | Retry only transient GET failures; respect Retry-After or stop; capped jitter/backoff; no nested retries or automatic whole-export retry |
| Information leakage | Sanitized ProblemDetails; no stack traces, upstream bodies, paths/config in responses or logs; structured safe fields only |
| Untrusted upstream content | Strict required-field/shape validation, nullable optional fields, no executable interpretation of data |
| Unsafe deployment | Initially trusted local machine only; no public listener/reverse-proxy deployment without a separate authentication, authorization and rate-limit review |
| Dependency risk | Minimal packages, compatible pinned versions, official package sources; review new dependencies and vulnerability/audit output; never suppress warnings without documented assessment |

Local HTTP launch settings are template development settings, not production guidance; use HTTPS profile. AllowedHosts '*' is baseline template configuration and must be narrowed for any deployed environment. HTTPS redirection alone is not access control.

Configure the output root through trusted operator settings. Desktop fallback is Development-only and programmatic; missing Desktop means explicit root required. Avoid network shares where rename/replace guarantees are unclear. Validate safe upper bounds for configurable limits, not only positivity. Cancellation must terminate both waits and I/O and release the lease.

CSV protective prefixes deliberately alter unsafe text cells. CSV quoting alone does not prevent formulas. Tests include =,+,-,@, leading spaces, tabs, CR/LF and negative numeric coordinates.

Stage 3 tests exercise live Windows junctions (including dangling roots and linked ancestors/destinations), write-time root changes, exclusive temporary handles, actual locked destination failures, injected write/flush/permission/publication failures, cancellation during writes, and atomic replacement on NTFS. Link tests run without capability skips. Cleanup refuses to follow an altered root; a cleanup failure logs only a stable code and may leave a temporary file for the operator. Root ownership remains mandatory because path checks and publication are separate OS operations. Atomic replacement is verified for the local NTFS filesystem used by the test host; other local filesystem deployments must verify their rename/replace guarantees before use.

Exports may include sensitive public records; avoid logging row content and do not commit generated CSVs. Place production/local exports outside the repository. Output retention and access permissions are operator responsibilities; no automatic deletion policy is introduced.

Security is implemented in Stages 1-7 with each relevant feature and verified end-to-end in Stage 8. Reference [Police API call limits](https://data.police.uk/docs/api-call-limits/) during implementation; application budgets must remain conservative even if upstream limits change.

Stage 7 enforces input bytes before model binding (including chunked input) and links the full request deadline through export I/O. Request-local budget state is isolated across concurrent requests. API tests exercise deadline/caller distinctions, committed output, global cross-route admission, oversized upstream/rows, cleanup failure and synthetic sensitive validation/exception text. Framework binding/media-type errors use fixed sanitized messages. Default Microsoft.AspNetCore Warning logging avoids verbose request/query and model-binding logs; operators must not enable payload/verbose framework logging for sensitive records. The official call-limit page was rechecked on 2026-09-16: upstream uses 429 for exceeded limits. Existing conservative one-operation/three-attempt budgets remain unchanged.

## Final verification evidence

Stage 8 verifies the real host with 120 isolated integration cases. Request binding/limits reject before upstream or filesystem work; generated OpenAPI matches the required body and response contracts. Synthetic sensitive upstream text and exception messages are absent from ProblemDetails and captured application/framework logs in both environments. All nine cross-route retrieval contention pairs and contention during writes share immediate admission. Upstream byte/record limits, input/read/write deadlines, caller cancellation, cleanup failures and publication failures preserve the documented safe outcomes; completed publication still produces complete JSON when the operation timer expires.

Tests substitute a fake handler at the HTTP boundary, use synthetic configuration and a temporary content root, and write only in uniquely owned temporary sandboxes. They do not read real local settings, use the real Desktop or contact the Police API. Required legacy filesystem/junction tests also pass with no skipped cases. TestServer cannot establish deployment TLS/reverse-proxy behavior; operator ownership and local filesystem support remain mandatory. No public hosting or authentication scope was added.

Microsoft.AspNetCore.Mvc.Testing 10.0.12 is the only new direct dependency, test-only and aligned with the application's Microsoft.AspNetCore.OpenApi 10.0.12. Official NuGet package metadata was checked on 2026-09-16. Restore had no audit warnings; `dotnet list "Test Proj.slnx" package --vulnerable --include-transitive --no-restore` reported no vulnerable packages for either project from the configured sources on that date. This is a point-in-time check, not a future vulnerability guarantee. Full requirement-to-test traceability is in TEST_STRATEGY.md.
