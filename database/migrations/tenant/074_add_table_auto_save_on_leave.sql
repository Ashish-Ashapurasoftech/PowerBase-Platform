-- Adds the Table Advanced Setting "Auto save when redirected away from the page". While true, clicking a
-- Url / Formula URL link in an Add / Edit Record form saves the record (through the normal Save path, so every
-- validation still runs) before the link opens. See UpdateTableCommandHandler and the Add/Edit Record page.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppTable') AND name = 'IsAutoSaveOnLeave')
BEGIN
    ALTER TABLE meta.AppTable ADD IsAutoSaveOnLeave BIT NOT NULL CONSTRAINT DF_AppTable_IsAutoSaveOnLeave DEFAULT(0);
END
GO
