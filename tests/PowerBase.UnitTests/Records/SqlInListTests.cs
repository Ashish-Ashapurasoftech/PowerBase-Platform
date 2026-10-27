using System.Text.Json;
using Dapper;
using FluentAssertions;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.UnitTests.Records;

/// <summary>The <c>IN (...)</c> the relationship reads (lookups' parent rows, summaries' children) and user-name reads filter
/// on. Dapper turns a list into one parameter per item and SQL Server refuses more than 2,100, so a long list of Ids
/// becomes one JSON array.</summary>
public class SqlInListTests
{
    [Fact]
    public void ShortList_StaysADapperList_AsBefore()
    {
        var parameters = new DynamicParameters();
        IReadOnlyCollection<object> keys = [1L, 2L, 3L];

        var sql = SqlInList.Predicate("f_10", "parentKeyValues", keys, parameters);

        sql.Should().Be("f_10 IN @parentKeyValues");
        parameters.Get<IReadOnlyCollection<object>>("parentKeyValues").Should().BeSameAs(keys);
    }

    [Fact]
    public void ListAtTheLimit_StaysADapperList()
    {
        var parameters = new DynamicParameters();
        var ids = Enumerable.Range(1, SqlInList.MaxParameters).Select(i => (long)i).ToList();

        SqlInList.Predicate("Id", "ids", ids, parameters).Should().Be("Id IN @ids");
    }

    [Fact]
    public void LongListOfIds_IsOneJsonParameter_ReadWithOpenJson()
    {
        var parameters = new DynamicParameters();
        var ids = Enumerable.Range(1, 50_000).Select(i => (long)i).ToList();

        var sql = SqlInList.Predicate("Id", "ids", ids, parameters);

        sql.Should().Be("Id IN (SELECT k FROM OPENJSON(@ids) WITH (k BIGINT '$'))");
        parameters.ParameterNames.Should().ContainSingle();
        JsonSerializer.Deserialize<long[]>(parameters.Get<string>("ids")).Should().Equal(ids);
    }

    [Fact]
    public void LongListOfBoxedKeys_WholeDecimalsIncluded_IsOneJsonParameter()
    {
        // A reference converted from a Number field is a DECIMAL column: its keys can come back as 42.0000m.
        var parameters = new DynamicParameters();
        var keys = Enumerable.Range(1, 3_000).Select(i => i % 2 == 0 ? (object)(long)i : (decimal)i).ToList();

        var sql = SqlInList.Predicate("f_10", "parentKeyValues", keys, parameters);

        sql.Should().StartWith("f_10 IN (SELECT k FROM OPENJSON(@parentKeyValues)");
        JsonSerializer.Deserialize<long[]>(parameters.Get<string>("parentKeyValues")).Should().Equal(Enumerable.Range(1, 3_000).Select(i => (long)i));
    }

    [Theory]
    [InlineData("text")]
    [InlineData(1.5)]
    public void LongListWithAKeyThatIsNoWholeNumber_StaysADapperList(object odd)
    {
        var parameters = new DynamicParameters();
        var keys = Enumerable.Range(1, 3_000).Select(i => (object)(long)i).Append(odd is double d ? (decimal)d : odd).ToList();

        SqlInList.Predicate("f_10", "parentKeyValues", keys, parameters).Should().Be("f_10 IN @parentKeyValues");
    }
}
