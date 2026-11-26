-- Migration 098: Quickbase source ids on imported apps and tables.
--   meta.App.QBAppID            — the Quickbase app id (e.g. bunx343v9), from the backend's fixed app catalog or typed in the import wizard
--   meta.App.QBAppLogicalID     — the app's logical id in the QBL file (e.g. $App_HIH_Archive)
--   meta.AppTable.QBTableID     — the Quickbase table id (e.g. bunx343yd), from the Quickbase "get tables" API, matched on the table alias
--   meta.AppTable.QBTableLogicalID — the table's logical id in the QBL file (e.g. $Table_Clients)
-- Set only by the app import (new app, or empty columns on update). All nullable: apps created in PowerBase have none.
-- usp_CopyAppSchema does not copy them — a copy is not the Quickbase app. Idempotent.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.App') AND name = 'QBAppID')
    ALTER TABLE meta.App ADD QBAppID NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.App') AND name = 'QBAppLogicalID')
    ALTER TABLE meta.App ADD QBAppLogicalID NVARCHAR(200) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppTable') AND name = 'QBTableID')
    ALTER TABLE meta.AppTable ADD QBTableID NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppTable') AND name = 'QBTableLogicalID')
    ALTER TABLE meta.AppTable ADD QBTableLogicalID NVARCHAR(200) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('meta.App') AND name = 'IX_App_QBAppID')
    CREATE INDEX IX_App_QBAppID ON meta.App (QBAppID) WHERE QBAppID IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('meta.AppTable') AND name = 'IX_AppTable_QBTableID')
    CREATE INDEX IX_AppTable_QBTableID ON meta.AppTable (QBTableID) WHERE QBTableID IS NOT NULL;
GO
