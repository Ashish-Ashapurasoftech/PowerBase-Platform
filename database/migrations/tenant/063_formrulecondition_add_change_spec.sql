-- Tenant DB: refine "has changed" / "has not changed" conditions with optional Old-value and
-- New-value sub-conditions, e.g. "changed FROM Today TO during last 3rd week". Each sub-condition
-- reuses the SAME operator/value vocabulary as a normal field condition (eq/ne/gt/lt/contains/
-- isEmpty/during/etc.) — evaluated against the field's value-before-this-session (From) and its
-- current value (To) instead of just "the two differ". Both are optional and independent; when
-- both are absent, evaluation is unchanged (any change / no change at all) — fully backward
-- compatible with every existing 'changed'/'notChanged' condition row.
--
-- Only meaningful when Operator IN ('changed', 'notChanged'); NULL otherwise. No FK on
-- ChangeFromValueFieldId/ChangeToValueFieldId, matching ValueFieldId's own column (Fid values,
-- not AppField.Id — see migration 013).

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleCondition') AND name = 'ChangeFromOperator')
BEGIN
    ALTER TABLE meta.FormRuleCondition ADD ChangeFromOperator VARCHAR(30) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleCondition') AND name = 'ChangeFromValue')
BEGIN
    ALTER TABLE meta.FormRuleCondition ADD ChangeFromValue NVARCHAR(500) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleCondition') AND name = 'ChangeFromValueType')
BEGIN
    ALTER TABLE meta.FormRuleCondition ADD ChangeFromValueType VARCHAR(30) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleCondition') AND name = 'ChangeFromValueFieldId')
BEGIN
    ALTER TABLE meta.FormRuleCondition ADD ChangeFromValueFieldId BIGINT NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleCondition') AND name = 'ChangeToOperator')
BEGIN
    ALTER TABLE meta.FormRuleCondition ADD ChangeToOperator VARCHAR(30) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleCondition') AND name = 'ChangeToValue')
BEGIN
    ALTER TABLE meta.FormRuleCondition ADD ChangeToValue NVARCHAR(500) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleCondition') AND name = 'ChangeToValueType')
BEGIN
    ALTER TABLE meta.FormRuleCondition ADD ChangeToValueType VARCHAR(30) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleCondition') AND name = 'ChangeToValueFieldId')
BEGIN
    ALTER TABLE meta.FormRuleCondition ADD ChangeToValueFieldId BIGINT NULL;
END
GO
