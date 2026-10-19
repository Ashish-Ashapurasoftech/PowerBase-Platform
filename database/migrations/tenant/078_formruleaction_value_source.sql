-- "Change value" rule actions can take their new value from more than a typed literal or a formula:
--   ValueSource = 'literal' (typed value, ActionValue) | 'field' (copy another field, SourceFieldId) |
--                 'clear' (empty the target) | 'formula' (ActionValue is a formula, IsExpressionValue = 1).
-- NULL means a rule saved before this existed: formula when IsExpressionValue = 1, otherwise literal.
-- SourceFieldId is the per-table Fid of the field to copy (same identity the rule conditions use).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'ValueSource')
    ALTER TABLE meta.FormRuleAction ADD ValueSource VARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'SourceFieldId')
    ALTER TABLE meta.FormRuleAction ADD SourceFieldId BIGINT NULL;
GO
