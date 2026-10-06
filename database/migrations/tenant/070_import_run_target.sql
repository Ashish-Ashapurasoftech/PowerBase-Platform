-- An import may fill several destination tables in one run. Each target's own counts are kept here; the run's own counters stay the
-- totals. A run that fills one table has no rows in this table.
IF OBJECT_ID('meta.ImportRunTarget') IS NULL
BEGIN
    CREATE TABLE meta.ImportRunTarget (
        ImportRunId        BIGINT NOT NULL CONSTRAINT FK_ImportRunTarget_Run REFERENCES meta.ImportRun(Id),
        TargetIndex        TINYINT NOT NULL,                 -- 0 = the import's own table, 1.. = the others, in the order saved
        DestinationTableId BIGINT NOT NULL,
        Inserted           BIGINT NOT NULL CONSTRAINT DF_ImportRunTarget_Inserted DEFAULT 0,
        Updated            BIGINT NOT NULL CONSTRAINT DF_ImportRunTarget_Updated DEFAULT 0,
        Skipped            BIGINT NOT NULL CONSTRAINT DF_ImportRunTarget_Skipped DEFAULT 0,
        Errored            BIGINT NOT NULL CONSTRAINT DF_ImportRunTarget_Errored DEFAULT 0,
        CONSTRAINT PK_ImportRunTarget PRIMARY KEY (ImportRunId, TargetIndex)
    );
END
GO
