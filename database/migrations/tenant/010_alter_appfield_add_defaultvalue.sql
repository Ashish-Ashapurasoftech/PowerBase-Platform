-- Migration 010: Add DefaultValue column to meta.AppField (tenant DB)
-- Needed for any tenant DB created before DefaultValue was introduced to the schema.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns c
    JOIN   sys.tables  t ON t.object_id = c.object_id
    JOIN   sys.schemas s ON s.schema_id = t.schema_id
    WHERE  s.name    = 'meta'
      AND  t.name    = 'AppField'
      AND  c.name    = 'DefaultValue'
)
BEGIN
    ALTER TABLE meta.AppField
        ADD DefaultValue NVARCHAR(500) NULL;
END
