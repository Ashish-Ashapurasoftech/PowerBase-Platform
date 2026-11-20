-- Migration 096: a form rule action aimed at a field remembers the FIELD, not only the form element that showed it.
-- Until now an action stored the element's PublicId. Taking the field off the form deleted the element, so the action pointed at
-- nothing and nobody could tell which field it had been; putting the field back made a new element, which the rule never found.
-- TargetFieldId (the field's Fid, the same number FormElement.AppFieldId holds) is the real target: the designer shows the field
-- by name, flags the rule while the field is off the form, and the rule works again as soon as the field is back on it.
-- Additive: one nullable column, filled in from the element each existing Field action points at.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'TargetFieldId')
    ALTER TABLE meta.FormRuleAction ADD TargetFieldId BIGINT NULL;
GO

EXEC('
UPDATE ra SET ra.TargetFieldId = el.AppFieldId
FROM meta.FormRuleAction ra
JOIN meta.FormElement el ON (ra.TargetElementPublicId IS NOT NULL AND el.PublicId = ra.TargetElementPublicId)
                         OR (ra.TargetElementPublicId IS NULL AND el.Id = ra.TargetElementId)
WHERE ra.TargetType = ''Field'' AND ra.TargetFieldId IS NULL AND el.ElementType = ''Field'' AND el.AppFieldId IS NOT NULL;
');
GO

-- Copy App keeps the new column: patch the procedure's FormRuleAction insert in place (the procedure is far too long to restate here).
DECLARE @def NVARCHAR(MAX) = OBJECT_DEFINITION(OBJECT_ID('meta.usp_CopyAppSchema'));
IF @def IS NOT NULL AND CHARINDEX('TargetFieldId', @def) = 0
BEGIN
    SET @def = REPLACE(@def, 'TargetPagePublicId, ValueSource, SourceFieldId, MessageType, MessageSeverity)', 'TargetPagePublicId, ValueSource, SourceFieldId, MessageType, MessageSeverity, TargetFieldId)');
    SET @def = REPLACE(@def, 'a.ValueSource, a.SourceFieldId, a.MessageType, a.MessageSeverity', 'a.ValueSource, a.SourceFieldId, a.MessageType, a.MessageSeverity, a.TargetFieldId');
    IF CHARINDEX('TargetFieldId', @def) = 0 THROW 50000, 'Migration 096: could not patch usp_CopyAppSchema', 1;
    -- OBJECT_DEFINITION returns the text as "CREATE PROCEDURE ..." (the original CREATE OR ALTER is not kept), which fails on an
    -- existing procedure: turn the leading CREATE into ALTER.
    DECLARE @pos INT = CHARINDEX('CREATE', @def);
    IF @pos = 0 THROW 50000, 'Migration 096: usp_CopyAppSchema has no CREATE header', 1;
    IF UPPER(SUBSTRING(@def, @pos, 15)) = 'CREATE OR ALTER' SET @def = STUFF(@def, @pos, 15, 'ALTER');
    ELSE SET @def = STUFF(@def, @pos, 6, 'ALTER');
    SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON;
    EXEC sys.sp_executesql @def;
END
GO
