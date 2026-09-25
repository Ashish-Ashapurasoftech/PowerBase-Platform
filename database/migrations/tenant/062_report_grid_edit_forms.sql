-- Tenant DB: a report's Grid Edit rule selection is now "pick FORMS, then opt individual rules out"
-- instead of hand-picking every rule.
--
--   * meta.ReportGridEditForm  — the forms a report has selected. Every active, applicable rule of a
--     selected form applies to that report's Grid Edit AUTOMATICALLY (including rules added to the
--     form later), except the ones explicitly excluded below.
--   * meta.ReportGridEditRule.IsExcluded — a rule of a selected form the user moved to the
--     "Available" side. DisplayOrder on the non-excluded rows is the report's own priority order
--     across forms ("top-most wins"); a rule with NO row (e.g. added to the form after the last
--     save) is appended after the stored ones, in form/rule order, and is applied.
--
-- Inactive/deleted rules are never fetched or shown on either side; their stored rows are simply
-- ignored (and would reappear in the same spot if the rule is reactivated).

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('meta.ReportGridEditForm'))
BEGIN
    CREATE TABLE meta.ReportGridEditForm (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        ReportId     BIGINT NOT NULL,
        FormId       BIGINT NOT NULL,
        DisplayOrder INT    NOT NULL DEFAULT 0,
        CONSTRAINT PK_ReportGridEditForm PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_ReportGridEditForm UNIQUE (ReportId, FormId),
        CONSTRAINT FK_ReportGridEditForm_Report FOREIGN KEY (ReportId) REFERENCES meta.Report(Id) ON DELETE CASCADE,
        CONSTRAINT FK_ReportGridEditForm_Form   FOREIGN KEY (FormId)   REFERENCES meta.Form(Id)
    );
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.ReportGridEditRule') AND name = 'IsExcluded')
BEGIN
    ALTER TABLE meta.ReportGridEditRule ADD IsExcluded BIT NOT NULL DEFAULT 0;
END
GO

-- Backfill, step 1: for a report that already saved individual rules, every OTHER rule of those
-- rules' forms is recorded as excluded — so the report keeps applying exactly what it applied before
-- this migration, rather than suddenly pulling in the rest of each form. (Rules created after this
-- point auto-apply, per the new model.) Must run BEFORE step 2, which is what marks the forms selected.
INSERT INTO meta.ReportGridEditRule (ReportId, FormRuleId, DisplayOrder, IsExcluded)
SELECT sel.ReportId, r2.Id, 0, 1
FROM (
    SELECT DISTINCT g.ReportId, fr.FormId
    FROM meta.ReportGridEditRule g
    JOIN meta.FormRule fr ON fr.Id = g.FormRuleId
    WHERE NOT EXISTS (SELECT 1 FROM meta.ReportGridEditForm f WHERE f.ReportId = g.ReportId AND f.FormId = fr.FormId)
) sel
JOIN meta.FormRule r2 ON r2.FormId = sel.FormId AND r2.IsDeleted = 0
WHERE NOT EXISTS (SELECT 1 FROM meta.ReportGridEditRule x WHERE x.ReportId = sel.ReportId AND x.FormRuleId = r2.Id);
GO

-- Backfill, step 2: those reports' forms become selected.
INSERT INTO meta.ReportGridEditForm (ReportId, FormId, DisplayOrder)
SELECT g.ReportId, r.FormId, ROW_NUMBER() OVER (PARTITION BY g.ReportId ORDER BY MIN(g.DisplayOrder))
FROM meta.ReportGridEditRule g
JOIN meta.FormRule r ON r.Id = g.FormRuleId
WHERE NOT EXISTS (SELECT 1 FROM meta.ReportGridEditForm f WHERE f.ReportId = g.ReportId AND f.FormId = r.FormId)
GROUP BY g.ReportId, r.FormId;
GO
