using Dapper;
using FluentAssertions;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.UnitTests.Records;

/// <summary>The SQL a report read gives a Lookup it was told to read in SQL: the parent column, through the row's reference —
/// and nothing for any other calculated field, as always.</summary>
public class ReportLookupSqlTests
{
    private const string LookupOfParentName = """{"relationshipId":1,"referenceFid":10,"sourceTableId":99,"sourceFid":6,"sourceTypeCode":"Text"}""";

    private static readonly Dictionary<long, AppField> Fields = new()
    {
        [10] = new AppField { Id = 10, Fid = 10, Name = "Project", TypeCode = "Reference" },
        [11] = new AppField { Id = 11, Fid = 11, Name = "Project - Name", TypeCode = "Lookup", Settings = LookupOfParentName },
        [12] = new AppField { Id = 12, Fid = 12, Name = "Hours", TypeCode = "Number" },
        [13] = new AppField { Id = 13, Fid = 13, Name = "Double", TypeCode = "Formula_Number" },
    };

    private const string ParentName = "(SELECT lk.f_6 FROM data.t_99 lk WHERE lk.Id = t.f_10 AND lk.IsDeleted = 0)";

    private static FilterGroup Where(long fid, string op, string value) =>
        new() { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fid, Operator = op, Value = value } }] };

    [Fact]
    public void LookupReadInSql_IsFilteredOnTheParentColumn()
    {
        var parameters = new DynamicParameters();

        var sql = RecordRepository.BuildReportFilterWhere(Where(11, "eq", "Apollo"), parameters, Fields, new HashSet<long> { 11 });

        sql.Should().Contain(ParentName);
        parameters.ParameterNames.Should().ContainSingle();
    }

    [Fact]
    public void WithoutSqlLookups_ALookupConditionIsSkipped_AsAlways()
    {
        RecordRepository.BuildReportFilterWhere(Where(11, "eq", "Apollo"), new DynamicParameters(), Fields, null).Should().BeEmpty();
        RecordRepository.BuildReportFilterWhere(Where(11, "eq", "Apollo"), new DynamicParameters(), Fields, new HashSet<long> { 99 }).Should().BeEmpty();
    }

    [Fact]
    public void WithoutSqlLookups_TheSqlIsExactlyWhatItWas()
    {
        var plain = Where(12, "gt", "5");

        RecordRepository.BuildReportFilterWhere(plain, new DynamicParameters(), Fields, null)
            .Should().Be(RecordRepository.BuildFilterTreeWhere(plain, new DynamicParameters(), Fields));
    }

    [Fact]
    public void LookupReadInSql_IsSortedByTheParentColumn_OtherCalculatedSortsAreLeftToMemory()
    {
        var sorts = new List<SortSpec> { new() { FieldId = 11, Desc = true }, new() { FieldId = 13 }, new() { FieldId = 12 } };

        RecordRepository.BuildReportOrderBy(sorts, Fields, new HashSet<long> { 11 }).Should().Be($"{ParentName} DESC, f_12 ASC");
        RecordRepository.BuildReportOrderBy(sorts, Fields, null).Should().Be("f_12 ASC");
        RecordRepository.BuildReportOrderBy([new() { FieldId = 13 }], Fields, null).Should().Be("Id");
    }
}
