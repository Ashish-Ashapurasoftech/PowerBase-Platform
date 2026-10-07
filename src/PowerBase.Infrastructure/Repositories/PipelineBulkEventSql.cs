namespace PowerBase.Infrastructure.Repositories;

/// <summary>SQL shared by every writer of <c>meta.PipelineBulkEventRecord</c>.</summary>
public static class PipelineBulkEventSql
{
    public const string Insert = """
        INSERT INTO meta.PipelineBulkEventRecord (BulkEventId, Ordinal, RecordPublicId, EventType, BeforeValuesJson, AfterValuesJson, ChangedFieldsJson, Processed, CreatedOn)
        VALUES (@BulkEventId, @Ordinal, @RecordPublicId, @EventType, @BeforeValuesJson, @AfterValuesJson, @ChangedFieldsJson, @Processed, @CreatedOn)
        """;
}
