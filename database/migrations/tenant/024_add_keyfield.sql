-- Set Key feature: a table can designate an eligible field as its unique "key" field,
-- replacing the default Record ID# for relationship references. NULL = Record ID# (default,
-- backward compatible — every existing table is unaffected until a user explicitly opts in).
ALTER TABLE meta.AppTable
ADD KeyFieldId BIGINT NULL;

ALTER TABLE meta.AppTable
ADD CONSTRAINT FK_AppTable_AppField_KeyField FOREIGN KEY (KeyFieldId) REFERENCES meta.AppField (Id);
GO
