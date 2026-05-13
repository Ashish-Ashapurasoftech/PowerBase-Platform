IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Report')
BEGIN
    CREATE TABLE meta.Report (
        Id             BIGINT IDENTITY(1,1) NOT NULL,
        PublicId       UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        TenantId       BIGINT        NOT NULL,
        AppTableId     BIGINT        NOT NULL,
        OwnerId        BIGINT        NOT NULL,
        Name           NVARCHAR(200) NOT NULL,
        Description    NVARCHAR(500) NULL,
        ReportType     TINYINT       NOT NULL DEFAULT 0,
        Visibility     TINYINT       NOT NULL DEFAULT 0,
        DefinitionJson NVARCHAR(MAX) NOT NULL DEFAULT '{}',
        IsDeleted      BIT           NOT NULL DEFAULT 0,
        CreatedAt      DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt      DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        RowVersion     ROWVERSION    NOT NULL,
        CONSTRAINT PK_Report PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_Report_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_Report_AppTable FOREIGN KEY (AppTableId) REFERENCES meta.AppTable(Id),
        CONSTRAINT FK_Report_Owner FOREIGN KEY (OwnerId) REFERENCES core.[User](Id)
    );
    CREATE NONCLUSTERED INDEX IX_Report_TenantId ON meta.Report(TenantId) WHERE IsDeleted = 0;
END
GO
