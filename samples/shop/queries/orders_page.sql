-- Keyset pagination over orders (app: admin order list).
SELECT id, total
FROM orders
WHERE id > 150000
ORDER BY id
LIMIT 50
