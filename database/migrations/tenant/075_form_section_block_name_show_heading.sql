-- Form designer: every column (FormSectionBlock) gets an internal Name that is kept even when its heading
-- is hidden, and sections / columns get a ShowHeading flag. Form rules reference components by name, so a
-- component must never lose its name just because its heading is hidden.
--   * Existing columns with a Heading keep showing it (Name = Heading, ShowHeading = 1).
--   * Existing columns with no Heading get a unique "Column N" name per section and ShowHeading = 0.
--   * Existing sections keep showing their heading (ShowHeading = 1). Newly added ones default to hidden
--     in the designer, which sends the flag explicitly.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSectionBlock') AND name = 'Name')
BEGIN
    ALTER TABLE meta.FormSectionBlock ADD Name NVARCHAR(200) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSectionBlock') AND name = 'ShowHeading')
BEGIN
    ALTER TABLE meta.FormSectionBlock ADD ShowHeading BIT NOT NULL CONSTRAINT DF_FormSectionBlock_ShowHeading DEFAULT(1);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSection') AND name = 'ShowHeading')
BEGIN
    ALTER TABLE meta.FormSection ADD ShowHeading BIT NOT NULL CONSTRAINT DF_FormSection_ShowHeading DEFAULT(1);
END
GO

-- Backfill (dynamic SQL so the new columns exist when the batch is compiled).
EXEC('
UPDATE meta.FormSectionBlock
SET Name = Heading
WHERE Name IS NULL AND Heading IS NOT NULL AND LTRIM(RTRIM(Heading)) <> '''';

;WITH numbered AS (
    SELECT Id, ROW_NUMBER() OVER (PARTITION BY FormSectionId ORDER BY DisplayOrder, Id) AS rn
    FROM meta.FormSectionBlock
    WHERE Name IS NULL
)
UPDATE b
SET b.Name = ''Column '' + CAST(n.rn AS NVARCHAR(10)),
    b.ShowHeading = 0
FROM meta.FormSectionBlock b
JOIN numbered n ON n.Id = b.Id;
');
GO
