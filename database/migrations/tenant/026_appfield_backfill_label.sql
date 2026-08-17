-- Backfill: AppField.Label becomes the user-facing display/edit value going forward (AppField.Name
-- becomes an auto-generated, immutable stable identifier — see FieldNaming/IFieldNameResolver).
-- Copy the existing display value (historically stored in Name) into Label wherever Label is still
-- blank, so nothing displays empty after the application-code switch to Label.
--
-- This does NOT regenerate Name — that requires the C_/S_ camelCase slug logic, which lives in C#
-- (PowerBase.Domain.Constants.FieldNaming) and can't reasonably be duplicated in T-SQL. Run the
-- PowerBase.Migrator name-backfill step after this script to regenerate Name for existing rows.
UPDATE meta.AppField
SET Label = Name
WHERE (Label IS NULL OR LTRIM(RTRIM(Label)) = '');
GO
