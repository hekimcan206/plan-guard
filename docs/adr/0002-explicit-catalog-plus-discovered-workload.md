# 2. An explicit query catalog, plus workload discovery from pg_stat_statements

- Status: accepted
- Date: 2026-11-12

## Context and problem statement

To compare plans, plan-guard needs to know *which* queries to plan. Applications build SQL in ORMs, query
builders and stored procedures; the text is rarely available as files. Where do the queries come from?

## Considered options

1. **Catalog files**: one `.sql` file per important query, with literal values, kept in the repository.
2. **Discovery**: run the application's own test suite against the CI database, read the statements it executed
   from `pg_stat_statements` and plan them.
3. **Log parsing** with `auto_explain` during the test run.

## Decision outcome

Chosen: **1 and 2, as two capture commands that produce the same snapshot format.**

- `capture` runs the catalog with `EXPLAIN (ANALYZE, BUFFERS, VERBOSE, SETTINGS, FORMAT JSON)`: real executions,
  real buffer counts, chosen parameter values that represent the hot path (the customer with many orders, the
  busiest store).
- `discover` reads `pg_stat_statements` after the test suite ran and plans every normalised statement with
  `EXPLAIN (GENERIC_PLAN)` (PostgreSQL 16+). No parameter values are needed and nothing is executed, so it covers
  every statement the tests touched at zero authoring cost. The evidence is the planner's estimate, so a cost
  regression fails a discovered statement (ADR 0001 explains why cost only warns for catalog queries); per-call
  buffers from `pg_stat_statements` are reported as a warning because the tests' parameters may differ between runs.

`auto_explain` was rejected: it needs `shared_preload_libraries` changes *and* log scraping, and only logs plans
above a duration threshold, which reintroduces the timing noise the design avoids.

### Matching queries across captures

`pg_stat_statements.queryid` cannot identify a statement across captures: it is computed from the parse tree
including relation OIDs, so the same text has different ids in two databases built from the same migrations
(clones made with `CREATE DATABASE ... TEMPLATE` keep OIDs, which is why the lab can hide this; CI databases
usually are not clones). Catalog queries are keyed by file name, discovered statements by a SHA-256 of their
normalised text (comments and whitespace removed, literals kept).

### Generic plans through Npgsql

Sending `EXPLAIN (GENERIC_PLAN) SELECT ... $1` from Npgsql fails: the extended query protocol makes the server
treat `$1` as a parameter of the EXPLAIN itself, so with no values bound it refuses the statement, and with NULLs
bound it folds them into constants (`One-Time Filter: false` instead of the real plan). Discovery therefore runs
the EXPLAIN through dynamic `EXECUTE` in a `pg_temp` PL/pgSQL function created inside a rolled-back transaction;
the statement travels as a bound text value. Found while writing the integration tests.

### Consequences

- Good: teams can start with discovery (no authoring) and add catalog files for the queries that matter most.
- Good: one snapshot format, one comparison, one report.
- Bad: generic plans can differ from the custom plans the server uses for the first five executions of a prepared
  statement; skewed columns can make the generic plan unrepresentative.
- Bad: discovery requires `pg_stat_statements` preloaded on the CI database, and statements on temporary tables
  cannot be planned afterwards (they are reported as skipped).
- Statements that touch only system catalogs (driver type loading, `SELECT version()`) are skipped.

## References

- PostgreSQL documentation, `EXPLAIN` (GENERIC_PLAN, PostgreSQL 16+): https://www.postgresql.org/docs/current/sql-explain.html
- PostgreSQL documentation, pg_stat_statements: https://access.crunchydata.com/documentation/postgresql16/16.3/pgstatstatements.html
- `compute_query_id`: https://postgresqlco.nf/doc/en/param/compute_query_id/
- postgres.ai, "How to find query examples for problematic pg_stat_statements records": https://postgres.ai/docs/postgres-howtos/performance-optimization/query-tuning/from-pgss-to-explain--how-to-find-query-examples
- RegreSQL (a comparable tool comparing cost baselines of SQL files): https://boringsql.com/posts/regresql-testing-queries/
