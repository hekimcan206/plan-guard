-- Top spenders in one month (marketing export).
SELECT customer_id, sum(total) AS spent
FROM orders
WHERE created_at >= TIMESTAMPTZ '2026-05-01 00:00:00+00'
  AND created_at <  TIMESTAMPTZ '2026-06-01 00:00:00+00'
  AND customer_id IS NOT NULL
GROUP BY customer_id
ORDER BY spent DESC, customer_id
LIMIT 100
