-- Migration 015: Backfill missing FormSectionBlock rows for sections that have
-- FormElement rows with NULL FormSectionBlockId (created by old CreateTableCommandHandler
-- which wrote elements without blocks). Creates one default block per section and links
-- all orphaned elements to it.

DECLARE @formId    BIGINT;
DECLARE @sectionId BIGINT;
DECLARE @blockId   BIGINT;

DECLARE sectionCur CURSOR LOCAL FAST_FORWARD FOR
    SELECT DISTINCT fe.FormSectionId
    FROM meta.FormElement fe
    WHERE fe.FormSectionBlockId IS NULL;

OPEN sectionCur;
FETCH NEXT FROM sectionCur INTO @sectionId;

WHILE @@FETCH_STATUS = 0
BEGIN
    -- Only act if the section has no blocks yet
    IF NOT EXISTS (SELECT 1 FROM meta.FormSectionBlock WHERE FormSectionId = @sectionId)
    BEGIN
        INSERT INTO meta.FormSectionBlock (FormSectionId, Heading, BackgroundColor, Width, DisplayOrder)
        VALUES (@sectionId, NULL, NULL, 1, 1);

        SET @blockId = SCOPE_IDENTITY();

        UPDATE meta.FormElement
        SET FormSectionBlockId = @blockId
        WHERE FormSectionId = @sectionId
          AND FormSectionBlockId IS NULL;
    END

    FETCH NEXT FROM sectionCur INTO @sectionId;
END

CLOSE sectionCur;
DEALLOCATE sectionCur;
