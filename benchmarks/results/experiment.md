## Condition: quiet (0 background load workers, PostgreSQL 17.11)

Median capture time for the 10-query catalog (1 warm-up + 5 runs each): 0.59 s.

### Scenario detection (target query flagged?)

| Scenario | Kind | Target | time-1run-1.2x | time-median-1.2x | time-median-2x+5ms | cost-1.5x+50 | plan-shape | buffers-1.3x+64 | plan-guard-default | plan-guard verdicts |
|---|---|---|---|---|---|---|---|---|---|---|
| drop-customer-index | Regression | customer_recent_orders | yes | yes | yes | yes | yes | yes | yes | customer_recent_orders=Fail |
| drop-fk-index | Regression | order_detail | yes | yes | yes | yes | yes | yes | yes | order_detail=Fail, store_top_items=Fail |
| non-sargable-date | Regression | store_daily_revenue | yes | yes | **no** | yes | **no** | yes | yes | store_daily_revenue=Fail |
| lost-functional-index | Regression | customer_by_email | yes | yes | yes | yes | yes | yes | yes | customer_by_email=Fail |
| partial-index-mismatch | Regression | open_orders_queue | yes | yes | yes | yes | yes | yes | yes | open_orders_queue=Fail |
| offset-pagination | Regression | orders_page | yes | yes | yes | yes | **no** | yes | yes | orders_page=Fail |
| work-mem-spill | Regression | top_customers | **no** | yes | **no** | **no** | yes | **no** | yes | top_customers=Fail |
| join-without-index | Regression | store_top_items | yes | yes | yes | yes | yes | yes | yes | order_detail=Fail, store_top_items=Fail |
| add-unrelated-index | Neutral | - | - | - | - | - | - | - | - | all pass |
| add-nullable-column | Neutral | - | **1 FP** | **2 FP** | - | - | - | - | - | all pass |
| reanalyze | Neutral | - | - | - | - | - | - | - | - | all pass |
| equivalent-rewrite | Neutral | customer_recent_orders | **1 FP** | - | - | - | - | - | - | all pass |
| covering-index | Improvement | top_customers | - | - | - | - | **1 FP** | - | - | top_customers=Improved |

### Target query numbers (baseline -> candidate, medians)

| Scenario | Shared blocks | Temp blocks | Cost | Time (ms) |
|---|---:|---:|---:|---:|
| drop-customer-index | 10 -> 2509 | 0 -> 0 | 27.8 -> 5678.4 | 0.091 -> 10.870 |
| drop-fk-index | 10 -> 7507 | 0 -> 0 | 41.8 -> 13204.6 | 0.055 -> 23.972 |
| non-sargable-date | 121 -> 1122 | 0 -> 0 | 505.1 -> 2330.0 | 0.639 -> 1.432 |
| lost-functional-index | 4 -> 569 | 0 -> 0 | 8.4 -> 1194.0 | 0.046 -> 19.551 |
| partial-index-mismatch | 21 -> 2512 | 0 -> 0 | 2.1 -> 5977.9 | 0.054 -> 20.369 |
| offset-pagination | 4 -> 1653 | 0 -> 0 | 2.3 -> 5139.6 | 0.055 -> 19.565 |
| work-mem-spill | 2472 -> 2508 | 31 -> 320 | 8152.6 -> 11003.3 | 31.884 -> 39.754 |
| join-without-index | 479 -> 7955 | 0 -> 0 | 468.4 -> 13389.7 | 0.688 -> 41.362 |
| equivalent-rewrite | 10 -> 10 | 0 -> 0 | 27.8 -> 27.8 | 0.091 -> 0.054 |
| covering-index | 2472 -> 157 | 31 -> 31 | 8152.6 -> 2439.9 | 31.884 -> 17.097 |

### False positives on unchanged code (A/A, 20 repetitions x 10 queries)

| Detector | Flagged query pairs | Builds that would fail | ... after a fresh ANALYZE: pairs | builds |
|---|---:|---:|---:|---:|
| time-1run-1.2x | 20/200 (10.0%) | 12/20 | 28/200 (14.0%) | 14/20 |
| time-median-1.2x | 24/200 (12.0%) | 14/20 | 36/200 (18.0%) | 13/20 |
| time-median-2x+5ms | 0/200 (0.0%) | 0/20 | 0/200 (0.0%) | 0/20 |
| cost-1.5x+50 | 0/200 (0.0%) | 0/20 | 0/200 (0.0%) | 0/20 |
| plan-shape | 0/200 (0.0%) | 0/20 | 0/200 (0.0%) | 0/20 |
| buffers-1.3x+64 | 0/200 (0.0%) | 0/20 | 0/200 (0.0%) | 0/20 |
| plan-guard-default | 0/200 (0.0%) | 0/20 | 0/200 (0.0%) | 0/20 |

### Noise profile per query (baseline + A/A captures)

| Query | Median time (ms) | CV single run | CV of 5-run median | max/min single run | Median shared blocks | CV shared blocks | Distinct plans |
|---|---:|---:|---:|---:|---:|---:|---:|
| correct_order_total | 0.097 | 28.6 % | 20.0 % | 3.64 | 21 | 0.00 % | 1 |
| customer_by_email | 0.034 | 41.5 % | 36.2 % | 4.60 | 4 | 0.00 % | 1 |
| customer_recent_orders | 0.074 | 53.3 % | 18.5 % | 8.73 | 10 | 0.00 % | 1 |
| customers_per_city | 7.617 | 17.3 % | 10.9 % | 2.32 | 569 | 0.00 % | 1 |
| open_orders_queue | 0.054 | 57.8 % | 19.4 % | 10.33 | 21 | 0.00 % | 1 |
| order_detail | 0.068 | 158.0 % | 20.7 % | 29.18 | 10 | 0.00 % | 1 |
| orders_page | 0.044 | 46.0 % | 41.1 % | 6.03 | 4 | 0.00 % | 1 |
| store_daily_revenue | 0.271 | 40.4 % | 31.9 % | 5.21 | 121 | 0.00 % | 1 |
| store_top_items | 0.356 | 33.0 % | 26.2 % | 3.46 | 479 | 0.00 % | 1 |
| top_customers | 31.884 | 13.5 % | 8.4 % | 2.01 | 2472 | 0.02 % | 1 |

## Condition: loaded (4 background load workers, PostgreSQL 17.11)

Median capture time for the 10-query catalog (1 warm-up + 5 runs each): 2.28 s.

### Scenario detection (target query flagged?)

| Scenario | Kind | Target | time-1run-1.2x | time-median-1.2x | time-median-2x+5ms | cost-1.5x+50 | plan-shape | buffers-1.3x+64 | plan-guard-default | plan-guard verdicts |
|---|---|---|---|---|---|---|---|---|---|---|
| drop-customer-index | Regression | customer_recent_orders | yes | yes | yes | yes | yes | yes | yes | customer_recent_orders=Fail, customers_per_city=Warn |
| drop-fk-index | Regression | order_detail | yes | yes | yes | yes | yes | yes | yes | order_detail=Fail, store_top_items=Fail |
| non-sargable-date | Regression | store_daily_revenue | yes | yes | **no** | yes | **no** | yes | yes | store_daily_revenue=Fail |
| lost-functional-index | Regression | customer_by_email | yes | yes | yes | yes | yes | yes | yes | customer_by_email=Fail |
| partial-index-mismatch | Regression | open_orders_queue | yes | yes | yes | yes | yes | yes | yes | customers_per_city=Warn, open_orders_queue=Fail |
| offset-pagination | Regression | orders_page | yes | yes | yes | yes | **no** | yes | yes | customers_per_city=Warn, orders_page=Fail |
| work-mem-spill | Regression | top_customers | yes | **no** | **no** | **no** | yes | **no** | yes | customers_per_city=Warn, top_customers=Fail |
| join-without-index | Regression | store_top_items | yes | yes | yes | yes | yes | yes | yes | order_detail=Fail, store_top_items=Fail |
| add-unrelated-index | Neutral | - | **3 FP** | **3 FP** | - | - | - | - | - | all pass |
| add-nullable-column | Neutral | - | **6 FP** | **2 FP** | - | - | - | - | - | all pass |
| reanalyze | Neutral | - | **5 FP** | **3 FP** | **1 FP** | - | - | - | - | customers_per_city=Warn |
| equivalent-rewrite | Neutral | customer_recent_orders | **2 FP** | **2 FP** | - | - | - | - | - | all pass |
| covering-index | Improvement | top_customers | **2 FP** | - | - | - | **1 FP** | - | - | top_customers=Improved |

### Target query numbers (baseline -> candidate, medians)

| Scenario | Shared blocks | Temp blocks | Cost | Time (ms) |
|---|---:|---:|---:|---:|
| drop-customer-index | 10 -> 2509 | 0 -> 0 | 27.8 -> 5678.4 | 0.102 -> 32.991 |
| drop-fk-index | 10 -> 7507 | 0 -> 0 | 41.8 -> 13204.6 | 0.107 -> 102.102 |
| non-sargable-date | 121 -> 1122 | 0 -> 0 | 505.1 -> 2330.0 | 0.339 -> 1.891 |
| lost-functional-index | 4 -> 569 | 0 -> 0 | 8.4 -> 1194.0 | 0.055 -> 92.596 |
| partial-index-mismatch | 21 -> 2512 | 0 -> 0 | 2.1 -> 5977.9 | 0.071 -> 87.896 |
| offset-pagination | 4 -> 1653 | 0 -> 0 | 2.3 -> 5139.6 | 0.105 -> 85.463 |
| work-mem-spill | 2472 -> 2508 | 31 -> 318 | 8152.6 -> 11003.3 | 172.263 -> 188.947 |
| join-without-index | 479 -> 7955 | 0 -> 0 | 468.4 -> 13389.7 | 0.687 -> 200.685 |
| equivalent-rewrite | 10 -> 10 | 0 -> 0 | 27.8 -> 27.8 | 0.102 -> 0.089 |
| covering-index | 2472 -> 157 | 31 -> 31 | 8152.6 -> 2439.9 | 172.263 -> 76.495 |

### False positives on unchanged code (A/A, 20 repetitions x 10 queries)

| Detector | Flagged query pairs | Builds that would fail | ... after a fresh ANALYZE: pairs | builds |
|---|---:|---:|---:|---:|
| time-1run-1.2x | 73/200 (36.5%) | 20/20 | 83/200 (41.5%) | 20/20 |
| time-median-1.2x | 57/200 (28.5%) | 19/20 | 44/200 (22.0%) | 18/20 |
| time-median-2x+5ms | 3/200 (1.5%) | 3/20 | 2/200 (1.0%) | 2/20 |
| cost-1.5x+50 | 0/200 (0.0%) | 0/20 | 0/200 (0.0%) | 0/20 |
| plan-shape | 0/200 (0.0%) | 0/20 | 0/200 (0.0%) | 0/20 |
| buffers-1.3x+64 | 0/200 (0.0%) | 0/20 | 0/200 (0.0%) | 0/20 |
| plan-guard-default | 0/200 (0.0%) | 0/20 | 0/200 (0.0%) | 0/20 |

### Noise profile per query (baseline + A/A captures)

| Query | Median time (ms) | CV single run | CV of 5-run median | max/min single run | Median shared blocks | CV shared blocks | Distinct plans |
|---|---:|---:|---:|---:|---:|---:|---:|
| correct_order_total | 0.140 | 71.1 % | 23.9 % | 14.91 | 21 | 0.00 % | 1 |
| customer_by_email | 0.055 | 56.1 % | 24.6 % | 9.50 | 4 | 0.00 % | 1 |
| customer_recent_orders | 0.126 | 83.9 % | 25.3 % | 21.57 | 10 | 0.00 % | 1 |
| customers_per_city | 13.650 | 91.7 % | 109.8 % | 14.70 | 569 | 0.00 % | 1 |
| open_orders_queue | 0.080 | 48.9 % | 42.3 % | 5.91 | 21 | 0.00 % | 1 |
| order_detail | 0.104 | 168.8 % | 24.2 % | 37.31 | 10 | 0.00 % | 1 |
| orders_page | 0.069 | 35.4 % | 21.7 % | 5.28 | 4 | 0.00 % | 1 |
| store_daily_revenue | 0.436 | 546.8 % | 19.7 % | 341.99 | 121 | 0.00 % | 1 |
| store_top_items | 0.551 | 496.9 % | 20.7 % | 185.12 | 479 | 0.00 % | 1 |
| top_customers | 116.054 | 32.6 % | 25.9 % | 8.94 | 2472 | 0.02 % | 1 |

Detectors:

- `time-1run-1.2x`: Execution time of a single EXPLAIN ANALYZE run grew > 1.2x (the naive check)
- `time-median-1.2x`: Median execution time of 5 runs grew > 1.2x
- `time-median-2x+5ms`: Median execution time grew > 2x and by >= 5 ms (plan-guard's time thresholds)
- `cost-1.5x+50`: Planner total cost grew > 1.5x and by >= 50
- `plan-shape`: Plan fingerprint changed (any operator, relation or index difference)
- `buffers-1.3x+64`: Median shared blocks (hit + read) grew > 1.3x and by >= 64 (plan-guard's primary signal)
- `plan-guard-default`: plan-guard's default policy returns FAIL (buffers or temp blocks; cost and time only warn)
