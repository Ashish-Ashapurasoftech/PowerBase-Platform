-- Tenant DB: table-to-table relationships (one-to-many).
-- One parent record has many child records; the link is stored on the child via a
-- Reference field (a physical BIGINT column holding the parent row Id). Lookup fields
-- (child) and Summary fields (parent) are computed at read time from this link.

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Relationship')
BEGIN
    CREATE TABLE meta.Relationship (
        Id               BIGINT IDENTITY(1,1) NOT NULL,
        PublicId         UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        AppId            BIGINT        NOT NULL,
        ParentTableId    BIGINT        NOT NULL,   -- the "one" side
        ChildTableId     BIGINT        NOT NULL,   -- the "many" side; owns the reference field
        ReferenceFieldId BIGINT        NOT NULL,   -- child meta.AppField.Id storing the parent row Id (FK)
        ReferenceFid     INT           NOT NULL,   -- the reference field's Fid (its f_{fid} column on the child data table)
        ProxyFieldId     BIGINT        NULL,        -- child Lookup meta.AppField.Id used as the display label
        IsDeleted        BIT           NOT NULL DEFAULT 0,
        CreatedOn        DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy        BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn       DATETIME2(3)  NULL,
        ModifiedBy       BIGINT        NULL,
        DeletedOn        DATETIME2(3)  NULL,
        DeletedBy        BIGINT        NULL,
        CONSTRAINT PK_Relationship PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_Relationship_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_Relationship_App         FOREIGN KEY (AppId)         REFERENCES meta.App(Id),
        CONSTRAINT FK_Relationship_ParentTable FOREIGN KEY (ParentTableId) REFERENCES meta.AppTable(Id),
        CONSTRAINT FK_Relationship_ChildTable  FOREIGN KEY (ChildTableId)  REFERENCES meta.AppTable(Id)
    );
    CREATE NONCLUSTERED INDEX IX_Relationship_AppId        ON meta.Relationship(AppId)         WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IX_Relationship_ParentTable  ON meta.Relationship(ParentTableId) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IX_Relationship_ChildTable   ON meta.Relationship(ChildTableId)  WHERE IsDeleted = 0;
END
GO
