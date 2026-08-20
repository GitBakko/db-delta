-- DbDelta ↔ Redgate parity fixture — REFUSALS, kept apart on purpose
--
-- Scenario 22 lives in its own pair of databases because its expected
-- DbDelta outcome is a REFUSAL: script generation stops, the CLI exits 30
-- and the app shows a banner. A refusal aborts the whole run, so putting a
-- columnstore in the 21-scenario fixture would make the other twenty
-- unmeasurable.
--
-- Run 00-bootstrap.sql first for the main pair, then this file, which
-- creates and fills its own two databases.

USE [master];
GO

IF DB_ID(N'DbDeltaParity_RefusalSource') IS NOT NULL
BEGIN
    ALTER DATABASE [DbDeltaParity_RefusalSource] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [DbDeltaParity_RefusalSource];
END
GO
IF DB_ID(N'DbDeltaParity_RefusalTarget') IS NOT NULL
BEGIN
    ALTER DATABASE [DbDeltaParity_RefusalTarget] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [DbDeltaParity_RefusalTarget];
END
GO
CREATE DATABASE [DbDeltaParity_RefusalSource];
GO
CREATE DATABASE [DbDeltaParity_RefusalTarget];
GO

-- =========================================================================
-- Scenario 22 — Columnstore index
-- DbDelta READS every index type — a columnstore difference is reported,
-- not hidden — but it can only WRITE the two rowstore shapes. Asked to
-- script one it refuses (UnscriptableIndexException, CLI exit 30) rather
-- than emit a CREATE that would produce a DIFFERENT index, or skip it and
-- let a table rebuild destroy it under a green banner.
--
-- The parity question this measures: what does Redgate emit here, and is
-- what it emits correct? The answer decides whether the refusal is a gap
-- to close or a limit to keep declaring.
-- =========================================================================
USE [DbDeltaParity_RefusalSource];
GO
CREATE TABLE dbo.Metric
(
    Id      int            IDENTITY(1, 1) NOT NULL,
    TakenAt datetime2(0)   NOT NULL,
    Value   decimal(18, 4) NOT NULL,
    CONSTRAINT PK_Metric PRIMARY KEY CLUSTERED (Id)
);
GO
CREATE NONCLUSTERED COLUMNSTORE INDEX IX_Metric_Columnstore
    ON dbo.Metric (TakenAt, Value);
GO

USE [DbDeltaParity_RefusalTarget];
GO
-- Same table, no columnstore: the index is the whole difference.
CREATE TABLE dbo.Metric
(
    Id      int            IDENTITY(1, 1) NOT NULL,
    TakenAt datetime2(0)   NOT NULL,
    Value   decimal(18, 4) NOT NULL,
    CONSTRAINT PK_Metric PRIMARY KEY CLUSTERED (Id)
);
GO
