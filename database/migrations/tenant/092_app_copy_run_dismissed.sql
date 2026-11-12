-- Copy App with data: the app list shows the caller's copies (running, done or failed) until they dismiss them, so a
-- copy whose dialog was closed can still be followed (GET apps/copies). Additive.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH(N'meta.AppCopyRun', N'IsDismissed') IS NULL
    ALTER TABLE meta.AppCopyRun ADD IsDismissed BIT NOT NULL CONSTRAINT DF_AppCopyRun_IsDismissed DEFAULT 0;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'meta.AppCopyRun') AND name = N'IX_AppCopyRun_StartedBy')
    CREATE INDEX IX_AppCopyRun_StartedBy ON meta.AppCopyRun (StartedBy, IsDismissed, Status);
GO
