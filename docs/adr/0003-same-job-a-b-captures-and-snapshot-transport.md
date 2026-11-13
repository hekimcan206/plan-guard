# 3. Capture base and head in the same job; snapshots as the contract

- Status: accepted
- Date: 2026-11-12

## Context and problem statement

A comparison is only as good as the two captures are comparable. The baseline (target branch) and the candidate
(pull request) differ in code, but they should not differ in machine, PostgreSQL version, settings or data. How do
the two captures get produced, and how does the baseline reach the job that compares?

## Considered options

1. **Stored baseline**: capture `main` after every merge, store it (artifact or database), compare each PR
   against the latest stored capture.
2. **Same-job A/B**: in the PR job, build the database for the base commit, capture, rebuild it for the head
   commit, capture, compare.
3. **Committed baseline**: check the snapshot into the repository.

## Decision outcome

Chosen: **2 as the recommended workflow, with 1 supported** (`--store-connection`, `--baseline-label`).
Rejected: 3.

- Same-job captures run on the same runner, with the same PostgreSQL image, settings and seed data, minutes
  apart. Buffer counts (ADR 0001) are machine-independent anyway, but time and the planner's view of the
  machine (`effective_cache_size`, parallel workers) are not.
- The stored baseline saves a second migration and capture per PR and keeps a history of plans over time
  (`plan-guard history`), at the price of comparing across runners and across time. Every snapshot records the
  server version and planner-relevant settings, and the report shows a warning when they differ.
- Committed snapshots go stale and turn every schema change into a merge conflict.

Snapshots are versioned JSON (`formatVersion`), read strictly (unknown version or duplicate ids fail), so the
capture and compare steps can run in different jobs or tool versions without silently misreading each other.

### Data

Plan regressions only show on realistic data volumes; an empty test database plans everything as a sequential
scan. The demo seeds 300,000 orders deterministically (`setseed`) in under a minute. Real projects should seed
synthetic data of production-like shape, or load production *statistics* without data (for example with the
`pg_regresql` extension referenced below), never production rows.

### Consequences

- Good: few environment differences between the two captures; environment differences that remain are reported.
- Bad: a PR job does the database setup twice (the lab measures about 2 s per 10-query capture on top of the
  seeding time).
- The GitHub Action exposes `capture`, `discover` and `compare` as separate commands so either workflow can be
  built from the same pieces.

## References

- GitHub Actions, workflow commands and job summaries: https://docs.github.com/actions/using-workflows/workflow-commands-for-github-actions
- pg_regresql (inject production statistics into a CI database): https://pgxn.org/dist/pg_regresql/
