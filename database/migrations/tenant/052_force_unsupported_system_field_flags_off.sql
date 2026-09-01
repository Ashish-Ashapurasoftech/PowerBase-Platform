-- System fields (Record ID#, Date Created, Date Modified, Record Owner, Last Modified By) now
-- only expose Searchable/Reportable as togglable Advanced settings on the Field Detail page —
-- Required/Unique/Sortable/Filterable/Auditable/Encrypted are forced off server-side on every
-- future update (see PowerBase.Application.Fields.Commands.UpdateField.UpdateFieldCommandHandler).
-- This flips existing tenants' already-seeded system fields (Record ID#'s IsSortable=1, Date
-- Created/Modified's IsSortable=1/IsFilterable=1 — see AppSeeder.CreateTableWithDefaultsAsync) to
-- match immediately, rather than only whenever each one next happens to be saved.
UPDATE meta.AppField
SET IsRequired = 0, IsUnique = 0, IsSortable = 0, IsFilterable = 0, IsAuditable = 0, IsEncrypted = 0
WHERE IsSystem = 1 AND IsDeleted = 0;
GO
