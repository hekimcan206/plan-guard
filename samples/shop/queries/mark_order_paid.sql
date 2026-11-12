-- Payment confirmation. Measured inside a rolled-back transaction, so the row never changes.
UPDATE orders
SET status = 'paid'
WHERE id = 777
