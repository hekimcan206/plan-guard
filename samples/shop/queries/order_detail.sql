-- Lines of one order with item names (app: receipt).
SELECT oi.line_no, m.name, oi.qty, oi.unit_price
FROM order_items AS oi
JOIN menu_items AS m ON m.id = oi.menu_item_id
WHERE oi.order_id = 123456
ORDER BY oi.line_no
