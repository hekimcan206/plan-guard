# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-13

### Added

- `plan-guard capture`: runs a catalog of `.sql` files with `EXPLAIN (ANALYZE, BUFFERS, VERBOSE, SETTINGS,
  FORMAT JSON)`, one warm-up and N measured runs per query, each in a rolled-back transaction, and records
  pg_stat_statements deltas linked through the query identifier.
- `plan-guard discover`: plans every statement the application's test suite ran (pg_stat_statements) with
  `EXPLAIN (GENERIC_PLAN)` on PostgreSQL 16+, without parameter values and without executing anything.
- `plan-guard compare`: noise-aware comparison. Median shared buffers (hit + read) and temp-file blocks fail the
  build; planner cost and median time warn; plan-shape changes are reported with the operators removed and added.
  Every threshold needs both a ratio and an absolute floor.
- Reports: GitHub pull-request comment and job summary (Markdown), console summary, JSON report, Actions
  annotations. The comment degrades (plans, then details, then table rows) to stay under GitHub's size limit.
- `plan-guard comment`: creates or updates a single marked PR comment, editing only comments written by
  the account behind the workflow `GITHUB_TOKEN` (matched by user id 41898282, configurable with `--author-id`).
- Snapshot history store in PostgreSQL (`--store-connection`, `--baseline-label`, `plan-guard history`).
- Composite GitHub Action (`action.yml`) with `capture`, `discover` and `compare` commands, and an example
  same-job base/head workflow.
- Synthetic restaurant point-of-sale sample (300,000 orders, 900,000 lines, deterministic seed), 13 change
  scenarios, the `docker compose up` demo and the detector experiment (`plan-guard-lab`).
- Tests: unit, FsCheck property and golden tests; Testcontainers integration tests for every scenario on
  PostgreSQL 17, discovery, the store, privileges, timeouts, and capture on PostgreSQL 16 and 18.
- Benchmarks: detector experiment on a quiet and a loaded host, BenchmarkDotNet micro-benchmarks.
- ADRs, threat model, CI (build, test, pack, action self-test, image, compose demo), CodeQL.

[1.0.0]: https://github.com/hekimcan206/plan-guard/releases/tag/v1.0.0
