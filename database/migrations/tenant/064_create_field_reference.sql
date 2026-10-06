-- Tenant DB: meta.FieldReference — a reverse index of "what uses this field".
--
-- One row per (source, target field, usage): a report column/filter/sort/group-by/aggregation/
-- dynamic filter, a form element, a form-rule condition/action, a formula that reads the field
-- (Formula_* fields, Action Button formulas, rule expressions), or a Lookup/Summary/Report Link/
-- Action Button setting that points at it. It lets the Usage tab (and any future "can I delete
-- this field?" check) answer with one indexed query instead of scanning every report definition
-- and formula.
--
-- Derived data: every row is rebuilt from the real source (replace-all-rows-of-a-source on every
-- save), so the table can always be dropped and re-created from scratch — see
-- IFieldReferenceIndexer.RebuildTableAsync. Deliberately NO foreign keys: sources are soft-deleted,
-- fields can outlive the things referring to them, and a stale index row must never be able to
-- block a delete elsewhere.
--
-- SourceId is the source's internal Id in its own table (meta.Report / meta.Form / meta.FormRule /
-- meta.AppField), per SourceType. TargetFieldId is meta.AppField.Id (never the per-table Fid).

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'FieldReference')
BEGIN
    CREATE TABLE meta.FieldReference (
        Id            BIGINT IDENTITY(1,1) NOT NULL,
        SourceType    VARCHAR(20) NOT NULL,   -- Report | Form | FormRule | Field
        SourceId      BIGINT      NOT NULL,
        SourceTableId BIGINT      NOT NULL,   -- the table the source lives on
        TargetFieldId BIGINT      NOT NULL,
        Usage         VARCHAR(40) NOT NULL,   -- column | filter | sort | ... see FieldReferenceUsages
        CONSTRAINT PK_FieldReference PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_FieldReference UNIQUE (SourceType, SourceId, TargetFieldId, Usage)
    );

    -- "What uses this field?" — the Usage tab's query.
    CREATE NONCLUSTERED INDEX IX_FieldReference_Target ON meta.FieldReference (TargetFieldId) INCLUDE (SourceType, SourceId, Usage);
    -- "Replace everything this source references" — delete-then-insert on every save.
    CREATE NONCLUSTERED INDEX IX_FieldReference_Source ON meta.FieldReference (SourceType, SourceId);
    -- "Rebuild / drop everything for this table".
    CREATE NONCLUSTERED INDEX IX_FieldReference_SourceTable ON meta.FieldReference (SourceTableId);
END
GO

-- Marks a table whose existing reports/forms/rules/fields have already been indexed, so the
-- first Usage lookup on a table that predates meta.FieldReference can backfill it exactly once
-- (a table with no references at all is otherwise indistinguishable from one never indexed).
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'FieldReferenceIndexState')
BEGIN
    CREATE TABLE meta.FieldReferenceIndexState (
        AppTableId BIGINT       NOT NULL,
        IndexedOn  DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_FieldReferenceIndexState PRIMARY KEY CLUSTERED (AppTableId)
    );
END
GO
