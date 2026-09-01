-- Adds the "Turn custom data rules on?" toggle alongside CustomDataRule (050). While false, the
-- stored formula is not evaluated on record writes (and not even syntax-validated at save time) —
-- lets an admin draft/save an incomplete rule before switching enforcement on. See
-- PowerBase.Application.Records.CustomDataRuleValidator and UpdateTableCommandHandler.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppTable') AND name = 'IsCustomDataRuleEnabled')
BEGIN
    ALTER TABLE meta.AppTable ADD IsCustomDataRuleEnabled BIT NOT NULL CONSTRAINT DF_AppTable_IsCustomDataRuleEnabled DEFAULT(0);
END
GO
