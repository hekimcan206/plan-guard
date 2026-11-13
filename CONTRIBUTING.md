# Contributing

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker. The integration tests start `postgres:17-alpine`, `postgres:16-alpine` and `postgres:18` through
  Testcontainers, and `docker compose up` runs the demo.

## Workflow

1. Open an issue first for anything larger than a small fix.
2. Branch from `main`; one concern per pull request.
3. Before pushing:

   ```bash
   dotnet build                          # warnings are errors
   dotnet test                           # unit + FsCheck properties + golden files + Testcontainers suites
   dotnet format --verify-no-changes
   ```

## Golden files

Report and plan-rendering output is pinned in `tests/PlanGuard.UnitTests/Fixtures/golden`. After an intentional
change, regenerate with `PLANGUARD_UPDATE_GOLDEN=1 dotnet test --project tests/PlanGuard.UnitTests` and review the
diff like code. The EXPLAIN fixtures in `Fixtures/plans` are real PostgreSQL 17 output for the shop sample.

## Changing the comparison policy

The policy is the product. A change to a default threshold or gate needs:

1. the lab re-run (`plan-guard-lab experiment`, see `docs/benchmarks.md`) showing detection and false-positive
   rates before and after;
2. updated numbers in `docs/benchmarks.md` and the README;
3. a new ADR if it changes which signal fails a build (ADR 0001).

New regression patterns belong in `src/PlanGuard.Lab/Scenarios.cs`; the integration tests pick every scenario up
automatically.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) keep the history readable and make the hand-written
CHANGELOG easy to assemble at release time (`feat:`, `fix:`, `perf:` are user-visible; `docs:`, `test:`,
`build:`, `ci:`, `refactor:`, `chore:` are not).

## Architecture decisions

A change to a decision in `docs/adr/` needs a new ADR that supersedes the old one (MADR format).
