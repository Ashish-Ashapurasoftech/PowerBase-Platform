IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Report')
BEGIN
    CREATE TABLE meta.Report (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        PublicId     UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        TenantId     BIGINT        NOT NULL,
        AppTableId   BIGINT        NOT NULL,
        OwnerId      BIGINT        NOT NULL,
        Name         NVARCHAR(200) NOT NULL,
        Description  NVARCHAR(500) NULL,
        ReportType   VARCHAR(20)   NOT NULL DEFAULT 'Table',
        Visibility   VARCHAR(20)   NOT NULL DEFAULT 'Personal',
        Definition   NVARCHAR(MAX) NOT NULL DEFAULT '{}',
        IsDefault    BIT           NOT NULL DEFAULT 0,
        DisplayOrder INT           NOT NULL DEFAULT 0,
        IsDeleted    BIT           NOT NULL DEFAULT 0,
        CreatedOn    DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy    BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn   DATETIME2(3)  NULL,
        ModifiedBy   BIGINT        NULL,
        DeletedOn    DATETIME2(3)  NULL,
        DeletedBy    BIGINT        NULL,
        RowVersion   ROWVERSION    NOT NULL,
        CONSTRAINT PK_Report PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_Report_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_Report_AppTable FOREIGN KEY (AppTableId) REFERENCES meta.AppTable(Id),
        CONSTRAINT FK_Report_Owner FOREIGN KEY (OwnerId) REFERENCES core.[User](Id)
    );
    CREATE NONCLUSTERED INDEX IX_Report_TenantId ON meta.Report(TenantId) WHERE IsDeleted = 0;
END
GO
