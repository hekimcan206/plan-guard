-- Best-selling items of one store in one week (app: menu analytics).
SELECT m.name, sum(oi.qty) AS qty
FROM orders AS o
JOIN order_items AS oi ON oi.order_id = o.id
JOIN menu_items AS m ON m.id = oi.menu_item_id
WHERE o.store_id = 42
  AND o.created_at >= TIMESTAMPTZ '2026-06-01 00:00:00+00'
  AND o.created_at <  TIMESTAMPTZ '2026-06-08 00:00:00+00'
GROUP BY m.name
ORDER BY qty DESC, m.name
LIMIT 10
