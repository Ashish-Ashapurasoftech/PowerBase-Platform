-- Stored (materialized) column bookkeeping for Lookup / Summary / Formula fields: the value is cached in data.t_X.c_{fid}.
-- MaterializeMode   0 = off, 1 = on, 2 = not allowed (the field can never be stored; reads keep today's dynamic path)
-- MaterializeState  0 = none, 1 = building, 2 = ready, 3 = failed. Reads use c_{fid} ONLY when the state is 2.
-- MaterializeVersion bumped when the formula / summary settings / relationship change; the worker writes only the current version.
-- MaterializedOn    when the last full build finished.
-- All defaults are constants, so adding the columns is a metadata-only change. Additive and safe to re-run.

IF COL_LENGTH('meta.AppField', 'MaterializeMode') IS NULL
    ALTER TABLE meta.AppField ADD MaterializeMode TINYINT NOT NULL CONSTRAINT DF_AppField_MaterializeMode DEFAULT 0;
GO

IF COL_LENGTH('meta.AppField', 'MaterializeState') IS NULL
    ALTER TABLE meta.AppField ADD MaterializeState TINYINT NOT NULL CONSTRAINT DF_AppField_MaterializeState DEFAULT 0;
GO

IF COL_LENGTH('meta.AppField', 'MaterializeVersion') IS NULL
    ALTER TABLE meta.AppField ADD MaterializeVersion INT NOT NULL CONSTRAINT DF_AppField_MaterializeVersion DEFAULT 0;
GO

IF COL_LENGTH('meta.AppField', 'MaterializedOn') IS NULL
    ALTER TABLE meta.AppField ADD MaterializedOn DATETIME2(3) NULL;
GO
