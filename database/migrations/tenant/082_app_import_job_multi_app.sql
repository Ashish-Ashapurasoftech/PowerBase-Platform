-- A QBL file can hold several applications, each imported as its own app. A job records how many applications it
-- tried to import and how many were created (AppPublicId/AppName then describe the first one created).
IF COL_LENGTH('meta.AppImportJob', 'AppCount') IS NULL
    ALTER TABLE meta.AppImportJob ADD AppCount INT NOT NULL CONSTRAINT DF_AppImportJob_AppCount DEFAULT 1;
GO

IF COL_LENGTH('meta.AppImportJob', 'AppsCreated') IS NULL
    ALTER TABLE meta.AppImportJob ADD AppsCreated INT NOT NULL CONSTRAINT DF_AppImportJob_AppsCreated DEFAULT 0;
GO
