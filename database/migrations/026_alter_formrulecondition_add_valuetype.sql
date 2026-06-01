IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleCondition') AND name = 'ValueType')
BEGIN
    ALTER TABLE meta.FormRuleCondition
        ADD ValueType    VARCHAR(30)  NULL,
            ValueFieldId BIGINT       NULL;
END
GO
