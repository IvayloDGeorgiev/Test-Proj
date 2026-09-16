# Operating workflow

## Exact execution order

1. Project and test foundation — `stage/01-project-foundation`
2. Police API transport and validation — `stage/02-police-api-client`
3. Safe CSV and file export — `stage/03-csv-export`
4. Forces ingestion endpoint — `stage/04-forces-ingestion`
5. Crime ingestion endpoint — `stage/05-crime-ingestion`
6. Stop-and-search ingestion endpoint — `stage/06-stop-search-ingestion`
7. API limits and operational contracts — `stage/07-api-hardening`
8. Integration and final verification — `stage/08-integration-verification`
9. PostgreSQL persistence, data sync and simple frontend — `stage/09-postgres-sync-frontend`

Names/order mirror IMPLEMENTATION_PLAN.md and WORK_PROGRESS.md. This planning task implements none of them.

## Start and dependency checks

1. Read AGENTS.md, README and all seven docs. Inspect status, branch, diff filenames, worktrees and history without reading local secret files.
2. Select the first incomplete stage. If BLOCKED, resolve its documented blocker; never skip it. If IN PROGRESS elsewhere, do not start a duplicate.
3. Fetch origin without printing credentials. Verify predecessor implementation SHA and its completion receipt exist remotely and `git merge-base --is-ancestor <implementation-sha> <base-ref>` succeeds. Stage 1 starts from pushed docs/project-planning. Later stages start from the predecessor's latest pushed tip including its completion record.
4. Use an isolated worktree. If the app provides detached HEAD, create the exact stage branch before edits. If the branch already exists, inspect/resume its ownership and state instead of resetting it. Never share a branch across active worktrees.
5. Branch from updated main only if ancestry proves all prior stages and receipts are included. Default is a stack of stage branches; no automatic merge to main and no force pushes. Preserve unrelated changes.
6. Mark the selected stage IN PROGRESS, started UTC, base SHA, branch and task identity in progress. Commit/push that claim before substantial work; inspect existing remote branch/tasks first. A competing branch creation/push means stop and reconcile, never force overwrite.

## Implement, investigate, verify

Implement one coherent increment -> genuine AAA tests -> targeted test.
Failure -> read actual error -> reproduce -> inspect relevant safe output/code -> root cause -> fix -> regression test -> targeted retest -> broader affected tests -> full suite.
Then full stage tests -> build -> requirements/architecture/security review -> progress update -> diff/status/secret review -> commit -> push -> receipt -> next task -> STOP.

Record meaningful issues with symptom, investigation, root cause, resolution, regression test and decisions. No fake tests, weakened assertions, deleted valid failures, silent catches or fabricated command results.

## Full completion gate

- All scoped functionality/acceptance is implemented and matches REQUIREMENTS.md and ARCHITECTURE.md.
- Required unit tests are real AAA with meaningful assertions; no manipulation to pass.
- Targeted tests and ALL existing solution tests pass with nonzero discovery; build passes; no unresolved implementation errors.
- Record commands, test counts, date/exit status, build results and issue resolutions in progress.
- Documentation is consistent; generated CSV and local configuration are excluded; intended diff and status reviewed.
- Before EVERY staging/commit, from app directory run `git check-ignore appsettings.Local.json`; from root use `git check-ignore "Test Proj/appsettings.Local.json"`. Also ensure `git ls-files -- "Test Proj/appsettings.Local.json"` and staged names contain no local configuration. Ignore alone is insufficient if tracked.
- Stage explicit intended files, inspect `git diff --cached --name-only`, `git diff --cached --check`, and the safe staged diff. Commit coherent change(s), push the exact branch, and verify remote SHA matches expected.
- Only after implementation push succeeds may the stage record become COMPLETED. Successor must also verify the completion receipt push.

### Two-commit completion receipt

A commit cannot contain its own SHA or prove its own future push. Avoid that circular claim:

1. Keep status IN PROGRESS while recording successful code/test/build evidence. Commit implementation and progress as commit A. Push A and verify origin branch tip equals A.
2. Record A's SHA, successful push evidence and COMPLETED in WORK_PROGRESS.md. Commit this documentation-only receipt B and push. Verify remote tip equals B. B does not claim its own SHA in its contents; Git history supplies it.
3. Next stage uses B (or a later dispatch-only descendant) as base and verifies A ancestry plus remote receipt before beginning.
4. If B push fails, effective gate remains incomplete. Locally record BLOCKED/push failure, repair/retry safely, and do not dispatch. On restart verify remote evidence rather than trusting the word COMPLETED in a local file.

Planning uses an equivalent planning commit and optional receipt; no implementation tests are fabricated for documentation-only work.

## Failure behaviour

Investigate and fix within the same stage. If unable to safely resolve, record BLOCKED, exact failure, investigated evidence, known/suspected cause, attempted solutions and concrete unblock action. Commit/push only safe recoverable progress if possible; label incomplete. Missing SDK, restore/network/auth permissions, denied pushes, test/build failures or uncertain dispatch all block dependent execution. Never put credentials into shell commands as a workaround. Report actual limitations.

## Fresh task chaining and duplicate protection

This environment exposes list_projects, create_thread and task inspection tools. Use actual returned identifiers; never invent task creation or success. Worktree support is described in [official documentation](https://learn.chatgpt.com/docs/environments/git-worktrees). Ignored local secrets are not required in a fresh worktree.

After the entire gate, inspect existing tasks for this repo/stage and the progress dispatch ledger. One coordinator owns dispatch. Commit/push a dispatch intent (stage, predecessor tip, stable key repository+stage, state PENDING) before calling task creation. Create exactly ONE task on the matching saved Git project, environment worktree with startingState branch set to the completed predecessor branch (never the project default/main implicitly). Assign only the next stage with the prompt below.

Record returned task/client ID and timestamp in progress, commit/push a dispatch-only update. A child created before that ledger update must inspect origin's predecessor branch and existing task state before claiming work. Task records and the branch claim jointly prevent duplicate implementation. If creation times out or result is ambiguous, inspect tasks using the dispatch key; do not blindly retry. If still uncertain, mark dispatch BLOCKED for manual reconciliation. A pending worktree client ID is not a usable thread ID.

Never queue N+2/N+3. No guessed durations or recurring timer substitutes for dependency gates. If only scheduling is available, schedule ONE standalone next task for earliest practical time after success. If unavailable, record manual handoff, output the prompt and stop. Task creation failure does not undo completed code but blocks automated handoff. Stage 9 is the current final requested stage.

### Handoff prompt template

> In this repository, execute ONLY Stage {N}: {name}, branch {stage-branch}. Start an isolated worktree from {completed-predecessor-branch} at its verified latest pushed completion state, never stale main. Read AGENTS.md, README.md and every docs file. Inspect Git, remote completion evidence, ancestry, task/dispatch ledger and stage ownership; do not duplicate active work. Claim and implement exactly this stage, add genuine AAA xUnit tests, investigate failures and add regressions, run targeted/full tests and build, update WORK_PROGRESS.md, review secrets/diff/status, commit and push. Never read/disclose/commit appsettings.Local.json. Follow WORKFLOW.md's two-commit receipt and full gate. Only after verified completion create one isolated fresh task for Stage {N+1}; if unsupported emit its exact prompt. Stop this task after handoff. If blocked, record evidence and do not dispatch.

Planning may optionally launch Stage 1 after its push. Leaving it unlaunched is allowed; state that explicitly and provide a manual prompt.
