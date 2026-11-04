CREATE TABLE financials (
    Line TEXT PRIMARY KEY,
    Current TEXT NOT NULL,
    Prior TEXT NOT NULL,
    AsOf TEXT
);
INSERT INTO financials (Line, Current, Prior, AsOf) VALUES
    ('Revenue', '1250.0000', '1100.0000', NULL),
    ('Assets', '5000.00', '4700.00', '2025-12-31');
