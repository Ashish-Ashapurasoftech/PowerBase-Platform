-- Adds the explicit "default Quick Peek form" marker to meta.Form. A table may have several Quick
-- Peek forms (IsQuickPeekForm, migration 053); exactly one of them is the default - the one used by
-- every report set to "Use table default". Like the main default form (IsDefault), the default
-- Quick Peek form cannot be un-flagged or deleted; another Quick Peek form must be made the
-- default first. Existing tables keep today's behaviour: the first flagged form by DisplayOrder
-- becomes the default.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.Form') AND name = 'IsQuickPeekDefault')
BEGIN
    ALTER TABLE meta.Form ADD IsQuickPeekDefault BIT NOT NULL CONSTRAINT DF_Form_IsQuickPeekDefault DEFAULT(0);
END
GO

;WITH firstFlagged AS (
    SELECT Id, ROW_NUMBER() OVER (PARTITION BY AppTableId ORDER BY DisplayOrder, Id) AS rn
    FROM meta.Form
    WHERE IsQuickPeekForm = 1 AND IsDeleted = 0
)
UPDATE f SET IsQuickPeekDefault = 1
FROM meta.Form f
JOIN firstFlagged x ON x.Id = f.Id AND x.rn = 1
WHERE f.IsQuickPeekDefault = 0;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Form_QuickPeekDefault' AND object_id = OBJECT_ID('meta.Form'))
BEGIN
    CREATE UNIQUE INDEX UX_Form_QuickPeekDefault ON meta.Form (AppTableId)
        WHERE IsQuickPeekDefault = 1 AND IsDeleted = 0;
END
GO
