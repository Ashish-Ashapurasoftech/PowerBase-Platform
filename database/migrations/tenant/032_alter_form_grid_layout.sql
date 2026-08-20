-- Tenant DB: Phase 8 grid-snap form canvas.
--
-- Adds 12-column grid coordinates + per-element/zone/section styling to the
-- existing meta.FormSection / FormSectionBlock / FormElement tables, and a
-- new meta.FormPage table for multi-page (tabs/steps) forms. All new columns
-- are nullable or defaulted so every existing row and the current save path
-- (SaveFormLayoutRequest without any of these fields) stay valid mid-migration.
-- New forms populate them; existing forms are left NULL and derive their grid
-- placement client-side on first open (form-grid-engine.ts: deriveGridFromLegacy).
--
-- Zone == FormSectionBlock (the reference design's "zone" is our existing
-- block with ColStart/ColSpan added) — this keeps FormRuleAction.TargetBlockId
-- resolvable across the rebuild; no rule ever needs remapping.

-- ── meta.FormPage ────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'FormPage')
BEGIN
    CREATE TABLE meta.FormPage (
        Id           BIGINT           NOT NULL IDENTITY(1,1),
        PublicId     UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        FormId       BIGINT           NOT NULL,
        Heading      NVARCHAR(200)    NOT NULL DEFAULT 'Page',
        DisplayOrder INT              NOT NULL DEFAULT 0,
        CONSTRAINT PK_FormPage PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_FormPage_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_FormPage_Form FOREIGN KEY (FormId) REFERENCES meta.Form(Id) ON DELETE CASCADE
    );
    CREATE NONCLUSTERED INDEX IX_FormPage_FormId ON meta.FormPage(FormId);
END
GO

-- ── meta.Form: page-nav mode + per-form theme override ──────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.Form') AND name = 'PageNavMode')
BEGIN
    ALTER TABLE meta.Form ADD PageNavMode VARCHAR(10) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.Form') AND name = 'AlwaysTabsOnView')
BEGIN
    ALTER TABLE meta.Form ADD AlwaysTabsOnView BIT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.Form') AND name = 'ThemeJson')
BEGIN
    -- NULL = inherit the app's Branding tokens (3-tier precedence, same
    -- pattern as GridPreferencesService's report/table overrides).
    ALTER TABLE meta.Form ADD ThemeJson NVARCHAR(MAX) NULL;
END
GO

-- ── meta.FormSection: grid cols, page/pin, background, border, dividers ─────

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSection') AND name = 'GridCols')
BEGIN
    ALTER TABLE meta.FormSection ADD GridCols TINYINT NOT NULL DEFAULT 12;
END
GO

-- Column, FK, and index are guarded independently (not as one block behind a
-- single IF) so a prior partial failure — e.g. the column landing but the FK
-- erroring out, since a plain batch has no implicit transaction — heals
-- itself on re-run instead of being permanently skipped because the column
-- guard alone now reads as "already done".
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSection') AND name = 'FormPageId')
BEGIN
    ALTER TABLE meta.FormSection ADD FormPageId BIGINT NULL;
END
GO

-- ON DELETE NO ACTION, not SET NULL/CASCADE: meta.FormSection already cascades
-- from meta.Form (FK_FormSection_Form), and SQL Server refuses a second
-- cascading path (SET NULL counts as one) to the same table from FormSection
-- by way of FormPage -> Form. The application's own delete order already
-- makes this safe without a cascade: SaveLayoutAsync deletes FormSection
-- (which cascades to FormElement) BEFORE it deletes FormPage, in the same
-- transaction, so no section/element row can ever still reference a page
-- that's about to be removed.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_FormSection_FormPage')
BEGIN
    ALTER TABLE meta.FormSection ADD CONSTRAINT FK_FormSection_FormPage FOREIGN KEY (FormPageId) REFERENCES meta.FormPage(Id) ON DELETE NO ACTION;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON t.object_id = i.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'FormSection' AND i.name = 'IX_FormSection_FormPageId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_FormSection_FormPageId ON meta.FormSection(FormPageId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSection') AND name = 'IsPinned')
BEGIN
    ALTER TABLE meta.FormSection ADD IsPinned BIT NOT NULL DEFAULT 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSection') AND name = 'BackgroundColor')
BEGIN
    ALTER TABLE meta.FormSection ADD BackgroundColor NVARCHAR(9) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSection') AND name = 'BackgroundType')
BEGIN
    ALTER TABLE meta.FormSection ADD BackgroundType VARCHAR(10) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSection') AND name = 'BackgroundImage')
BEGIN
    ALTER TABLE meta.FormSection ADD BackgroundImage NVARCHAR(MAX) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSection') AND name = 'BorderColor')
BEGIN
    ALTER TABLE meta.FormSection ADD BorderColor NVARCHAR(9) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSection') AND name = 'BorderWidth')
BEGIN
    ALTER TABLE meta.FormSection ADD BorderWidth INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSection') AND name = 'ShowDividers')
BEGIN
    ALTER TABLE meta.FormSection ADD ShowDividers BIT NOT NULL DEFAULT 1;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSection') AND name = 'DividerColor')
BEGIN
    ALTER TABLE meta.FormSection ADD DividerColor NVARCHAR(9) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSection') AND name = 'DividerWidthPx')
BEGIN
    ALTER TABLE meta.FormSection ADD DividerWidthPx INT NULL;
END
GO

-- ── meta.FormSectionBlock: zone grid span + background/divider overrides ───

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSectionBlock') AND name = 'ColStart')
BEGIN
    ALTER TABLE meta.FormSectionBlock ADD ColStart INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSectionBlock') AND name = 'ColSpan')
BEGIN
    ALTER TABLE meta.FormSectionBlock ADD ColSpan INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSectionBlock') AND name = 'BackgroundType')
BEGIN
    ALTER TABLE meta.FormSectionBlock ADD BackgroundType VARCHAR(10) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSectionBlock') AND name = 'BackgroundImage')
BEGIN
    ALTER TABLE meta.FormSectionBlock ADD BackgroundImage NVARCHAR(MAX) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSectionBlock') AND name = 'DividerMode')
BEGIN
    -- 'inherit' (default) | 'show' | 'hide' — resolved against the owning
    -- section's ShowDividers by the client (effectiveDividerShown()).
    ALTER TABLE meta.FormSectionBlock ADD DividerMode VARCHAR(10) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSectionBlock') AND name = 'DividerColor')
BEGIN
    ALTER TABLE meta.FormSectionBlock ADD DividerColor NVARCHAR(9) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormSectionBlock') AND name = 'DividerWidthPx')
BEGIN
    ALTER TABLE meta.FormSectionBlock ADD DividerWidthPx INT NULL;
END
GO

-- ── meta.FormElement: grid box, group/clone/page links, styling ────────────

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'ColStart')
BEGIN
    ALTER TABLE meta.FormElement ADD ColStart INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'RowStart')
BEGIN
    ALTER TABLE meta.FormElement ADD RowStart INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'ColSpan')
BEGIN
    ALTER TABLE meta.FormElement ADD ColSpan INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'RowSpan')
BEGIN
    ALTER TABLE meta.FormElement ADD RowSpan INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'GroupId')
BEGIN
    -- Row-group membership (layoutGroupInZone) — elements sharing a GroupId
    -- lay out side-by-side within their zone.
    ALTER TABLE meta.FormElement ADD GroupId UNIQUEIDENTIFIER NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'CloneGroupId')
BEGIN
    -- Elements sharing a CloneGroupId are linked copies across pages.
    ALTER TABLE meta.FormElement ADD CloneGroupId UNIQUEIDENTIFIER NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'FormPageId')
BEGIN
    ALTER TABLE meta.FormElement ADD FormPageId BIGINT NULL;
END
GO

-- Same reasoning as FK_FormSection_FormPage above: meta.FormElement already
-- cascades from meta.FormSection, so a second cascading path via FormPage
-- would hit the same SQL Server restriction. NO ACTION is safe here for the
-- identical reason — elements are deleted (via the FormSection cascade)
-- before pages, in the same SaveLayoutAsync transaction.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_FormElement_FormPage')
BEGIN
    ALTER TABLE meta.FormElement ADD CONSTRAINT FK_FormElement_FormPage FOREIGN KEY (FormPageId) REFERENCES meta.FormPage(Id) ON DELETE NO ACTION;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON t.object_id = i.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'FormElement' AND i.name = 'IX_FormElement_FormPageId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_FormElement_FormPageId ON meta.FormElement(FormPageId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'TextStyle')
BEGIN
    -- 'body' | 'h1' | 'h2' | 'h3' | 'eyebrow' — StaticText elements only.
    ALTER TABLE meta.FormElement ADD TextStyle VARCHAR(20) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'BackgroundColor')
BEGIN
    ALTER TABLE meta.FormElement ADD BackgroundColor NVARCHAR(9) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'BorderColor')
BEGIN
    ALTER TABLE meta.FormElement ADD BorderColor NVARCHAR(9) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'BorderWidth')
BEGIN
    ALTER TABLE meta.FormElement ADD BorderWidth INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'ContentWidthMode')
BEGIN
    -- 'auto' | 'custom'
    ALTER TABLE meta.FormElement ADD ContentWidthMode VARCHAR(10) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'ContentWidthValue')
BEGIN
    ALTER TABLE meta.FormElement ADD ContentWidthValue INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'ContentWidthUnit')
BEGIN
    -- 'px' | '%'
    ALTER TABLE meta.FormElement ADD ContentWidthUnit VARCHAR(4) NULL;
END
GO

-- Widen ElementType to admit 'Spacer' — was VARCHAR(30), already wide enough
-- for the longest new value, but the check is here in case a future value
-- ever needs more room. No-op today; kept for documentation/CI-grep purposes.
