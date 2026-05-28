-- DbDelta ↔ Redgate parity fixture — SOURCE schema
-- Apply on DbDeltaParity_Source. Pair with 02-target.sql to produce
-- 17 deliberate, well-named divergences for line-by-line parity diff.

USE [DbDeltaParity_Source];
GO

-- =========================================================================
-- Scenario 01 — Table.AddedColumn
-- Source has [Email]; Target lacks it. Expected diff: ALTER TABLE … ADD.
-- =========================================================================
CREATE TABLE dbo.Customer
(
    Id    int           IDENTITY(1, 1) NOT NULL,
    Name  nvarchar(100) NOT NULL,
    Email nvarchar(200) NULL,
    CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED (Id)
);
GO

-- =========================================================================
-- Scenario 02 — Table.DroppedColumn
-- Source omits [LegacyCode]; Target has it. Expected diff: ALTER TABLE … DROP.
-- =========================================================================
CREATE TABLE dbo.Product
(
    Id   int           IDENTITY(1, 1) NOT NULL,
    Name nvarchar(100) NOT NULL,
    CONSTRAINT PK_Product PRIMARY KEY CLUSTERED (Id)
);
GO

-- =========================================================================
-- Scenario 03 — Table.IdentityFlip
-- Source [Id] is IDENTITY; Target [Id] is plain int.
-- Expected DbDelta: temp-table rebuild (M13-FIX.3).
-- Expected Redgate: similar rebuild dance.
-- =========================================================================
CREATE TABLE dbo.Invoice
(
    Id     int           IDENTITY(1, 1) NOT NULL,
    Amount decimal(18, 2) NOT NULL,
    CONSTRAINT PK_Invoice PRIMARY KEY CLUSTERED (Id)
);
GO

-- =========================================================================
-- Scenario 04 — Table.ForeignKey.ActionChange
-- Source FK ON DELETE CASCADE; Target ON DELETE NO ACTION.
-- Expected diff: DROP + ADD CONSTRAINT … FOREIGN KEY … ON DELETE CASCADE.
-- =========================================================================
CREATE TABLE dbo.Category
(
    Id   int           IDENTITY(1, 1) NOT NULL,
    Name nvarchar(50)  NOT NULL,
    CONSTRAINT PK_Category PRIMARY KEY CLUSTERED (Id)
);
GO
CREATE TABLE dbo.Article
(
    Id         int           IDENTITY(1, 1) NOT NULL,
    Title      nvarchar(200) NOT NULL,
    CategoryId int           NOT NULL,
    CONSTRAINT PK_Article PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Article_Category
        FOREIGN KEY (CategoryId) REFERENCES dbo.Category (Id)
        ON DELETE CASCADE
);
GO

-- =========================================================================
-- Scenario 05 — View.BodyChange
-- Source view body returns 1; Target returns 2. Expected: CREATE OR ALTER.
-- =========================================================================
EXEC ('CREATE VIEW dbo.vReport AS SELECT 1 AS Marker;');
GO

-- =========================================================================
-- Scenario 06 — Procedure.BodyChange
-- Source returns top 10 customers; Target returns top 5. Expected: CREATE OR ALTER.
-- =========================================================================
EXEC ('CREATE PROCEDURE dbo.uspTopCustomers AS SELECT TOP (10) * FROM dbo.Customer;');
GO

-- =========================================================================
-- Scenario 07 — Function.SourceOnly
-- Source defines fnDouble; Target does not. Expected: CREATE OR ALTER FUNCTION.
-- =========================================================================
EXEC ('CREATE FUNCTION dbo.fnDouble (@x int) RETURNS int AS BEGIN RETURN @x * 2; END');
GO

-- =========================================================================
-- Scenario 08 — Sequence.SeedChange
-- Source START WITH 100; Target START WITH 1. Expected: DROP + CREATE SEQUENCE.
-- =========================================================================
CREATE SEQUENCE dbo.OrderNo AS bigint START WITH 100 INCREMENT BY 1 NO CYCLE CACHE 20;
GO

-- =========================================================================
-- Scenario 09 — Synonym.BaseObjectChange
-- Source points to dbo.Customer; Target points to dbo.Product.
-- Expected: DROP + CREATE SYNONYM.
-- =========================================================================
CREATE SYNONYM dbo.CustomerAlias FOR dbo.Customer;
GO

-- =========================================================================
-- Scenario 10 — UserDefinedType (alias).SizeChange
-- Source ShortDescription = nvarchar(200); Target = nvarchar(100).
-- Expected: DROP + CREATE TYPE.
-- Note: live SQL Server rejects dropping a UDT bound to a column;
-- the fixture keeps it standalone so the parity tools can diff freely.
-- =========================================================================
CREATE TYPE dbo.ShortDescription FROM nvarchar(200) NOT NULL;
GO

-- =========================================================================
-- Scenario 11 — TableType (UDTT).ColumnAdded
-- Source TVP has 3 columns; Target has 2. Expected: DROP + CREATE TYPE … AS TABLE.
-- =========================================================================
CREATE TYPE dbo.OrderItemTvp AS TABLE
(
    ProductId int           NOT NULL,
    Quantity  int           NOT NULL,
    Notes     nvarchar(100) NULL
);
GO

-- =========================================================================
-- Scenario 12 — Table.IdentityFlip with inbound FK (M13-PARITY.6 #33)
-- Source [Order].Id is IDENTITY(1,1); Target is plain int. Source has
-- [OrderLine] with FK → [Order].Id (matching FK on target, table is
-- Identical so it never appears in the diff pairs by itself).
-- Expected DbDelta: DROP inbound FK → DROP PK → temp-table rebuild
-- without inline PK → DROP old → sp_rename → ADD PK → ADD inbound FK.
-- Expected Redgate: same dance with [RG_Recovery_N_Order] naming.
-- =========================================================================
CREATE TABLE dbo.[Order]
(
    Id     int           IDENTITY(1, 1) NOT NULL,
    Total  decimal(18, 2) NOT NULL,
    CONSTRAINT PK_Order PRIMARY KEY CLUSTERED (Id)
);
GO
CREATE TABLE dbo.OrderLine
(
    Id        int           IDENTITY(1, 1) NOT NULL,
    OrderId   int           NOT NULL,
    SkuCode   nvarchar(40)  NOT NULL,
    CONSTRAINT PK_OrderLine PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_OrderLine_Order
        FOREIGN KEY (OrderId) REFERENCES dbo.[Order] (Id)
);
GO

-- =========================================================================
-- Scenario 13 — Cross-kind: computed column → scalar function (#24)
-- Both [fnLineTotal] and [PriceList] are source-only. The computed column
-- references the function, so the deploy script MUST CREATE the function
-- before the table. Exercises EdgeKind.ComputedColumn topological ordering.
-- =========================================================================
CREATE FUNCTION dbo.fnLineTotal (@qty int, @price decimal(18, 2))
    RETURNS decimal(18, 2)
AS
BEGIN
    RETURN @qty * @price;
END
GO
CREATE TABLE dbo.PriceList
(
    Id        int            IDENTITY(1, 1) NOT NULL,
    Qty       int            NOT NULL,
    Price     decimal(18, 2) NOT NULL,
    LineTotal AS (dbo.fnLineTotal(Qty, Price)),
    CONSTRAINT PK_PriceList PRIMARY KEY CLUSTERED (Id)
);
GO

-- =========================================================================
-- Scenario 14 — Cross-kind: view → view (#24)
-- Both views source-only. [vSalesDerived] selects from [vSalesBase], so the
-- base view MUST be created first (views have no deferred name resolution).
-- Exercises EdgeKind.ModuleReference between two views.
-- =========================================================================
EXEC ('CREATE VIEW dbo.vSalesBase AS SELECT 1 AS Id, CAST(N''x'' AS nvarchar(10)) AS Label;');
GO
EXEC ('CREATE VIEW dbo.vSalesDerived AS SELECT Id, Label FROM dbo.vSalesBase;');
GO

-- =========================================================================
-- Scenario 15 — Cross-kind: view → scalar function (#24)
-- Both source-only. [vTaxedItems] invokes [fnTaxRate]; function must be
-- created first. Exercises EdgeKind.ModuleReference (view → function).
-- =========================================================================
EXEC ('CREATE FUNCTION dbo.fnTaxRate (@amount decimal(18, 2)) RETURNS decimal(18, 2) AS BEGIN RETURN @amount * 0.22; END');
GO
EXEC ('CREATE VIEW dbo.vTaxedItems AS SELECT CAST(100 AS decimal(18, 2)) AS Amount, dbo.fnTaxRate(100) AS Tax;');
GO

-- =========================================================================
-- Scenario 16 — Cross-kind: schemabound inline TVF → table (#24)
-- Both source-only. [tvfRegionLookup] is WITH SCHEMABINDING over [Region],
-- a hard dependency: the table MUST be created first and cannot be dropped
-- while the TVF exists. Exercises EdgeKind.ModuleReference / FunctionOnTable.
-- =========================================================================
CREATE TABLE dbo.Region
(
    Id   int          IDENTITY(1, 1) NOT NULL,
    Name nvarchar(60) NOT NULL,
    CONSTRAINT PK_Region PRIMARY KEY CLUSTERED (Id)
);
GO
EXEC ('CREATE FUNCTION dbo.tvfRegionLookup () RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT Id, Name FROM dbo.Region;');
GO

-- =========================================================================
-- Scenario 17 — Cross-kind multi-hop: table → schemabound fn → view (#24)
-- All three source-only. [fnStockValue] is schemabound over [Warehouse];
-- [vStockReport] invokes [fnStockValue]. Correct topo order is
-- Warehouse → fnStockValue → vStockReport. Stresses transitive ordering.
-- =========================================================================
CREATE TABLE dbo.Warehouse
(
    Id       int            IDENTITY(1, 1) NOT NULL,
    Capacity decimal(18, 2) NOT NULL,
    CONSTRAINT PK_Warehouse PRIMARY KEY CLUSTERED (Id)
);
GO
EXEC ('CREATE FUNCTION dbo.fnStockValue (@id int) RETURNS decimal(18, 2) WITH SCHEMABINDING AS BEGIN RETURN (SELECT TOP (1) Capacity FROM dbo.Warehouse WHERE Id = @id); END');
GO
EXEC ('CREATE VIEW dbo.vStockReport AS SELECT 1 AS WarehouseId, dbo.fnStockValue(1) AS Value;');
GO
