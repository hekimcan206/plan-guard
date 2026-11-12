-- Log-in lookup by e-mail, case-insensitive.
SELECT id, city
FROM customers
WHERE lower(email) = lower('Customer4242@example.test')
