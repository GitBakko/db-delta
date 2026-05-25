-- DbDelta ↔ Redgate parity fixture — TARGET schema
-- Apply on DbDeltaParity_Target. Pair with 01-source.sql.

USE [DbDeltaParity_Target];
GO

-- Scenario 01 — Table.AddedColumn (target lacks [Email])
CREATE TABLE dbo.Customer
(
    Id   int           IDENTITY(1, 1) NOT NULL,
    Name nvarchar(100) NOT NULL,
    CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED (Id)
);
GO

-- Scenario 02 — Table.DroppedColumn (target has extra [LegacyCode])
CREATE TABLE dbo.Product
(
    Id         int           IDENTITY(1, 1) NOT NULL,
    Name       nvarchar(100) NOT NULL,
    LegacyCode nvarchar(20)  NULL,
    CONSTRAINT PK_Product PRIMARY KEY CLUSTERED (Id)
);
GO

-- Scenario 03 — Table.IdentityFlip (target [Id] is plain int)
CREATE TABLE dbo.Invoice
(
    Id     int           NOT NULL,
    Amount decimal(18, 2) NOT NULL,
    CONSTRAINT PK_Invoice PRIMARY KEY CLUSTERED (Id)
);
GO

-- Scenario 04 — Table.ForeignKey.ActionChange (target FK ON DELETE NO ACTION)
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
        ON DELETE NO ACTION
);
GO

-- Scenario 05 — View.BodyChange (target returns 2)
EXEC ('CREATE VIEW dbo.vReport AS SELECT 2 AS Marker;');
GO

-- Scenario 06 — Procedure.BodyChange (target returns top 5)
EXEC ('CREATE PROCEDURE dbo.uspTopCustomers AS SELECT TOP (5) * FROM dbo.Customer;');
GO

-- Scenario 07 — Function.SourceOnly: target intentionally omits dbo.fnDouble.

-- Scenario 08 — Sequence.SeedChange (target seed=1)
CREATE SEQUENCE dbo.OrderNo AS bigint START WITH 1 INCREMENT BY 1 NO CYCLE CACHE 20;
GO

-- Scenario 09 — Synonym.BaseObjectChange (target alias points to Product)
CREATE SYNONYM dbo.CustomerAlias FOR dbo.Product;
GO

-- Scenario 10 — UserDefinedType (alias).SizeChange (target size=100)
CREATE TYPE dbo.ShortDescription FROM nvarchar(100) NOT NULL;
GO

-- Scenario 11 — TableType (UDTT).ColumnAdded (target lacks [Notes])
CREATE TYPE dbo.OrderItemTvp AS TABLE
(
    ProductId int NOT NULL,
    Quantity  int NOT NULL
);
GO

-- Scenario 12 — Table.IdentityFlip with inbound FK (M13-PARITY.6 #33).
-- Target [Order].Id is plain int (NO IDENTITY). OrderLine.FK points at
-- it — must be dropped before the rebuild and re-added after.
CREATE TABLE dbo.[Order]
(
    Id     int           NOT NULL,
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
