-- Migration 024: Introduce FormSectionBlock for independent column-block layout
-- Each FormSection contains 1-5 side-by-side FormSectionBlocks.
-- Each FormSectionBlock is one column with its own heading, background color, width,
-- and an independent vertical stack of FormElements.

-- ── 1. Create meta.FormSectionBlock ─────────────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.TABLES
    WHERE TABLE_SCHEMA = 'meta' AND TABLE_NAME = 'FormSectionBlock'
)
BEGIN
    CREATE TABLE meta.FormSectionBlock (
        Id              BIGINT IDENTITY(1,1) NOT NULL,
        PublicId        UNIQUEIDENTIFIER NOT NULL
                            CONSTRAINT DF_FormSectionBlock_PublicId DEFAULT NEWSEQUENTIALID(),
        TenantId        BIGINT NOT NULL,
        FormSectionId   BIGINT NOT NULL,
        Heading         NVARCHAR(200) NULL,
        BackgroundColor NVARCHAR(9)   NULL,   -- e.g. #F5F5F5
        Width           INT           NULL,   -- CSS fr weight; NULL = 1
        DisplayOrder    INT           NOT NULL,

        CONSTRAINT PK_FormSectionBlock PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UQ_FormSectionBlock_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_FormSectionBlock_FormSection
            FOREIGN KEY (FormSectionId)
            REFERENCES meta.FormSection (Id)
            ON DELETE CASCADE
    );

    -- EXEC: avoids batch-level parse resolving the index columns before the table exists
    EXEC('CREATE NONCLUSTERED INDEX IX_FormSectionBlock_FormSectionId
              ON meta.FormSectionBlock (FormSectionId)');
END;

-- ── 2. Add FormSectionBlockId to meta.FormElement ───────────────────────────
-- Must be NO ACTION (not CASCADE) to avoid multiple cascade paths:
-- FormElement already cascades from FormSection via FormSectionId.
IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'meta' AND TABLE_NAME = 'FormElement'
      AND COLUMN_NAME = 'FormSectionBlockId'
)
BEGIN
    -- Step 1: add the nullable column
    ALTER TABLE meta.FormElement
        ADD FormSectionBlockId BIGINT NULL;

    -- Step 2: add the FK constraint separately (safer across all SQL Server versions)
    ALTER TABLE meta.FormElement
        ADD CONSTRAINT FK_FormElement_FormSectionBlock
            FOREIGN KEY (FormSectionBlockId)
            REFERENCES meta.FormSectionBlock (Id)
            ON DELETE NO ACTION;

    -- Step 3: EXEC so the filtered index compiles at runtime, not at batch-parse time,
    -- which would fail because FormSectionBlockId didn't exist when the batch was parsed.
    EXEC('CREATE NONCLUSTERED INDEX IX_FormElement_FormSectionBlockId
              ON meta.FormElement (FormSectionBlockId)
              WHERE FormSectionBlockId IS NOT NULL');
END;
