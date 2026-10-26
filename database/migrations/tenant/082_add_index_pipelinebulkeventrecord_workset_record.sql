-- Lets a streamed Search Records step answered by Azure AI Search tell which records it already staged
-- (PipelineRepository.GetStagedSearchWorksetRecordsAsync) without reading the whole workset.
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PipelineBulkEventRecord_SearchWorksetRecord' AND object_id = OBJECT_ID('meta.PipelineBulkEventRecord'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_PipelineBulkEventRecord_SearchWorksetRecord
        ON meta.PipelineBulkEventRecord(SearchWorksetId, RecordPublicId)
        WHERE SearchWorksetId IS NOT NULL;
END
GO
