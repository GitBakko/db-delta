-- DbDelta ↔ Redgate parity fixture — SOURCE schema
-- Apply on DbDeltaParity_Source. Pair with 02-target.sql to produce
-- 11 deliberate, well-named divergences for line-by-line parity diff.

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
