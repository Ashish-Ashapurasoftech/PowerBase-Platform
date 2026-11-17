using System.Text.Json;
using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>POST /v1/records/query request parsing (Quickbase "Query for data").</summary>
public class QbRecordsQueryTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static QbQueryRequest Req(string json) => JsonSerializer.Deserialize<QbQueryRequest>(json, Web)!;
    private static readonly IReadOnlyDictionary<long, AppField> Fields = new Dictionary<long, AppField>
    {
        [6] = new() { Fid = 6, Name = "Name", TypeCode = "Text" },
        [7] = new() { Fid = 7, Name = "Age", TypeCode = "Number" },
    };

    [Fact]
    public void Request_QuickbaseBody_Deserializes()
    {
        var r = Req("""{"from":"bwcrtu9k5","select":[3,6,"7"],"where":"{6.EX.'a'}","sortBy":[{"fieldId":6,"order":"DESC"}],"groupBy":[{"fieldId":7,"grouping":"equal-values"}],"options":{"skip":5,"top":10}}""");

        r.From.Should().Be("bwcrtu9k5");
        r.Select.Should().HaveCount(3);
        r.Where.Should().Be("{6.EX.'a'}");
        r.Options!.Skip.Should().Be(5);
        r.Options.Top.Should().Be(10);
        r.GroupBy!.Single().FieldId.Should().Be(7);
    }

    [Fact]
    public void SortOf_GroupByFirstThenSortBy()
    {
        var sort = QbRecordService.SortOf(Req("""{"sortBy":[{"fieldId":6,"order":"DESC"},{"fieldId":7,"order":"ASC"}],"groupBy":[{"fieldId":7}]}"""), Fields);

        sort.Select(s => (s.FieldId, s.Desc)).Should().Equal((7L, false), (6L, true));
    }

    [Theory]
    [InlineData("""{"sortBy":false}""")]
    [InlineData("""{}""")]
    [InlineData("""{"sortBy":null}""")]
    public void SortOf_FalseOrMissing_NoSort(string json) =>
        QbRecordService.SortOf(Req(json), Fields).Should().BeEmpty();

    [Fact]
    public void SortOf_OrderMissing_IsAscending() =>
        QbRecordService.SortOf(Req("""{"sortBy":[{"fieldId":"6"}]}"""), Fields).Single().Desc.Should().BeFalse();

    [Theory]
    [InlineData("""{"sortBy":"6"}""")]
    [InlineData("""{"sortBy":[{"order":"ASC"}]}""")]
    [InlineData("""{"sortBy":[{"fieldId":"abc"}]}""")]
    public void SortOf_Invalid_Is400(string json)
    {
        var act = () => QbRecordService.SortOf(Req(json), Fields);
        act.Should().Throw<QbApiException>().Which.StatusCode.Should().Be(400);
    }
}
