-- Add DateRange and NumericRange field types.
-- Each range field stores two physical columns: f_{fid} (start/min) and f_{fid}_e (end/max).

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'DateRange')
BEGIN
    SET IDENTITY_INSERT core.FieldType ON;
    INSERT INTO core.FieldType (Id, Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive) VALUES
        (22, 'DateRange',    'Date Range',    'Date',    'DATE',           0, 1, 0, 14, 1),
        (23, 'NumericRange', 'Numeric Range', 'Numeric', 'DECIMAL(18,4)',  0, 1, 0, 10, 1);
    SET IDENTITY_INSERT core.FieldType OFF;
END
GO
