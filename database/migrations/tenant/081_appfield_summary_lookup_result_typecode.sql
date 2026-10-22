-- Migration 081: Summary and Lookup fields save the type of value they hold in Settings.resultTypeCode — a field type
-- code (Text, Number, Currency, Date, Boolean, …) — so a client can type a lookup of a summary ("Dept - Min of Salary"
-- holds a number) without reading other tables. New fields get it when they are created or edited (FieldValueTypes);
-- this fills it in for the ones saved before. Mirrors FieldValueTypes.Of: a stored field holds its own type, a Formula
-- what it returns, a Summary what its function produces, a Lookup what its source holds. A field whose type can't be
-- told is left without it (clients fall back to working it out). One batch: the temp tables live across the passes.
SET NOCOUNT ON;

-- A formula result ("Number", "Bool", "ListUser" …) or a Formula_{X} suffix, as the field type that holds such a value.
CREATE TABLE #FormulaResult (Result NVARCHAR(50) NOT NULL PRIMARY KEY, ValueType NVARCHAR(50) NOT NULL);
INSERT INTO #FormulaResult (Result, ValueType) VALUES
    ('Text', 'Text'), ('Number', 'Number'), ('Date', 'Date'), ('DateTime', 'DateTime'), ('Duration', 'Duration'),
    ('Time', 'Time'), ('Bool', 'Boolean'), ('Boolean', 'Boolean'), ('User', 'User'), ('ListUser', 'MultiUser'),
    ('Email', 'Email'), ('Phone', 'Phone'), ('Url', 'Url'), ('RichText', 'RichText'), ('MultiSelect', 'MultiSelect');

-- Every live field with the type of value it holds, where that is known up front. Settings that aren't valid JSON are
-- treated as missing, so JSON_VALUE never sees them.
SELECT af.Id, af.AppTableId, af.Fid, ft.Code AS TypeCode,
       CASE WHEN ISJSON(af.Settings) = 1 THEN af.Settings END AS Settings,
       CAST(NULL AS NVARCHAR(50)) AS ValueType
INTO #F
FROM meta.AppField af
JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
WHERE af.IsDeleted = 0;

-- Stored fields hold their own type; a Report Link and action buttons hold none.
UPDATE #F SET ValueType = TypeCode
WHERE TypeCode NOT IN ('Formula', 'Lookup', 'Summary', 'ReportLink')
  AND TypeCode NOT LIKE 'Formula[_]%' AND TypeCode NOT LIKE 'ActionButton%';

-- Formula_{X} holds what X says; a generic Formula what its resultType says (Text when it says nothing).
UPDATE f SET ValueType = r.ValueType
FROM #F f JOIN #FormulaResult r ON r.Result = SUBSTRING(f.TypeCode, 9, 50)
WHERE f.TypeCode LIKE 'Formula[_]%';

UPDATE f SET ValueType = r.ValueType
FROM #F f JOIN #FormulaResult r ON r.Result = COALESCE(JSON_VALUE(f.Settings, '$.resultType'), 'Text')
WHERE f.TypeCode = 'Formula';

-- A Summary / Lookup that already says what it holds.
UPDATE #F SET ValueType = JSON_VALUE(Settings, '$.resultTypeCode')
WHERE TypeCode IN ('Summary', 'Lookup') AND JSON_VALUE(Settings, '$.resultTypeCode') IS NOT NULL;

-- The rest, in passes: a summary's value type can depend on a lookup's (Max of a looked-up date), a lookup's on a
-- summary's (a lookup of "Min of Salary") or another lookup's.
DECLARE @pass INT = 0;
WHILE @pass < 5
BEGIN
    UPDATE f SET ValueType =
        CASE LOWER(JSON_VALUE(f.Settings, '$.function'))
            WHEN 'count' THEN 'Number'
            WHEN 'distinctcount' THEN 'Number'
            WHEN 'exists' THEN 'Boolean'
            WHEN 'combinedtext' THEN 'Text'
            WHEN 'sum' THEN t.ValueType
            WHEN 'avg' THEN t.ValueType
            WHEN 'min' THEN CASE WHEN JSON_VALUE(f.Settings, '$.targetSubField') IS NOT NULL THEN 'Text' ELSE t.ValueType END
            WHEN 'max' THEN CASE WHEN JSON_VALUE(f.Settings, '$.targetSubField') IS NOT NULL THEN 'Text' ELSE t.ValueType END
        END
    FROM #F f
    LEFT JOIN #F t ON t.AppTableId = TRY_CAST(JSON_VALUE(f.Settings, '$.childTableId') AS BIGINT)
                  AND t.Fid = TRY_CAST(JSON_VALUE(f.Settings, '$.targetFid') AS INT)
    WHERE f.TypeCode = 'Summary' AND f.ValueType IS NULL AND f.Settings IS NOT NULL;

    UPDATE f SET ValueType =
        CASE WHEN JSON_VALUE(f.Settings, '$.sourceSubField') IS NOT NULL THEN 'Text' ELSE s.ValueType END
    FROM #F f
    LEFT JOIN #F s ON s.AppTableId = TRY_CAST(JSON_VALUE(f.Settings, '$.sourceTableId') AS BIGINT)
                  AND s.Fid = TRY_CAST(JSON_VALUE(f.Settings, '$.sourceFid') AS INT)
    WHERE f.TypeCode = 'Lookup' AND f.ValueType IS NULL AND f.Settings IS NOT NULL;

    SET @pass += 1;
END

UPDATE af SET Settings = JSON_MODIFY(af.Settings, '$.resultTypeCode', f.ValueType)
FROM meta.AppField af
JOIN #F f ON f.Id = af.Id
WHERE f.TypeCode IN ('Summary', 'Lookup')
  AND f.ValueType IS NOT NULL
  AND f.Settings IS NOT NULL
  AND JSON_VALUE(f.Settings, '$.resultTypeCode') IS NULL;

DROP TABLE #F;
DROP TABLE #FormulaResult;
