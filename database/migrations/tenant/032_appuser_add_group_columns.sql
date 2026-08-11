IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'IsFromGroup' AND Object_ID = Object_ID(N'meta.AppUser'))
BEGIN
    ALTER TABLE meta.AppUser ADD IsFromGroup BIT NOT NULL DEFAULT 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'GroupId' AND Object_ID = Object_ID(N'meta.AppUser'))
BEGIN
    ALTER TABLE meta.AppUser ADD GroupId BIGINT NULL;
    ALTER TABLE meta.AppUser ADD CONSTRAINT FK_AppUser_Group FOREIGN KEY (GroupId) REFERENCES meta.[Group](Id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE Name = N'IX_AppUser_GroupId' AND Object_ID = Object_ID(N'meta.AppUser'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_AppUser_GroupId ON meta.AppUser(GroupId) WHERE IsDeleted = 0 AND GroupId IS NOT NULL;
END
GO
