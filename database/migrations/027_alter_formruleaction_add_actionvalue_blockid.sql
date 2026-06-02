IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'ActionValue')
BEGIN
    ALTER TABLE meta.FormRuleAction
        ADD ActionValue   NVARCHAR(500) NULL,
            TargetBlockId BIGINT        NULL;
END
GO
