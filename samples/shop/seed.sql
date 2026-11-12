-- Deterministic synthetic data: setseed() fixes random(), so every run on the same PostgreSQL major version
-- produces the same rows. 200 stores, 50,000 customers, 5,000 menu items, 300,000 orders, 900,000 order lines.

SELECT setseed(0.2026);

INSERT INTO stores (id, city, opened_on)
SELECT g,
       (ARRAY['Istanbul','Ankara','Izmir','Bursa','Antalya','Konya','Adana','Eskisehir','Trabzon','Kayseri'])[1 + (g % 10)],
       DATE '2015-01-01' + (g * 13 % 3000)
FROM generate_series(1, 200) AS g;

INSERT INTO customers (id, email, phone, city, created_at)
SELECT g,
       'customer' || g || '@example.test',
       '+90 5' || lpad(((g * 7919) % 1000000000)::text, 9, '0'),
       (ARRAY['Istanbul','Ankara','Izmir','Bursa','Antalya','Konya','Adana','Eskisehir','Trabzon','Kayseri'])[1 + floor(random() * 10)::int],
       TIMESTAMPTZ '2024-01-01 00:00:00+00' + random() * INTERVAL '700 days'
FROM generate_series(1, 50000) AS g;

INSERT INTO menu_items (id, store_id, name, category, price)
SELECT g,
       1 + ((g - 1) / 25),
       (ARRAY['Lahmacun','Iskender','Adana kebab','Pide','Manti','Doner','Kofte','Baklava','Ayran','Mercimek corbasi',
              'Kunefe','Menemen','Simit','Sutlac','Gozleme','Cay','Turk kahvesi','Salata','Pilav','Borek',
              'Kumpir','Midye','Hamsi','Lokum','Salgam'])[1 + ((g - 1) % 25)],
       (ARRAY['main','main','main','main','main','main','main','dessert','drink','soup',
              'dessert','breakfast','breakfast','dessert','breakfast','drink','drink','side','side','breakfast',
              'main','main','main','dessert','drink'])[1 + ((g - 1) % 25)],
       round((20 + random() * 380)::numeric, 2)
FROM generate_series(1, 5000) AS g;

-- Orders and lines are generated into temporary tables first, so each order's total is known before it is
-- inserted and no row is ever updated (no dead tuples, no VACUUM FULL needed).
CREATE TEMP TABLE seed_orders AS
SELECT g AS id,
       1 + floor(random() * 200)::int AS store_id,
       CASE WHEN random() < 0.85 THEN 1 + floor(random() * 50000)::int END AS customer_id,
       CASE WHEN r < 0.02 THEN 'open' WHEN r < 0.05 THEN 'cancelled' ELSE 'paid' END AS status,
       TIMESTAMPTZ '2026-01-01 00:00:00+00' + random() * INTERVAL '300 days' AS created_at
FROM (SELECT g, random() AS r FROM generate_series(1, 300000) AS g) AS s;

-- Three lines per order on average (1 to 5), each an item of the order's own store.
CREATE TEMP TABLE seed_lines AS
SELECT o.id AS order_id,
       l.line_no,
       m.id AS menu_item_id,
       1 + ((o.id + l.line_no) % 3)::int AS qty,
       m.price AS unit_price
FROM seed_orders AS o
CROSS JOIN LATERAL generate_series(1, 1 + (o.id % 5)::int) AS l(line_no)
JOIN menu_items AS m ON m.id = (o.store_id - 1) * 25 + 1 + ((o.id * 7 + l.line_no * 11) % 25)::int;

INSERT INTO orders (id, store_id, customer_id, status, total, created_at)
SELECT o.id, o.store_id, o.customer_id, o.status, t.total, o.created_at
FROM seed_orders AS o
JOIN (SELECT order_id, sum(qty * unit_price) AS total FROM seed_lines GROUP BY order_id) AS t ON t.order_id = o.id
ORDER BY o.id;

INSERT INTO order_items (order_id, line_no, menu_item_id, qty, unit_price)
SELECT order_id, line_no, menu_item_id, qty, unit_price
FROM seed_lines
ORDER BY order_id, line_no;

DROP TABLE seed_orders, seed_lines;

-- The seeder runs VACUUM (ANALYZE) afterwards: VACUUM cannot run inside the batch transaction.
