-- Price correction on one order. Measured inside a rolled-back transaction, so the row never changes.
UPDATE orders
SET total = total + 1
WHERE id = 777
