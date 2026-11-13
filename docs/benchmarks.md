# Benchmarks and the detector lab

Two kinds of measurement:

1. **The lab** (`plan-guard-lab experiment`): which signal tells a real query-plan regression from noise? Seven
   detectors are scored on the same captures: 13 change scenarios plus 20 A/A repetitions on unchanged code, on a
   quiet host and on a host with background load.
2. **Micro-benchmarks** (BenchmarkDotNet): what plan-guard itself costs per plan and per comparison.

All numbers below were measured on the machine described in [Environment](#environment); nothing is estimated.
The raw lab output is in [`benchmarks/results/experiment.md`](../benchmarks/results/experiment.md) and
[`experiment.json`](../benchmarks/results/experiment.json).

## Lab method

- **Data**: the synthetic shop database (`samples/shop`): 200 stores, 50,000 customers, 5,000 menu items,
  300,000 orders, 900,000 order lines, generated deterministically with `setseed`, then `VACUUM (ANALYZE)` once.
  Every capture runs on a `CREATE DATABASE ... TEMPLATE` copy, so all copies start from the same pages and the
  same statistics.
- **Catalog**: the 10 queries in `samples/shop/queries` (index lookups, a join report, aggregates, keyset
  pagination, a full-scan report, one `UPDATE`).
- **Capture**: 1 warm-up + 5 measured `EXPLAIN (ANALYZE, BUFFERS, VERBOSE, SETTINGS, FORMAT JSON)` runs per query,
  `jit = off`, everything else default.
- **Scenarios** (`src/PlanGuard.Lab/Scenarios.cs`): 8 regressions (dropped index, dropped FK index, non-sargable
  date filter, ILIKE defeating an expression index, partial-index predicate mismatch, OFFSET pagination,
  `work_mem = 64kB`, join through a dropped index), 4 neutral changes (unrelated index, nullable column,
  re-ANALYZE, equivalent rewrite) and 1 improvement (covering index).
- **A/A**: 20 captures of the unchanged database compared with the baseline (200 query pairs), then 20 more with a
  fresh `ANALYZE` before each capture (statistics re-sampled).
- **Conditions**: *quiet* (no extra load) and *loaded* (4 connections running CPU-heavy aggregates and full scans
  on a separate copy of the data, in the same PostgreSQL container, for the whole condition).
- **Detectors**: `time-1run-1.2x` (one EXPLAIN ANALYZE, >1.2x slower: the naive check), `time-median-1.2x`,
  `time-median-2x+5ms` (plan-guard's time thresholds), `cost-1.5x+50`, `plan-shape` (fingerprint changed),
  `buffers-1.3x+64` (median shared hit+read blocks), and `plan-guard-default` (the shipped policy: shared or temp
  blocks fail, cost and time warn).

## Lab results

### Regressions caught (8 scenarios, target query flagged)

| Detector | Quiet | Loaded | Missed |
|---|---:|---:|---|
| time, single run, >1.2x | 7/8 | 8/8 | work_mem spill (quiet) |
| time, median of 5, >1.2x | 8/8 | 7/8 | work_mem spill (loaded: 172 -> 189 ms is only x1.10) |
| time, median, >2x and +5 ms | 6/8 | 6/8 | non-sargable date (0.64 -> 1.43 ms, below the 5 ms floor), work_mem spill |
| cost >1.5x and +50 | 7/8 | 7/8 | work_mem spill (x1.35) |
| plan shape changed | 6/8 | 6/8 | non-sargable date, OFFSET pagination (same operators, far more work) |
| shared blocks >1.3x and +64 | 7/8 | 7/8 | work_mem spill (2,472 -> 2,508 shared; the extra work is in temp files) |
| **plan-guard default** | **8/8** | **8/8** | none: the spill fails on temp blocks (31 -> 320 quiet, 31 -> 318 loaded) |

`time-median-2x+5ms` stays quiet under load but missed the date-filter rewrite (0.64 -> 1.43 ms,
below the 5 ms floor), which on production-sized data would be a large regression. On test-sized
data most index-to-scan regressions cost a few milliseconds; buffers show them as x100 to x750.

### False alarms on unchanged code (A/A: 20 builds x 10 queries)

| Detector | Quiet: flagged pairs | builds failed | after ANALYZE | Loaded: flagged pairs | builds failed | after ANALYZE |
|---|---:|---:|---:|---:|---:|---:|
| time, single run, >1.2x | 20/200 (10.0%) | 12/20 | 14/20 | 73/200 (36.5%) | **20/20** | 20/20 |
| time, median of 5, >1.2x | 24/200 (12.0%) | 14/20 | 13/20 | 57/200 (28.5%) | 19/20 | 18/20 |
| time, median, >2x and +5 ms | 0/200 | 0/20 | 0/20 | 3/200 (1.5%) | 3/20 | 2/20 |
| cost >1.5x and +50 | 0/200 | 0/20 | 0/20 | 0/200 | 0/20 | 0/20 |
| plan shape changed | 0/200 | 0/20 | 0/20 | 0/200 | 0/20 | 0/20 |
| shared blocks >1.3x and +64 | 0/200 | 0/20 | 0/20 | 0/200 | 0/20 | 0/20 |
| **plan-guard default** | **0/200** | **0/20** | **0/20** | **0/200** | **0/20** | **0/20** |

The neutral scenarios agree: plan-guard's default policy failed none of the 4 neutral changes in either
condition and reported the covering index as *improved* (2,472 -> 157 shared blocks). Under load it emitted one
*warning* (time) on `customers_per_city` in five of the 13 scenario runs, which does not fail a build. The plan-shape
detector flagged the covering index, i.e. it would have failed an improvement.

### Why: noise per signal (baseline + 20 A/A captures, per query)

| Query | Quiet: median ms | CV single run | CV 5-run median | Loaded: CV single run | max/min single run | CV shared blocks |
|---|---:|---:|---:|---:|---:|---:|
| customer_recent_orders | 0.074 | 53.3% | 18.5% | 83.9% | 21.6 | 0.00% |
| order_detail | 0.068 | 158.0% | 20.7% | 168.8% | 37.3 | 0.00% |
| store_daily_revenue | 0.271 | 40.4% | 31.9% | 546.8% | 342.0 | 0.00% |
| store_top_items | 0.356 | 33.0% | 26.2% | 496.9% | 185.1 | 0.00% |
| customers_per_city | 7.617 | 17.3% | 10.9% | 91.7% | 14.7 | 0.00% |
| top_customers | 31.884 | 13.5% | 8.4% | 32.6% | 8.9 | 0.02% |

(CV = coefficient of variation; max/min is the loaded condition; all 10 queries are in the raw file.) Shared
block counts were identical across all runs and captures for 9 of 10 queries; `top_customers` varied by 0.02%
(a parallel hash aggregate). Every query kept exactly one plan across all 41 captures in both conditions, with and
without re-ANALYZE. Single-run times varied by up to 342x under load.

### Capture cost

Median wall time to capture the 10-query catalog (60 EXPLAIN executions plus pg_stat_statements reads):
**0.59 s** quiet, **2.28 s** loaded. Seeding the 300,000-order database took 55 s on the same 2-CPU container.

## Micro-benchmarks

BenchmarkDotNet 0.15.8, .NET 10.0.12, Release, default job, on the host (not in Docker). Medians. Raw reports:
[`micro-plan.md`](../benchmarks/results/micro-plan.md), [`micro-compare.md`](../benchmarks/results/micro-compare.md).

| Operation | Median | Allocated |
|---|---:|---:|
| Parse one EXPLAIN JSON (14 KB, 9 nodes) | 51.5 µs | 7.9 KB |
| Fingerprint one plan | 1.02 µs | 2.9 KB |
| Diff two plans | 2.67 µs | 7.2 KB |

| Catalog size | Compare | Render Markdown comment | Snapshot JSON round trip |
|---:|---:|---:|---:|
| 10 queries | 9.3 µs | 19.6 µs | 0.66 ms |
| 200 queries | 161 µs | 534 µs | 12.9 ms |
| 2,000 queries | 2.75 ms | 15.8 ms | 168 ms |

plan-guard's own work is negligible next to the database work it drives: parsing the plans of a 60-execution
capture costs about 3 ms, against 590 ms of capture time.

**Fixed along the way.** The first run showed compare growing quadratically: 18.24 ms for 2,000 queries
(10.29 µs for 10), because every query id was looked up with a linear scan of both snapshots
([`micro-compare-before-index.md`](../benchmarks/results/micro-compare-before-index.md)). Indexing both snapshots
by id once brought 2,000 queries to 2.75 ms (6.6x) and 200 queries from 327 µs to 161 µs.


## Environment

| | |
|---|---|
| CPU | 13th Gen Intel Core i7-13620H, 10 cores / 16 threads |
| RAM | 15.7 GB (Docker VM: 16 vCPUs, 7.6 GB, shared) |
| OS | Windows 11 Home 10.0.26200, Docker Desktop 29.9.1 |
| PostgreSQL | `postgres:17-alpine` (17.11) limited to `--cpus=2 --memory=2g --shm-size=256m` (a 2-vCPU private-repo GitHub runner has 2 vCPUs / 7 GB), `shared_preload_libraries=pg_stat_statements`, defaults otherwise |
| .NET | SDK 10.0.401, Release build |
| Host load | Not an idle machine: another build agent was running its own containers and builds in parallel, and 7 other containers (an idle Supabase stack) were up. The *loaded* condition adds deliberate load on top. |

## Reproduce

```bash
docker run -d --name planguard-lab-pg --cpus=2 --memory=2g --shm-size=256m -e POSTGRES_PASSWORD=lab \
  -p 127.0.0.1:55923:5432 postgres:17-alpine -c shared_preload_libraries=pg_stat_statements -c compute_query_id=on
export PLANGUARD_ADMIN_CONNECTION="Host=127.0.0.1;Port=55923;Username=postgres;Password=lab;Timeout=60;Command Timeout=600"
dotnet run --project src/PlanGuard.Lab -c Release -- experiment --out benchmarks/results --reps 20 --noise 4

cd benchmarks && dotnet run -c Release --project PlanGuard.Benchmarks -- --filter '*'
```

## Limits of this lab

- One schema, one data size (300k orders). Absolute times are small because the data fits in memory; that is
  also why the time thresholds miss regressions that buffers catch. On larger data time regressions grow, but so
  does the noise floor.
- The loaded condition puts the load inside the same PostgreSQL container (CPU and buffer contention). It does
  not model a noisy disk or a different runner for base and head.
- The thresholds were chosen before the run and not tuned to it; with 8 regression scenarios the detection rates
  are counts, not statistically strong rates.
