-- Migration 097: meta.FormRuleAction.ActionValue NVARCHAR(500) -> NVARCHAR(MAX).
-- A form rule's "show message" / "prevent save" text (or its formula) can be longer than 500 characters: a Quickbase app
-- (Solution Lifecycle Manager) carries a several-paragraph warning, and inserting it failed with "String or binary data would
-- be truncated", rolling back the whole app import. Widening keeps the text exactly as written. No index or constraint uses the
-- column, and usp_CopyAppSchema copies it with INSERT ... SELECT, so nothing else changes. Idempotent.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'ActionValue' AND max_length <> -1
)
    ALTER TABLE meta.FormRuleAction ALTER COLUMN ActionValue NVARCHAR(MAX) NULL;
GO
