-- Customers per city: a full scan by design (back-office report).
SELECT city, count(*) AS customers
FROM customers
GROUP BY city
ORDER BY customers DESC, city
