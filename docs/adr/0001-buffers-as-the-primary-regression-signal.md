# 1. Shared buffers (hit + read) as the primary regression signal

- Status: accepted
- Date: 2026-11-12

## Context and problem statement

plan-guard has to decide, inside a CI job, whether a pull request made a query worse. A wrong "fail" teaches the
team to ignore or disable the check; a missed regression ships to production. CI runners are shared, noisy
machines: the same query on the same data can take twice as long from one run to the next. Which measurement
separates a real plan regression from noise?

## Considered options

1. **Execution time** from `EXPLAIN ANALYZE` (single run, or the median of several).
2. **Planner cost** (`Total Cost`) from `EXPLAIN`.
3. **Plan shape**: fail when the plan changes at all.
4. **Shared buffers** touched by the executor (`Shared Hit Blocks + Shared Read Blocks` of the root node), the
   median of several runs.

## Decision outcome

Chosen: **4, shared buffers, as the only signal that fails a build by default**, with temp-file blocks (spills)
as a second failing signal. Cost and time are reported and *warn*; plan-shape changes are informational.

Thresholds need both a ratio and an absolute floor (default ×1.3 and +64 blocks = 512 KiB): the ratio alone fails
a 4-block query that now reads 12 blocks, the floor alone ignores a 10× blow-up of a small query.

The decision is backed by the lab in `docs/benchmarks.md`, which scores every option on the same captures:
13 scenarios (8 regressions, 4 neutral changes, 1 improvement) plus 20 A/A repetitions on unchanged code, on a quiet
host and with background load.

### Why the others were not chosen as the gate

- **Time** is the number users care about, but it measures the runner as much as the query. The lab shows
  single-run time (>1.2x) flagged 10% of unchanged query pairs on a quiet host and 36.5% under load, which would
  have failed 12 and 20 of 20 unchanged builds; even the median of five needs generous thresholds
  (×2 and +5 ms) to stay quiet, and then it misses regressions on small, fast queries whose relative change is
  large but absolute change is a few milliseconds on test-sized data.
- **Cost** did well in the lab: no false alarms, re-ANALYZE included, and 7 of 8 regressions. But it is an
  estimate built on sampled statistics: it missed the work_mem spill (x1.35 below the x1.5 threshold), and a
  misestimate (stale statistics, correlated columns) moves it in either direction without the query doing more or
  less work. It warns for measured catalog queries and fails discovered statements, which have no runtime numbers.
- **Plan shape** changes for good reasons too (a new index, a different join order on the same work). Failing on
  every change would fail improvements.
- **Buffers** depend only on the plan and the data. Counting hits *and* reads makes the number independent of
  cache state, so a cold runner does not look like a regression. The executor visits the same pages every run;
  the lab measured identical counts in every run and capture for 9 of 10 queries (the tenth, a parallel
  aggregate, varied by 0.02%), while single-run times varied by up to 342x under load.

### Consequences

- Good: near-zero false positives without tuning; failures come with a concrete "×250 more pages" figure.
- Good: regressions invisible to plan-shape diffing (OFFSET pagination, work_mem spills) are caught.
- Bad: a query whose page count is unchanged but which became CPU-bound (an expensive function in the filter,
  a hash join that blows up in memory without spilling) is only *warned* about through time. Teams who want such
  cases to fail can set `timeGate: Fail` and accept more noise.
- Bad: buffers are a count of 8 KiB page visits, not bytes read from disk; a buffer hit is far cheaper than a read.
  That is the price of cache independence.
- Known edge: PostgreSQL may report slightly different buffer counts for the first execution in a session
  (catalog and sort setup); plan-guard runs a warm-up execution that is not measured.

## References

- PostgreSQL documentation, `EXPLAIN` (BUFFERS, GENERIC_PLAN, SETTINGS options): https://www.postgresql.org/docs/current/sql-explain.html
- pgsql-hackers, "Non-deterministic buffer counts reported in execution with EXPLAIN ANALYZE BUFFERS" (first-execution differences, raised by an author of a buffer-based regression tool): https://www.postgresql.org/message-id/f035dbd6-e8a7-4660-a54d-75a01f43fc32%40vondra.me
- boringSQL, "EXPLAIN BUFFERS": https://boringsql.com/posts/explain-buffers/
- pganalyze, "EXPLAIN (ANALYZE, BUFFERS) and interpreting shared buffers hit in Nested Loops": https://pganalyze.com/blog/5mins-explain-analyze-buffers-nested-loops
