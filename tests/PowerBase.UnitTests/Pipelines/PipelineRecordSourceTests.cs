using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>Update / Delete Record and Upload a File naming their record by Record ID (e.g. as the first step), and which
/// first steps a manual or scheduled run can start from.</summary>
public class PipelineRecordSourceTests
{
    private static readonly AppTable Table = new() { Id = 5, PublicId = Guid.NewGuid(), Name = "Clients" };
    private static readonly IReadOnlyList<AppField> Fields = new List<AppField> { new() { Id = 1, Fid = 3, Name = "Record ID#", IsSystem = true, PhysicalColumnName = "Id" } };

    [Theory]
    [InlineData("{\"recordSource\":\"id\"}", true)]
    [InlineData("{\"recordSource\":\"ID\"}", true)]
    [InlineData("{\"targetRecordId\":\"{{steps.ref_a.CreatedRecordPublicId}}\"}", false)]
    [InlineData("{}", false)]
    [InlineData(null, false)]
    [InlineData("not json", false)]
    public void IsRecordId_OnlyForTheRecordIdMode(string? config, bool expected) =>
        PipelineRecordSource.IsRecordId(config).Should().Be(expected);

    [Fact]
    public void TableId_ReadsWhicheverTableKeyTheEditorWrote()
    {
        var id = Guid.NewGuid().ToString();
        PipelineRecordSource.TableId($"{{\"tableLabel\":\"{id}\"}}").Should().Be(id);
        PipelineRecordSource.TableId("{\"tableId\":\"\"}").Should().BeNull();
    }

    [Fact]
    public async Task Resolve_ARecordIdNumber_FindsTheRecordsPublicId()
    {
        var publicId = Guid.NewGuid();
        var records = Substitute.For<IRecordRepository>();
        records.GetRowsByIdsAsync(Table, Fields, Arg.Is<IReadOnlyCollection<long>>(ids => ids.Single() == 7), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [7] = new Dictionary<string, object?> { ["Id"] = 7L, ["PublicId"] = publicId } });

        (await PipelineRecordSource.ResolveAsync(" 7 ", Table, Fields, records, CancellationToken.None)).Should().Be(publicId);
    }

    [Fact]
    public async Task Resolve_APublicId_IsUsedAsIs()
    {
        var publicId = Guid.NewGuid();
        var records = Substitute.For<IRecordRepository>();

        (await PipelineRecordSource.ResolveAsync(publicId.ToString(), Table, Fields, records, CancellationToken.None)).Should().Be(publicId);
        await records.DidNotReceiveWithAnyArgs().GetRowsByIdsAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("abc", "is not a valid Record ID")]
    [InlineData("99", "Record with ID 99 was not found in Table 'Clients'")]
    public async Task Resolve_BadOrMissingRecord_FailsClearly(string value, string message)
    {
        var records = Substitute.For<IRecordRepository>();
        records.GetRowsByIdsAsync(default!, default!, default!, default).ReturnsForAnyArgs(new Dictionary<long, IReadOnlyDictionary<string, object?>>());

        var act = () => PipelineRecordSource.ResolveAsync(value, Table, Fields, records, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*{message}*");
    }

    private static PipelineStep Root(string type, string subtype, string? config = null) =>
        new() { Id = 1, Type = type, Subtype = subtype, ConfigJson = config };

    [Theory]
    [InlineData("query", "search-records", null, true)]
    [InlineData("action", "make-request", null, true)]
    [InlineData("action", "create-record", null, true)]
    [InlineData("action", "call-another-pipeline", null, true)]
    [InlineData("email", "send-email-outlook", null, true)]
    [InlineData("action", "update-record", "{\"recordSource\":\"id\"}", true)]
    [InlineData("action", "delete-record", "{\"recordSource\":\"id\"}", true)]
    [InlineData("file", "upload-file", "{\"recordSource\":\"id\"}", true)]
    [InlineData("action", "update-record", null, true)]
    [InlineData("action", "stop", null, false)]
    [InlineData("condition", "if-else", null, false)]
    [InlineData("loop", "for-each", null, false)]
    [InlineData("trigger", "new-event", null, false)]
    public void IsRunnableRoot_MatchesTheStepsThatCanStartARun(string type, string subtype, string? config, bool expected) =>
        PipelineScheduleEligibility.IsRunnableRoot(Root(type, subtype, config)).Should().Be(expected);

    [Theory]
    [InlineData("email", "send-email-outlook", true)]
    [InlineData("file", "upload-file", true)]
    [InlineData("action", "create-record", true)]
    [InlineData("query", "look-up-record", true)]
    [InlineData("trigger", "new-event", false)]
    public void IsPipelineScheduleable_AcceptsEmailAndUploadFirstSteps(string type, string subtype, bool expected) =>
        PipelineScheduleEligibility.IsPipelineScheduleable(new[] { Root(type, subtype) }).Should().Be(expected);
}
