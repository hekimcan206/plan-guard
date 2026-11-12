-- A customer's 20 most recent orders (app: order history screen).
SELECT id, status, total, created_at
FROM orders
WHERE customer_id = 4242
ORDER BY created_at DESC
LIMIT 20
