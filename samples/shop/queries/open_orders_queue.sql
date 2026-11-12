-- Oldest open orders first (kitchen queue).
SELECT id, store_id, created_at
FROM orders
WHERE status = 'open'
ORDER BY created_at
LIMIT 50
