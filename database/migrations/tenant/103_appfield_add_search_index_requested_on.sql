-- When a stored field's values were last asked to go to the search index (the field job sent at Ready, or a whole-field re-send).
-- A field job marks the field Indexed only if the job was created at or after this moment: a job still running for an older build
-- (or an older re-send) can therefore never mark a newer one as being in the index. MaterializedOn is not used for this, because the
-- nightly check and the daily refresh move it forward while a job is running.
-- Nullable, no default: a metadata-only change. Additive and safe to re-run.

IF COL_LENGTH('meta.AppField', 'SearchIndexRequestedOn') IS NULL
    ALTER TABLE meta.AppField ADD SearchIndexRequestedOn DATETIME2(3) NULL;
GO
