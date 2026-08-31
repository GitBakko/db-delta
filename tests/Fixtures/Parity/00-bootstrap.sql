-- DbDelta ↔ Redgate SQL Compare parity fixture — bootstrap
-- Run once against a SQL Server instance (LocalDB / 2022 / Azure SQL).
-- Creates two empty databases, then 01-source.sql and 02-target.sql
-- populate them with 21 deliberate divergences (one per scenario).
-- Scenario 22 is a refusal and lives in 03-refusals.sql, its own DB pair.

USE [master];
GO

IF DB_ID(N'DbDeltaParity_Source') IS NOT NULL
BEGIN
    ALTER DATABASE [DbDeltaParity_Source] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [DbDeltaParity_Source];
END
GO

IF DB_ID(N'DbDeltaParity_Target') IS NOT NULL
BEGIN
    ALTER DATABASE [DbDeltaParity_Target] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [DbDeltaParity_Target];
END
GO

CREATE DATABASE [DbDeltaParity_Source];
GO
CREATE DATABASE [DbDeltaParity_Target];
GO
