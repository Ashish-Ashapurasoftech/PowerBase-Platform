-- Migration 096: Reference fields save the type they are shown as in Settings.resultTypeCode (ReferenceSettings.ResultTypeCode),
-- the way Summary and Lookup fields already do (migration 081), so a client can name a Reference "Number (Reference)" without
-- reading the parent table. New References get it when the relationship is created (CreateRelationshipCommandHandler);
-- this fills it in for the ones saved before. Mirrors FieldValueTypes.ForNewReference / ForConvertedReference:
--   * a Reference created as a new field  → the type of the parent's key it shows: the relationship's Display Key, else the
--     parent table's Set Key, else Record ID# (Number); a formula / lookup / summary key as the type of value it produces;
--   * an existing child field converted into the Reference → that field's own type (ReferenceSettings.OriginalTypeCode, a
--     "Formula - text" field as Text), or Number when it was a Number field converted metadata-only (OriginalTypeCode null).
-- Only fills a missing value; never changes one already saved. Settings that aren't valid JSON are left alone.
-- One batch: the temp tables live across the statements.
SET NOCOUNT ON;

-- A formula result ("Number", "Bool", "ListUser" …) or a Formula_{X} suffix, as the field type that holds such a value.
CREATE TABLE #FormulaResult (Result NVARCHAR(50) NOT NULL PRIMARY KEY, ValueType NVARCHAR(50) NOT NULL);
INSERT INTO #FormulaResult (Result, ValueType) VALUES
    ('Text', 'Text'), ('Number', 'Number'), ('Date', 'Date'), ('DateTime', 'DateTime'), ('Duration', 'Duration'),
    ('Time', 'Time'), ('Bool', 'Boolean'), ('Boolean', 'Boolean'), ('User', 'User'), ('ListUser', 'MultiUser'),
    ('Email', 'Email'), ('Phone', 'Phone'), ('Url', 'Url'), ('RichText', 'RichText'), ('MultiSelect', 'MultiSelect');

-- Every live field with the type of value it holds, where that can be told from the field itself.
SELECT af.Id, af.AppTableId, af.Fid, ft.Code AS TypeCode,
       CASE WHEN ISJSON(af.Settings) = 1 THEN af.Settings END AS Settings,
       CAST(NULL AS NVARCHAR(50)) AS ValueType
INTO #F
FROM meta.AppField af
JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
WHERE af.IsDeleted = 0;

-- Stored fields hold their own type.
UPDATE #F SET ValueType = TypeCode
WHERE TypeCode NOT IN ('Formula', 'Lookup', 'Summary', 'ReportLink', 'Reference')
  AND TypeCode NOT LIKE 'Formula[_]%' AND TypeCode NOT LIKE 'ActionButton%';

-- Record ID# is a number whatever its stored type code.
UPDATE #F SET ValueType = 'Number' WHERE Fid = 3;

-- Formula_{X} holds what X says; a generic Formula what its resultType says (Text when it says nothing).
UPDATE f SET ValueType = r.ValueType
FROM #F f JOIN #FormulaResult r ON r.Result = SUBSTRING(f.TypeCode, 9, 50)
WHERE f.TypeCode LIKE 'Formula[_]%' AND f.Fid <> 3;

UPDATE f SET ValueType = r.ValueType
FROM #F f JOIN #FormulaResult r ON r.Result = COALESCE(JSON_VALUE(f.Settings, '$.resultType'), 'Text')
WHERE f.TypeCode = 'Formula' AND f.Fid <> 3;

-- A Lookup / Summary key holds what it saved (migration 081 filled it in).
UPDATE #F SET ValueType = JSON_VALUE(Settings, '$.resultTypeCode')
WHERE TypeCode IN ('Lookup', 'Summary') AND JSON_VALUE(Settings, '$.resultTypeCode') IS NOT NULL;

-- Each live Reference that has no saved type yet, with what decides it.
SELECT ref.Id, ref.Settings,
       rel.ReferenceFieldIsExisting AS IsExisting,
       JSON_VALUE(ref.Settings, '$.originalTypeCode') AS OriginalTypeCode,
       dk.ValueType AS DisplayKeyType,
       sk.ValueType AS SetKeyType,
       CAST(NULL AS NVARCHAR(50)) AS ResultType
INTO #R
FROM #F ref
JOIN meta.Relationship rel ON rel.ReferenceFieldId = ref.Id AND rel.IsDeleted = 0
JOIN meta.AppTable parent ON parent.Id = rel.ParentTableId
LEFT JOIN #F dk ON dk.Id = rel.DisplayKeyFieldId AND dk.AppTableId = rel.ParentTableId
LEFT JOIN #F sk ON sk.Id = parent.KeyFieldId AND sk.AppTableId = rel.ParentTableId
WHERE ref.TypeCode = 'Reference'
  AND ref.Settings IS NOT NULL
  AND JSON_VALUE(ref.Settings, '$.resultTypeCode') IS NULL;

-- An existing field converted into the Reference keeps its own type: a Formula_{X} as X, a generic Formula as its saved
-- resultType (Text when none), a stored type as itself, and a Number field converted metadata-only (no OriginalTypeCode)
-- as Number.
UPDATE r SET ResultType = COALESCE(fx.ValueType, fg.ValueType,
                                   CASE WHEN r.OriginalTypeCode NOT LIKE 'Formula%' THEN r.OriginalTypeCode END, 'Number')
FROM #R r
LEFT JOIN #FormulaResult fx ON r.OriginalTypeCode LIKE 'Formula[_]%' AND fx.Result = SUBSTRING(r.OriginalTypeCode, 9, 50)
LEFT JOIN #FormulaResult fg ON r.OriginalTypeCode = 'Formula'
      AND fg.Result = COALESCE(CASE WHEN ISJSON(JSON_VALUE(r.Settings, '$.originalSettings')) = 1
                                    THEN JSON_VALUE(JSON_VALUE(r.Settings, '$.originalSettings'), '$.resultType') END, 'Text')
WHERE r.IsExisting = 1;

-- A new Reference field takes the parent key's type: Display Key, else Set Key, else Record ID# (Number).
UPDATE #R SET ResultType = COALESCE(DisplayKeyType, SetKeyType, 'Number')
WHERE IsExisting = 0 OR IsExisting IS NULL;

UPDATE af SET Settings = JSON_MODIFY(af.Settings, '$.resultTypeCode', r.ResultType)
FROM meta.AppField af
JOIN #R r ON r.Id = af.Id
WHERE r.ResultType IS NOT NULL
  AND ISJSON(af.Settings) = 1
  AND JSON_VALUE(af.Settings, '$.resultTypeCode') IS NULL;

DROP TABLE #R;
DROP TABLE #F;
DROP TABLE #FormulaResult;
