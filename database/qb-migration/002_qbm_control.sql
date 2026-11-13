-- QuickBase -> PowerBase migration: operator controls and validation results (qbm schema only; see 001_qbm_state.sql).
-- Applied by the worker's "init" command to the tenant being migrated. Safe to run more than once.

-- Pause / stop requested by an operator ('pause' | 'stop' | NULL). Workers check it between chunks.
IF COL_LENGTH('qbm.Run', 'Control') IS NULL
    ALTER TABLE qbm.Run ADD Control NVARCHAR(16) NULL, ControlOn DATETIME2(3) NULL;
GO

-- The latest validation of each table (gate 3 and 7 counts), so a report can tell current results from older ones.
IF COL_LENGTH('qbm.TableMap', 'ValidatedOn') IS NULL
    ALTER TABLE qbm.TableMap ADD
        ValidatedOn         DATETIME2(3) NULL,   -- when the latest validation of this table started
        ComparedRecords     BIGINT       NULL,
        Gate3Differences    INT          NULL,   -- records that differ, plus missing and extra records
        Gate7NotEncrypted   INT          NULL;
GO
