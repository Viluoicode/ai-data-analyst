/* ============================================================================
   Azure SQL Database — deterministic seed (identical data to the local seed).
   Run while CONNECTED to AnalystDB. Requires GENERATE_SERIES (Azure SQL supports it).
   ============================================================================ */

DELETE FROM gold.FactOrderItem;
DELETE FROM gold.DimDate;
DELETE FROM gold.DimStore;
DELETE FROM gold.DimProduct;
DELETE FROM gold.DimCustomer;
GO

INSERT gold.DimStore (StoreKey, StoreName, City, District, OpenedDate) VALUES
 (1, N'Bubble House Hoan Kiem',  N'Hà Nội',        N'Hoàn Kiếm', '2022-03-01'),
 (2, N'Bubble House Cau Giay',   N'Hà Nội',        N'Cầu Giấy',  '2022-09-15'),
 (3, N'Bubble House District 1', N'Hồ Chí Minh',   N'Quận 1',    '2021-11-20'),
 (4, N'Bubble House Thu Duc',    N'Hồ Chí Minh',   N'Thủ Đức',   '2023-01-10'),
 (5, N'Bubble House Hai Chau',   N'Đà Nẵng',       N'Hải Châu',  '2023-05-05'),
 (6, N'Bubble House Le Chan',    N'Hải Phòng',     N'Lê Chân',   '2023-08-12');
GO

INSERT gold.DimProduct (ProductKey, ProductName, Category, Size, BasePrice) VALUES
 (1,  N'Classic Milk Tea',     N'Milk Tea',  'M', 45000),
 (2,  N'Classic Milk Tea',     N'Milk Tea',  'L', 55000),
 (3,  N'Brown Sugar Boba',     N'Milk Tea',  'M', 55000),
 (4,  N'Brown Sugar Boba',     N'Milk Tea',  'L', 65000),
 (5,  N'Taro Milk Tea',        N'Milk Tea',  'M', 50000),
 (6,  N'Matcha Milk Tea',      N'Milk Tea',  'M', 52000),
 (7,  N'Peach Tea',            N'Fruit Tea', 'M', 48000),
 (8,  N'Peach Tea',            N'Fruit Tea', 'L', 58000),
 (9,  N'Lychee Tea',           N'Fruit Tea', 'M', 48000),
 (10, N'Passion Fruit Tea',    N'Fruit Tea', 'M', 50000),
 (11, N'Milk Coffee',          N'Coffee',    'M', 40000),
 (12, N'Americano',            N'Coffee',    'M', 42000),
 (13, N'Latte',                N'Coffee',    'M', 50000),
 (14, N'Mango Smoothie',       N'Smoothie',  'M', 55000),
 (15, N'Strawberry Smoothie',  N'Smoothie',  'M', 55000),
 (16, N'Pearl (Boba) Topping', N'Topping',   'NA', 8000),
 (17, N'Cheese Foam Topping',  N'Topping',   'NA', 12000),
 (18, N'Pudding Topping',      N'Topping',   'NA', 10000);
GO

;WITH c AS (SELECT value AS k FROM GENERATE_SERIES(1, 300))
INSERT gold.DimCustomer (CustomerKey, CustomerName, Gender, MembershipTier, City, JoinedDate)
SELECT k, CONCAT(N'Customer ', k),
    CASE WHEN k % 2 = 0 THEN 'Female' ELSE 'Male' END,
    CASE WHEN k % 10 = 0 THEN 'Gold' WHEN k % 10 IN (1,2,3) THEN 'Silver' ELSE 'Bronze' END,
    CASE k % 4 WHEN 0 THEN N'Hà Nội' WHEN 1 THEN N'Hồ Chí Minh' WHEN 2 THEN N'Đà Nẵng' ELSE N'Hải Phòng' END,
    DATEADD(day, (k * 7) % 900, CAST('2023-01-01' AS date))
FROM c;
GO

;WITH d AS (
    SELECT DATEADD(day, value, CAST('2024-01-01' AS date)) AS FullDate
    FROM GENERATE_SERIES(0, DATEDIFF(day, '2024-01-01', '2025-12-31'))
)
INSERT gold.DimDate (DateKey, FullDate, [Year], [Quarter], MonthNum, MonthName, DayNum, DayOfWeekNum, DayName, IsWeekend)
SELECT YEAR(FullDate)*10000 + MONTH(FullDate)*100 + DAY(FullDate), FullDate,
    YEAR(FullDate), DATEPART(quarter, FullDate), MONTH(FullDate), DATENAME(month, FullDate),
    DAY(FullDate), ((DATEPART(weekday, FullDate) + @@DATEFIRST - 2) % 7) + 1, DATENAME(weekday, FullDate),
    CASE WHEN ((DATEPART(weekday, FullDate) + @@DATEFIRST - 2) % 7) + 1 IN (6,7) THEN 1 ELSE 0 END
FROM d;
GO

DECLARE @numProducts  INT = (SELECT COUNT(*) FROM gold.DimProduct);
DECLARE @numStores    INT = (SELECT COUNT(*) FROM gold.DimStore);
DECLARE @numCustomers INT = (SELECT COUNT(*) FROM gold.DimCustomer);
DECLARE @numDays      INT = (SELECT DATEDIFF(day, '2024-01-01', '2025-12-31') + 1);
DECLARE @N            INT = 12000;

;WITH items AS (
    SELECT value AS n, ((value - 1) / 3) + 1 AS OrderId FROM GENERATE_SERIES(1, @N)
),
ord AS (
    SELECT i.n, i.OrderId,
        1 + (i.OrderId * 5)  % @numStores    AS StoreKey,
        1 + (i.OrderId * 13) % @numCustomers AS CustomerKey,
        1 + (i.n * 7)        % @numProducts  AS ProductKey,
        1 + (i.n % 3)                        AS Quantity,
        (i.OrderId * 11) % @numDays          AS DayOffset,
        i.OrderId % 3                        AS PayIdx
    FROM items i
)
INSERT gold.FactOrderItem
    (OrderItemKey, OrderId, DateKey, StoreKey, ProductKey, CustomerKey, Quantity, UnitPrice, DiscountAmount, LineTotal, PaymentMethod)
SELECT o.n, o.OrderId,
    YEAR(d.FullDate) * 10000 + MONTH(d.FullDate) * 100 + DAY(d.FullDate),
    o.StoreKey, o.ProductKey, o.CustomerKey, o.Quantity, p.BasePrice, disc.DiscountAmount,
    p.BasePrice * o.Quantity - disc.DiscountAmount,
    CASE o.PayIdx WHEN 0 THEN 'Cash' WHEN 1 THEN 'Card' ELSE 'EWallet' END
FROM ord o
JOIN gold.DimProduct p ON p.ProductKey = o.ProductKey
CROSS APPLY (VALUES (DATEADD(day, o.DayOffset, CAST('2024-01-01' AS date)))) d(FullDate)
CROSS APPLY (VALUES (CASE WHEN o.OrderId % 7 = 0
                          THEN CAST(ROUND(p.BasePrice * o.Quantity * 0.10, 0) AS DECIMAL(12,2))
                          ELSE 0 END)) disc(DiscountAmount);
GO

DECLARE @rows INT = (SELECT COUNT(*) FROM gold.FactOrderItem);
PRINT CONCAT('Azure seed complete. FactOrderItem rows = ', @rows);
GO
