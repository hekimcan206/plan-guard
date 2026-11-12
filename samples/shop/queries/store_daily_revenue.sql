-- Daily revenue of one store for one month (app: store dashboard).
SELECT date_trunc('day', created_at, 'UTC') AS day, count(*) AS orders, sum(total) AS revenue
FROM orders
WHERE store_id = 17
  AND created_at >= TIMESTAMPTZ '2026-03-01 00:00:00+00'
  AND created_at <  TIMESTAMPTZ '2026-04-01 00:00:00+00'
GROUP BY 1
ORDER BY 1
