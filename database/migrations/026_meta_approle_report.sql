IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppRoleReport')
BEGIN
    CREATE TABLE meta.AppRoleReport (
        ReportId BIGINT NOT NULL,
        AppRoleId BIGINT NOT NULL,
        CONSTRAINT PK_AppRoleReport PRIMARY KEY CLUSTERED (ReportId, AppRoleId),
        CONSTRAINT FK_AppRoleReport_Report FOREIGN KEY (ReportId) REFERENCES meta.Report(Id),
        CONSTRAINT FK_AppRoleReport_AppRole FOREIGN KEY (AppRoleId) REFERENCES meta.AppRole(Id)
    );
END
GO
