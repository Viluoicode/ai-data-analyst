/* ============================================================================
   01_schema.sql  —  AI Data Analyst (F&B milk-tea demo)
   Creates the "gold" analytics star schema (Medallion Gold-layer style).
   Idempotent: drops & recreates tables so the init container can re-run.
   All monetary values are in VND.
   ============================================================================ */

IF DB_ID('AnalystDB') IS NULL
    CREATE DATABASE AnalystDB;
GO

USE AnalystDB;
GO

IF SCHEMA_ID('gold') IS NULL
    EXEC('CREATE SCHEMA gold');
GO

/* Drop in FK-safe order ------------------------------------------------------ */
DROP TABLE IF EXISTS gold.FactOrderItem;
DROP TABLE IF EXISTS gold.DimDate;
DROP TABLE IF EXISTS gold.DimStore;
DROP TABLE IF EXISTS gold.DimProduct;
DROP TABLE IF EXISTS gold.DimCustomer;
GO

/* Dimensions ----------------------------------------------------------------- */
CREATE TABLE gold.DimDate (
    DateKey       INT          NOT NULL CONSTRAINT PK_DimDate PRIMARY KEY,  -- YYYYMMDD
    FullDate      DATE         NOT NULL,
    [Year]        SMALLINT     NOT NULL,
    [Quarter]     TINYINT      NOT NULL,
    MonthNum      TINYINT      NOT NULL,
    MonthName     VARCHAR(20)  NOT NULL,
    DayNum        TINYINT      NOT NULL,
    DayOfWeekNum  TINYINT      NOT NULL,   -- 1 = Monday .. 7 = Sunday
    DayName       VARCHAR(20)  NOT NULL,
    IsWeekend     BIT          NOT NULL
);

CREATE TABLE gold.DimStore (
    StoreKey    INT           NOT NULL CONSTRAINT PK_DimStore PRIMARY KEY,
    StoreName   NVARCHAR(100) NOT NULL,
    City        NVARCHAR(60)  NOT NULL,
    District    NVARCHAR(60)  NOT NULL,
    OpenedDate  DATE          NOT NULL
);

CREATE TABLE gold.DimProduct (
    ProductKey   INT           NOT NULL CONSTRAINT PK_DimProduct PRIMARY KEY,
    ProductName  NVARCHAR(100) NOT NULL,
    Category     NVARCHAR(40)  NOT NULL,   -- Milk Tea, Fruit Tea, Coffee, Smoothie, Topping
    Size         VARCHAR(10)   NOT NULL,   -- S, M, L, or NA (toppings)
    BasePrice    DECIMAL(10,2) NOT NULL
);

CREATE TABLE gold.DimCustomer (
    CustomerKey    INT           NOT NULL CONSTRAINT PK_DimCustomer PRIMARY KEY,
    CustomerName   NVARCHAR(100) NOT NULL,
    Gender         VARCHAR(10)   NOT NULL,
    MembershipTier VARCHAR(10)   NOT NULL,  -- Bronze, Silver, Gold
    City           NVARCHAR(60)  NOT NULL,
    JoinedDate     DATE          NOT NULL
);
GO

/* Fact (grain: one row per product line within an order) --------------------- */
CREATE TABLE gold.FactOrderItem (
    OrderItemKey    BIGINT        NOT NULL CONSTRAINT PK_FactOrderItem PRIMARY KEY,
    OrderId         INT           NOT NULL,
    DateKey         INT           NOT NULL,
    StoreKey        INT           NOT NULL,
    ProductKey      INT           NOT NULL,
    CustomerKey     INT           NOT NULL,
    Quantity        INT           NOT NULL,
    UnitPrice       DECIMAL(10,2) NOT NULL,
    DiscountAmount  DECIMAL(12,2) NOT NULL,
    LineTotal       DECIMAL(12,2) NOT NULL,
    PaymentMethod   VARCHAR(20)   NOT NULL,
    CONSTRAINT FK_Fact_Date     FOREIGN KEY (DateKey)     REFERENCES gold.DimDate(DateKey),
    CONSTRAINT FK_Fact_Store    FOREIGN KEY (StoreKey)    REFERENCES gold.DimStore(StoreKey),
    CONSTRAINT FK_Fact_Product  FOREIGN KEY (ProductKey)  REFERENCES gold.DimProduct(ProductKey),
    CONSTRAINT FK_Fact_Customer FOREIGN KEY (CustomerKey) REFERENCES gold.DimCustomer(CustomerKey)
);
GO

CREATE INDEX IX_Fact_DateKey    ON gold.FactOrderItem(DateKey);
CREATE INDEX IX_Fact_StoreKey   ON gold.FactOrderItem(StoreKey);
CREATE INDEX IX_Fact_ProductKey ON gold.FactOrderItem(ProductKey);
GO

PRINT 'Schema created.';
GO
