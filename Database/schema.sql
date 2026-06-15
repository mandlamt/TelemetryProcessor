/* ============================================================================
   Distributed Telemetry Processor - Database Schema & Stored Procedures
   Target: SQL Server (tested against mcr.microsoft.com/mssql/server:2022-latest)

   Run this script after the TelemetryDb database has been created, e.g.:
       sqlcmd -S localhost,1433 -U sa -P "YourStrong!Passw0rd" -Q "CREATE DATABASE TelemetryDb"
       sqlcmd -S localhost,1433 -U sa -P "YourStrong!Passw0rd" -d TelemetryDb -i Database/schema.sql
   ============================================================================ */

IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'TelemetryDb')
BEGIN
    PRINT 'Run this script against the TelemetryDb database. Creating it now.';
    -- Note: CREATE DATABASE cannot run inside the same batch as USE on some
    -- clients; if this fails, create the DB manually first and re-run.
END
GO

IF DB_ID('TelemetryDb') IS NULL
BEGIN
    CREATE DATABASE TelemetryDb;
END
GO

USE TelemetryDb;
GO

/* ----------------------------------------------------------------------
   Table: PendingReadings
   Holds SensorReadings that were spilled from the Publisher's in-memory
   queue (either due to capacity or age). SequenceNumber preserves the
   original generation order for FIFO retrieval.
---------------------------------------------------------------------- */
IF OBJECT_ID('dbo.PendingReadings', 'U') IS NOT NULL
    DROP TABLE dbo.PendingReadings;
GO

CREATE TABLE dbo.PendingReadings
(
    Id              VARCHAR(50)      NOT NULL PRIMARY KEY,
    [Timestamp]     DATETIME2(3)     NOT NULL,
    Value           FLOAT            NOT NULL,
    SensorType      VARCHAR(50)      NOT NULL,
    SequenceNumber  BIGINT           NOT NULL,
    SpilledAt       DATETIME2(3)     NOT NULL CONSTRAINT DF_PendingReadings_SpilledAt DEFAULT SYSUTCDATETIME()
);
GO

-- Supports efficient "oldest first" retrieval (FIFO via SequenceNumber).
CREATE INDEX IX_PendingReadings_SequenceNumber ON dbo.PendingReadings (SequenceNumber);
GO

/* ----------------------------------------------------------------------
   Table: AnalysisResults
   Holds the Consumer's "Complex Analysis" (moving average) output for
   each processed reading.
---------------------------------------------------------------------- */
IF OBJECT_ID('dbo.AnalysisResults', 'U') IS NOT NULL
    DROP TABLE dbo.AnalysisResults;
GO

CREATE TABLE dbo.AnalysisResults
(
    Id              VARCHAR(50)      NOT NULL PRIMARY KEY,
    SensorReadingId VARCHAR(50)      NOT NULL,
    AnalysisType    VARCHAR(50)      NOT NULL,
    Result          FLOAT            NOT NULL,
    ProcessedAt     DATETIME2(3)     NOT NULL,
    CorrelationId   VARCHAR(50)      NULL,
    CreatedAt       DATETIME2(3)     NOT NULL CONSTRAINT DF_AnalysisResults_CreatedAt DEFAULT SYSUTCDATETIME()
);
GO

CREATE INDEX IX_AnalysisResults_SensorReadingId ON dbo.AnalysisResults (SensorReadingId);
GO

/* ----------------------------------------------------------------------
   sp_SavePendingReading
   Used by the Publisher to spill an in-memory reading to the database.
   Parameterized + wrapped in a transaction to satisfy injection-prevention
   and atomicity requirements.
---------------------------------------------------------------------- */
IF OBJECT_ID('dbo.sp_SavePendingReading', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_SavePendingReading;
GO

CREATE PROCEDURE dbo.sp_SavePendingReading
    @Id             VARCHAR(50),
    @Timestamp      DATETIME2(3),
    @Value          FLOAT,
    @SensorType     VARCHAR(50),
    @SequenceNumber BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    BEGIN TRY
        -- Idempotency guard: if this reading was already spilled (e.g. a
        -- retried call), do not insert a duplicate.
        IF NOT EXISTS (SELECT 1 FROM dbo.PendingReadings WHERE Id = @Id)
        BEGIN
            INSERT INTO dbo.PendingReadings (Id, [Timestamp], Value, SensorType, SequenceNumber)
            VALUES (@Id, @Timestamp, @Value, @SensorType, @SequenceNumber);
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        THROW;
    END CATCH
END
GO

/* ----------------------------------------------------------------------
   sp_GetOldestPending
   Used by the Publisher to retrieve and remove the oldest spilled reading
   (FIFO by SequenceNumber). SELECT + DELETE happen atomically inside a
   single transaction with an UPDLOCK/READPAST-free serializable read on
   just the one row, preventing two concurrent callers from returning the
   same row.
---------------------------------------------------------------------- */
IF OBJECT_ID('dbo.sp_GetOldestPending', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_GetOldestPending;
GO

CREATE PROCEDURE dbo.sp_GetOldestPending
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    BEGIN TRY
        DECLARE @Id VARCHAR(50);

        -- Lock and identify the oldest row, skipping rows already locked by
        -- another concurrent caller.
        SELECT TOP (1) @Id = Id
        FROM dbo.PendingReadings WITH (UPDLOCK, READPAST, ROWLOCK)
        ORDER BY SequenceNumber ASC;

        IF @Id IS NOT NULL
        BEGIN
            -- Return the row to the caller before deleting it.
            SELECT Id, [Timestamp], Value, SensorType, SequenceNumber
            FROM dbo.PendingReadings
            WHERE Id = @Id;

            DELETE FROM dbo.PendingReadings WHERE Id = @Id;
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        THROW;
    END CATCH
END
GO

/* ----------------------------------------------------------------------
   sp_InsertAnalysisResults
   Used by the Consumer to persist a processed AnalysisResult.
---------------------------------------------------------------------- */
IF OBJECT_ID('dbo.sp_InsertAnalysisResults', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_InsertAnalysisResults;
GO

CREATE PROCEDURE dbo.sp_InsertAnalysisResults
    @Id              VARCHAR(50),
    @SensorReadingId VARCHAR(50),
    @AnalysisType    VARCHAR(50),
    @Result          FLOAT,
    @ProcessedAt     DATETIME2(3),
    @CorrelationId   VARCHAR(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    BEGIN TRY
        IF NOT EXISTS (SELECT 1 FROM dbo.AnalysisResults WHERE Id = @Id)
        BEGIN
            INSERT INTO dbo.AnalysisResults
                (Id, SensorReadingId, AnalysisType, Result, ProcessedAt, CorrelationId)
            VALUES
                (@Id, @SensorReadingId, @AnalysisType, @Result, @ProcessedAt, @CorrelationId);
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        THROW;
    END CATCH
END
GO
