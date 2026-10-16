-- Form rule actions reference their target by the target's stable PublicId (GUID) instead of only by its
-- numeric row id. A section / column / element added in the designer already has its GUID before the form is
-- saved (the designer sends it and the layout save keeps it), so a rule can point at it without saving first.
-- TargetPagePublicId is new: rules can now target a form Page (show / hide / change label).
-- The numeric TargetElementId / TargetSectionId / TargetBlockId columns are kept as a resolved cache (server-side
-- validation reads them); they are re-derived from the GUIDs whenever a rule or the layout is saved.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'TargetElementPublicId')
    ALTER TABLE meta.FormRuleAction ADD TargetElementPublicId UNIQUEIDENTIFIER NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'TargetSectionPublicId')
    ALTER TABLE meta.FormRuleAction ADD TargetSectionPublicId UNIQUEIDENTIFIER NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'TargetBlockPublicId')
    ALTER TABLE meta.FormRuleAction ADD TargetBlockPublicId UNIQUEIDENTIFIER NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'TargetPagePublicId')
    ALTER TABLE meta.FormRuleAction ADD TargetPagePublicId UNIQUEIDENTIFIER NULL;
GO

-- Backfill from the existing numeric targets.
EXEC('
UPDATE ra SET ra.TargetElementPublicId = el.PublicId
FROM meta.FormRuleAction ra JOIN meta.FormElement el ON el.Id = ra.TargetElementId
WHERE ra.TargetElementPublicId IS NULL;

UPDATE ra SET ra.TargetSectionPublicId = sec.PublicId
FROM meta.FormRuleAction ra JOIN meta.FormSection sec ON sec.Id = ra.TargetSectionId
WHERE ra.TargetSectionPublicId IS NULL;

UPDATE ra SET ra.TargetBlockPublicId = blk.PublicId
FROM meta.FormRuleAction ra JOIN meta.FormSectionBlock blk ON blk.Id = ra.TargetBlockId
WHERE ra.TargetBlockPublicId IS NULL;
');
GO
