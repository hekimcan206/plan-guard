# 4. Executing catalog queries safely: rolled-back transactions, one statement, bounded time

- Status: accepted
- Date: 2026-11-12

## Context and problem statement

`EXPLAIN ANALYZE` executes the statement. The catalog contains `UPDATE`/`DELETE` statements as well as reads, the
SQL text cannot be parameterised (it is the thing being explained), and plan-guard often runs with a privileged CI
login. What stops a capture from changing data or running away?

## Decision outcome

- **Every EXPLAIN runs in its own transaction that is always rolled back**, including the plain EXPLAIN used to
  get the query id and the warm-up run. Data-changing catalog queries are measured with their real plan and buffer
  counts and leave no trace (the integration test reads the row before and after). Side effects outside
  transactional storage (sequence increments, `dblink`, `NOTIFY` is transactional and discarded) are documented as
  limitations.
- **One statement per file**: a small lexer that understands quotes, `E''` escapes, dollar quoting and nested
  comments refuses files with a second statement after a semicolon. Without it, `SELECT 1; DROP TABLE x` would run
  the second statement outside the EXPLAIN and its transaction. FsCheck properties cover literals containing
  semicolons and concatenated statements.
- **`statement_timeout`** (default 30 s) applies to every capture session; a query that hits it is recorded as an
  error (`57014`) and fails the comparison rather than hanging the job.
- **Session settings through `set_config($1, $2, false)`** with bound values; setting names are validated
  against `^[a-z_][a-z0-9_.]{0,62}$`. The history store's schema name is validated the same way, since identifiers
  cannot be bound.
- **Least privilege works**: with a login that cannot set `pg_stat_statements.track`, capture continues without
  statement counters; a query the login may not run is recorded as an error (`42501`) while the rest is captured.

## Consequences

- Good: the catalog can contain writes; running plan-guard twice gives the same numbers.
- Bad: a rolled-back write still generates WAL and dead tuples during the capture. Run captures against a CI
  database, never against production.
- Bad: the catalog is trusted code. plan-guard does not sandbox what a single statement can do (a function call can
  have side effects); repository review is the control, as for migrations.

## References

- PostgreSQL documentation, `EXPLAIN` ("keep in mind that the statement is actually executed when ANALYZE is used"):
  https://www.postgresql.org/docs/current/sql-explain.html
- PostgreSQL documentation, lexical structure (dollar quoting, escape strings): https://www.postgresql.org/docs/current/sql-syntax-lexical.html
