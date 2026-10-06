-- Progress is derived from the source cursor, so the pre-counted total is not needed.
IF COL_LENGTH('meta.ImportRun', 'TotalSourceRows') IS NOT NULL
    ALTER TABLE meta.ImportRun DROP COLUMN TotalSourceRows;
GO
