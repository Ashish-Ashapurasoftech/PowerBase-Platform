DECLARE @dbname NVARCHAR(MAX)
DECLARE @sql NVARCHAR(MAX)
DECLARE db_cursor CURSOR FOR 
SELECT name FROM sys.databases WHERE name LIKE 'Powerbase_%'

OPEN db_cursor  
FETCH NEXT FROM db_cursor INTO @dbname  

WHILE @@FETCH_STATUS = 0  
BEGIN  
      SET @sql = 'UPDATE ' + @dbname + '.meta.App 
      SET SecurityOptions = ''{"AllowNonAdminsToCopy":false,"AllowNonAdminsToExport":true,"AllowNonAdminsToConnect":true,"HideFromPublicSearch":false,"AllowCrawlerIndexing":false,"RequireAppTokens":true,"OnlyApprovedUsersAccess":false,"OnlyApprovedIpAddressesAccess":false,"WrappedDek":"'' + SecurityOptions + ''"}'' 
      WHERE IsEncrypted = 1 AND SecurityOptions NOT LIKE ''{%'';'
      EXEC(@sql)
      FETCH NEXT FROM db_cursor INTO @dbname 
END 

CLOSE db_cursor  
DEALLOCATE db_cursor
