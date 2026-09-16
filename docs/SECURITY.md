# Security

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

Exports may include sensitive public records; avoid logging row content and do not commit generated CSVs. Place production/local exports outside the repository. Output retention and access permissions are operator responsibilities; no automatic deletion policy is introduced.

Security is implemented in Stages 1-7 with each relevant feature and verified end-to-end in Stage 8. Reference [Police API call limits](https://data.police.uk/docs/api-call-limits/) during implementation; application budgets must remain conservative even if upstream limits change.
