-- Migration 028: Group table (Tenant Workspace)

-- 1. meta.Group
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Group')
BEGIN
    CREATE TABLE meta.[Group] (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        PublicId    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        Name        NVARCHAR(100)    NOT NULL,
        Description NVARCHAR(500)    NULL,
        CreatedOn   DATETIME2(3)     NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy   BIGINT           NOT NULL,
        ModifiedOn  DATETIME2(3)     NULL,
        ModifiedBy  BIGINT           NULL,
        IsDeleted   BIT              NOT NULL DEFAULT 0,
        DeletedOn   DATETIME2(3)     NULL,
        DeletedBy   BIGINT           NULL,
        RowVersion  ROWVERSION       NOT NULL,
        CONSTRAINT PK_Group PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_Group_PublicId UNIQUE (PublicId)
    );

    CREATE INDEX IX_Group_Name ON meta.[Group](Name) WHERE IsDeleted = 0;
END
GO
