-- Files uploaded to import from (CSV / Excel). A file is kept only until its import ends, it is discarded, or a day passes.
IF OBJECT_ID('meta.ImportFile') IS NULL
BEGIN
    CREATE TABLE meta.ImportFile (
        Id               BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ImportFile PRIMARY KEY,
        PublicId         UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ImportFile_PublicId DEFAULT NEWID(),
        UploadedByUserId BIGINT NOT NULL,
        FileName         NVARCHAR(260) NOT NULL,
        StoragePath      NVARCHAR(500) NOT NULL,
        Format           NVARCHAR(10) NOT NULL,           -- csv | xlsx
        SizeBytes        BIGINT NOT NULL,
        CreatedOn        DATETIME2(3) NOT NULL CONSTRAINT DF_ImportFile_CreatedOn DEFAULT SYSUTCDATETIME()
    );
    CREATE UNIQUE INDEX UX_ImportFile_PublicId ON meta.ImportFile (PublicId);
    CREATE INDEX IX_ImportFile_CreatedOn ON meta.ImportFile (CreatedOn);
END
GO
