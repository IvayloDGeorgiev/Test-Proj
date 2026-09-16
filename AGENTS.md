# Agent operating contract

Read this file, README.md, and all seven docs files before work: REQUIREMENTS.md, ARCHITECTURE.md, IMPLEMENTATION_PLAN.md, WORKFLOW.md, WORK_PROGRESS.md, TEST_STRATEGY.md, SECURITY.md.

- Repository evidence and WORK_PROGRESS.md are the source of truth. Execute exactly ONE top-level implementation stage per task. Never skip prerequisites or a failed stage.
- Preserve the existing net10.0 controller application, solution and namespace. Do not recreate the application. Follow the exact stage branches in the plan; use an isolated worktree and the latest completed predecessor including its completion receipt.
- Inspect status, branch, history and existing changes before editing. Do not overwrite unrelated work, force push, or reset it. Check prerequisite commit ancestry and remote completion.
- Never display, read for proof, copy, log, stage or commit appsettings.Local.json or credentials. Before EACH staging/commit run git check-ignore against the actual local configuration path and verify it is not tracked/staged. Use explicit staging paths; never git add . or -A.
- Git credentials belong to development tooling, not application services. Examples contain empty values/placeholders only.
- Use thin controllers, DI, typed HttpClient, System.Text.Json, async operations, CancellationToken propagation, nullable types, validated options and safe ProblemDetails. Implement the security controls with each feature.
- Add genuine xUnit Arrange / Act / Assert unit tests with meaningful observable assertions. Never mock the system under test, fabricate passing tests, weaken assertions/validation, suppress defects, or delete legitimate failing tests to get green.
- Isolate HTTP tests with a fake HttpMessageHandler; never depend on the live Police API. Use temporary test output directories.
- Investigate actual failures: reproduce, inspect, identify root cause, fix, add regression coverage, rerun targeted then full tests and build.
- Update progress with evidence and meaningful issue notes. Review requirements, architecture, diff, status and secret safety. Commit coherent changes and push the stage branch.
- COMPLETED requires every gate in WORKFLOW.md, including successful tests/build and confirmed remote commits. Do not fabricate success. If blocked, record failure, investigation, attempted fixes and exact unblock action; do not launch a successor.
- Use the two-commit completion receipt procedure in WORKFLOW.md. A completion record alone is not proof its push succeeded.
- Only after success, create at most ONE fresh task for the next stage using the predecessor branch in an isolated worktree. Check duplicate dispatch records and existing tasks first. Never create stages ahead of time or use guessed durations. If unavailable, emit the exact next-stage prompt and stop.
