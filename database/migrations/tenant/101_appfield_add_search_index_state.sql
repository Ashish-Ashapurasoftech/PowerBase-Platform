-- Whether a stored Lookup / Summary / Formula field (c_{fid}) is fully in the Azure AI Search index, so reads may take it from there.
-- Kept apart from MaterializeState on purpose: MaterializeState = 2 (Ready) still means "c_{fid} may be read in SQL", whatever the index holds.
-- SearchIndexState   0 = none, 1 = indexing (field job sent, not finished), 2 = indexed, 3 = failed (the field job gave up).
-- SearchIndexVersion the MaterializeVersion that was sent to the index. The field is "in AI" only while it equals MaterializeVersion, so a
--                    rebuild (which bumps MaterializeVersion) stops reads from the index at once, with no extra update.
-- SearchIndexedOn    when the field job for that version finished.
-- SearchExact        1 = every stored value comes back unchanged from the index (a double holds about 15 significant digits, c_ is
--                    DECIMAL(38,8)); 0 = at least one does not, so sort and the shown value of this field stay on SQL c_{fid}.
-- All defaults are constants (or NULL), so adding the columns is a metadata-only change. Additive and safe to re-run.

IF COL_LENGTH('meta.AppField', 'SearchIndexState') IS NULL
    ALTER TABLE meta.AppField ADD SearchIndexState TINYINT NOT NULL CONSTRAINT DF_AppField_SearchIndexState DEFAULT 0;
GO

IF COL_LENGTH('meta.AppField', 'SearchIndexVersion') IS NULL
    ALTER TABLE meta.AppField ADD SearchIndexVersion INT NULL;
GO

IF COL_LENGTH('meta.AppField', 'SearchIndexedOn') IS NULL
    ALTER TABLE meta.AppField ADD SearchIndexedOn DATETIME2(3) NULL;
GO

IF COL_LENGTH('meta.AppField', 'SearchExact') IS NULL
    ALTER TABLE meta.AppField ADD SearchExact BIT NOT NULL CONSTRAINT DF_AppField_SearchExact DEFAULT 1;
GO
