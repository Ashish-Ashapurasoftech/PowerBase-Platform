-- Non-field form elements (static text, divider, button, report, spacer) get an internal Name ("Text 1",
-- "Divider 1"…) so form rules can reference every component. Field elements stay NULL (known by field label).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'Name')
BEGIN
    ALTER TABLE meta.FormElement ADD Name NVARCHAR(200) NULL;
END
GO

-- Backfill: number per element type across the form, in layout order.
EXEC('
;WITH numbered AS (
    SELECT fe.Id,
           CASE fe.ElementType
                WHEN ''StaticText'' THEN ''Text''
                WHEN ''Divider''    THEN ''Divider''
                WHEN ''Button''     THEN ''Button''
                WHEN ''Report''     THEN ''Report''
                WHEN ''Spacer''     THEN ''Spacer''
           END AS Prefix,
           ROW_NUMBER() OVER (PARTITION BY fs.FormId, fe.ElementType ORDER BY fs.DisplayOrder, fe.FormSectionBlockId, fe.DisplayOrder, fe.Id) AS rn
    FROM meta.FormElement fe
    JOIN meta.FormSection fs ON fs.Id = fe.FormSectionId
    WHERE fe.Name IS NULL AND fe.ElementType <> ''Field''
)
UPDATE fe
SET fe.Name = n.Prefix + '' '' + CAST(n.rn AS NVARCHAR(10))
FROM meta.FormElement fe
JOIN numbered n ON n.Id = fe.Id
WHERE n.Prefix IS NOT NULL;
');
GO
