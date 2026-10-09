-- A Search Records step that reads its matches from Azure AI Search resumes from an AI cursor (the upper bound of the last
-- id range it read), not from LastRecordId. DiscoverySource records which source the workset is being filled from so a
-- retry keeps using it: switching source half way would stage records twice or skip some.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.PipelineSearchWorkset') AND name = 'DiscoverySource')
    ALTER TABLE meta.PipelineSearchWorkset ADD DiscoverySource VARCHAR(10) NOT NULL
        CONSTRAINT DF_PipelineSearchWorkset_DiscoverySource DEFAULT 'Sql'
        CONSTRAINT CK_PipelineSearchWorkset_DiscoverySource CHECK (DiscoverySource IN ('Sql', 'Ai'));
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.PipelineSearchWorkset') AND name = 'LastSearchCursor')
    ALTER TABLE meta.PipelineSearchWorkset ADD LastSearchCursor VARCHAR(64) NULL;
GO
