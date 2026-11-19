-- Migration 097: a Reference made as a new field is shown as the type of the parent key it shows
-- (ReferenceSettings.ResultTypeCode, see FieldValueTypes.ForNewReference). Until now that type was saved only when the
-- relationship was created, so a Set Key / Display Key change afterwards left it stale ("Number (Reference)" while the
-- reference now shows Department Name). The server now saves it again on every key change
-- (ReferenceResultTypeRefresher); this repairs the ones that went stale before. References converted from an existing
-- field keep their own type and are not touched. Rewrites only a value that differs from the current key's type.
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

-- Each live new-field Reference with the type of the key its parent shows now: the relationship's Display Key, else the
-- parent table's Set Key, else Record ID# (Number).
SELECT ref.Id, COALESCE(dk.ValueType, sk.ValueType, 'Number') AS KeyType
INTO #R
FROM #F ref
JOIN meta.Relationship rel ON rel.ReferenceFieldId = ref.Id AND rel.IsDeleted = 0
JOIN meta.AppTable parent ON parent.Id = rel.ParentTableId
LEFT JOIN #F dk ON dk.Id = rel.DisplayKeyFieldId AND dk.AppTableId = rel.ParentTableId
LEFT JOIN #F sk ON sk.Id = parent.KeyFieldId AND sk.AppTableId = rel.ParentTableId
WHERE ref.TypeCode = 'Reference'
  AND ref.Settings IS NOT NULL
  AND ISNULL(rel.ReferenceFieldIsExisting, 0) = 0;

UPDATE af SET Settings = JSON_MODIFY(af.Settings, '$.resultTypeCode', r.KeyType)
FROM meta.AppField af
JOIN #R r ON r.Id = af.Id
WHERE ISJSON(af.Settings) = 1
  AND ISNULL(JSON_VALUE(af.Settings, '$.resultTypeCode'), N'') <> r.KeyType;

DROP TABLE #R;
DROP TABLE #F;
DROP TABLE #FormulaResult;
