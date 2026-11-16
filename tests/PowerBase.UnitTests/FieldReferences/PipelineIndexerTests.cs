using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.UnitTests.FieldReferences;

/// <summary>PowerFlows in the Usage index: which rows a pipeline produces, the table rebuild, and the change-scan that replaces a hook in the save.</summary>
public class PipelineIndexerTests
{
    private const long AppId = 1;
    private const long TableId = 7;
    private const long OtherTableId = 8;

    private readonly IFieldReferenceRepository _references = Substitute.For<IFieldReferenceRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly Guid _tablePublicId = Guid.NewGuid();
    private readonly Guid _otherTablePublicId = Guid.NewGuid();

    public PipelineIndexerTests()
    {
        _fields.ListByTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 1001, Fid = 6, AppTableId = TableId, Name = "Qty", Label = "Qty", TypeCode = "Number" },
            new() { Id = 1002, Fid = 7, AppTableId = TableId, Name = "Price", Label = "Price", TypeCode = "Number" },
        });
        _fields.ListByTableAsync(OtherTableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 2001, Fid = 6, AppTableId = OtherTableId, Name = "Qty", Label = "Qty", TypeCode = "Number" },
        });
        _tables.GetByPublicIdAsync(_tablePublicId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = TableId, PublicId = _tablePublicId, AppId = AppId });
        _tables.GetByPublicIdAsync(_otherTablePublicId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = OtherTableId, PublicId = _otherTablePublicId, AppId = AppId });
        _tables.GetByPublicIdAsync(Arg.Is<Guid>(g => g != _tablePublicId && g != _otherTablePublicId), Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotFoundException("Table", Guid.Empty));
        _tables.GetByIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = TableId, PublicId = _tablePublicId, AppId = AppId });
        _references.ListReportSourcesAsync(TableId, Arg.Any<CancellationToken>()).Returns(new List<Report>());
        _references.ListRelationshipsOfChildTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(new List<RelationshipSource>());
        _references.ListDashboardSourcesAsync(AppId, Arg.Any<CancellationToken>()).Returns(new List<DashboardSource>());
        _references.ListCustomRulesOfTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(new List<CustomRuleSource>());
        _references.ListPipelineSourcesAsync(AppId, Arg.Any<CancellationToken>()).Returns(new List<PipelineSource>());
    }

    private FieldReferenceIndexer NewIndexer() =>
        new(_references, _fields, _tables, Substitute.For<IReportRepository>(), Substitute.For<IFormRepository>(),
            Substitute.For<IFormRuleRepository>(), new FormulaEngine(), NullLogger<FieldReferenceIndexer>.Instance);

    private PipelineSource Pipeline(long id, Guid publicId, params PipelineStepSnapshot[] steps) =>
        new(id, publicId, AppId, "Order flow", steps);

    private PipelineStepSnapshot Step(string refId, string label, string config) =>
        new(refId, label, "action", "update_record", config);

    private static string UpdateQty(Guid table) => "{\"tablePublicId\":\"" + table + "\",\"fieldValues\":{\"fid_6\":\"1\"}}";

    [Fact]
    public async Task A_step_that_maps_a_field_of_a_table_files_a_row_under_that_table()
    {
        var id = Guid.NewGuid();
        _references.GetPipelineSourceAsync(id, Arg.Any<CancellationToken>())
            .Returns(Pipeline(9, id, Step("step_2", "Update stock", UpdateQty(_tablePublicId))));

        await NewIndexer().ReindexPipelineAsync(id);

        await _references.Received(1).ReplaceForSourceAsync(
            FieldReferenceSourceTypes.PowerFlow, 9,
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows =>
                rows.Count == 1
                && rows.Single().SourceTableId == TableId
                && rows.Single().TargetFieldId == 1001
                && rows.Single().Usage == "step:step_2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_same_field_number_on_another_table_is_not_a_use()
    {
        var id = Guid.NewGuid();
        _references.GetPipelineSourceAsync(id, Arg.Any<CancellationToken>())
            .Returns(Pipeline(9, id, Step("step_2", "Update other", UpdateQty(_otherTablePublicId))));

        await NewIndexer().ReindexPipelineAsync(id);

        await _references.Received(1).ReplaceForSourceAsync(
            FieldReferenceSourceTypes.PowerFlow, 9,
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows => rows.Count == 1 && rows.Single().TargetFieldId == 2001),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Every_step_that_uses_a_field_gets_its_own_row()
    {
        var id = Guid.NewGuid();
        _references.GetPipelineSourceAsync(id, Arg.Any<CancellationToken>()).Returns(Pipeline(9, id,
            Step("step_1", "First", UpdateQty(_tablePublicId)),
            Step("step_2", "Second", UpdateQty(_tablePublicId))));

        await NewIndexer().ReindexPipelineAsync(id);

        await _references.Received(1).ReplaceForSourceAsync(
            FieldReferenceSourceTypes.PowerFlow, 9,
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows => rows.Select(r => r.Usage).OrderBy(u => u).SequenceEqual(new[] { "step:step_1", "step:step_2" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_deleted_pipeline_loses_its_rows()
    {
        var id = Guid.NewGuid();
        _references.GetPipelineSourceAsync(id, Arg.Any<CancellationToken>()).Returns((PipelineSource?)null);

        await NewIndexer().ReindexPipelineAsync(id);

        await _references.Received(1).DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.PowerFlow, id, Arg.Any<CancellationToken>());
        await _references.DidNotReceiveWithAnyArgs().ReplaceForSourceAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task A_table_rebuild_puts_back_only_that_tables_pipeline_rows_without_touching_others()
    {
        var id = Guid.NewGuid();
        _references.ListPipelineSourcesAsync(AppId, Arg.Any<CancellationToken>()).Returns(new List<PipelineSource>
        {
            Pipeline(9, id, Step("step_1", "Both", UpdateQty(_tablePublicId)), Step("step_2", "Other", UpdateQty(_otherTablePublicId))),
        });

        await NewIndexer().RebuildTableAsync(TableId);

        await _references.Received().InsertRowsAsync(
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows => rows.Count == 1 && rows.Single().SourceTableId == TableId && rows.Single().Usage == "step:step_1"),
            Arg.Any<CancellationToken>());
        await _references.DidNotReceive().ReplaceForSourceAsync(
            FieldReferenceSourceTypes.PowerFlow, Arg.Any<long>(), Arg.Any<IReadOnlyCollection<FieldReferenceRow>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sync_reindexes_the_changed_pipelines_and_moves_the_watermark()
    {
        var watermark = new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);
        var scanned = watermark.AddMinutes(1);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _references.GetPipelineSyncWatermarkAsync(Arg.Any<CancellationToken>()).Returns(watermark);
        _references.ListPipelinesChangedSinceAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns((new List<Guid> { a, b }, scanned));
        _references.GetPipelineSourceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((PipelineSource?)null);

        var count = await NewIndexer().SyncPipelinesAsync();

        count.Should().Be(2);
        await _references.Received(1).ListPipelinesChangedSinceAsync(watermark.AddMinutes(-5), Arg.Any<CancellationToken>());
        await _references.Received(1).SetPipelineSyncWatermarkAsync(scanned, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_first_sync_looks_at_every_pipeline()
    {
        _references.GetPipelineSyncWatermarkAsync(Arg.Any<CancellationToken>()).Returns((DateTime?)null);
        _references.ListPipelinesChangedSinceAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns((new List<Guid>(), DateTime.UtcNow));

        await NewIndexer().SyncPipelinesAsync();

        await _references.Received(1).ListPipelinesChangedSinceAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_sync_is_logged_and_never_thrown_and_keeps_the_old_watermark()
    {
        _references.GetPipelineSyncWatermarkAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("table missing"));

        var count = await NewIndexer().SyncPipelinesAsync();

        count.Should().Be(0);
        await _references.DidNotReceiveWithAnyArgs().SetPipelineSyncWatermarkAsync(default, default);
    }
}
