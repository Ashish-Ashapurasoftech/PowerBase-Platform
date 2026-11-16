-- Tenant DB: meta.FieldReferenceIndexState.IndexVersion — which generation of the index a table was last built with.
--
-- The index learns new kinds of source over time (v1: reports, forms, form rules, fields; v2: + relationships,
-- dashboards, custom rules). A table indexed under an older version has none of the newer rows, and "already
-- indexed" used to be a yes/no flag, so those tables would never have been rebuilt. The code now compares this
-- number with its current version (FieldReferenceIndexVersion.Current) and rebuilds a table once when it is behind.
--
-- Existing rows get version 1. Re-running is harmless.

IF EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'FieldReferenceIndexState')
   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FieldReferenceIndexState') AND name = 'IndexVersion')
BEGIN
    ALTER TABLE meta.FieldReferenceIndexState ADD IndexVersion INT NOT NULL CONSTRAINT DF_FieldReferenceIndexState_IndexVersion DEFAULT 1;
END
GO
