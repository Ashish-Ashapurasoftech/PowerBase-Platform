-- Widens Rating's catalog physical type from INT to DECIMAL(18,4) so it matches
-- Number/Currency/Percent (all already DECIMAL(18,4)). This only affects columns for
-- Rating fields created AFTER this migration runs — SchemaEngineService.AddColumnAsync
-- reads SqlDataType at column-creation time only, so no existing physical column is
-- touched here. INT -> DECIMAL is always lossless, so this is safe in every direction;
-- existing Rating fields still on a physical INT column are widened lazily, in place,
-- the first time a Number/Currency/Percent/Rating "Display As" switch touches them
-- (see SchemaEngineService.WidenIntColumnToDecimalIfNeededAsync).
IF EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Rating' AND SqlDataType <> 'DECIMAL(18,4)')
BEGIN
    UPDATE core.FieldType SET SqlDataType = 'DECIMAL(18,4)' WHERE Code = 'Rating';
END
GO
