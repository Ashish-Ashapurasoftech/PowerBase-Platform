-- Tenant DB: add meta.FormRuleCondition.ConditionKind ('field' | 'role') and make AppFieldId
-- nullable, so a condition can express something other than "current value of one field OP
-- value" — specifically a User Role condition, which has no appFieldId at all. Field-change-
-- detection ('changed'/'notChanged') stays a plain Operator value on a 'field'-kind condition,
-- since it's still about one appFieldId, just compared against the record's original value
-- instead of an authored one — no schema change needed for that part.
--
-- There is no FK on AppFieldId (migration 013 dropped FK_FormRuleCondition_Field, since the
-- column stores Fid values, not AppField.Id) and no index on it either, so this is a plain
-- ALTER COLUMN, unlike 022's FormElement.AppFieldId nullability change which also had to drop
-- and recreate an index.

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleCondition') AND name = 'ConditionKind')
BEGIN
    ALTER TABLE meta.FormRuleCondition ADD ConditionKind VARCHAR(20) NOT NULL DEFAULT 'field';
END
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleCondition') AND name = 'AppFieldId' AND is_nullable = 0)
BEGIN
    ALTER TABLE meta.FormRuleCondition ALTER COLUMN AppFieldId INT NULL;
END
GO
