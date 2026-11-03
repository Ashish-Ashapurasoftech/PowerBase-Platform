using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Relationships;

public class LookupResultTypeRepairTests
{
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();

    private static AppField Parent(long id, int fid, string typeCode, long tableId = 349) => new()
    {
        Id = id, PublicId = Guid.NewGuid(), AppTableId = tableId, Fid = fid, TypeCode = typeCode, Name = $"p{fid}", Label = $"p{fid}",
    };

    private static AppField LookupOf(long id, int fid, int sourceFid, string sourceType, string resultType, long sourceTableId = 349) => new()
    {
        Id = id, PublicId = Guid.NewGuid(), AppTableId = 5, Fid = fid, TypeCode = "Lookup", Name = $"l{fid}", Label = $"l{fid}",
        Settings = $$"""{"relationshipId":1,"referenceFid":8,"sourceTableId":{{sourceTableId}},"sourceFid":{{sourceFid}},"sourceTypeCode":"{{sourceType}}","resultTypeCode":"{{resultType}}","sourceSubField":null}""",
    };

    [Fact]
    public async Task StaleResultType_IsCorrectedToTheSourcesCurrentType()
    {
        // The parent's Start Date became a Number; the lookup still says it shows a Date.
        var lookup = LookupOf(1, 10, sourceFid: 8, sourceType: "Number", resultType: "Date");
        _fieldRepo.ListByTableAsync(349, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Parent(2, 8, "Number") });

        var repaired = await new LookupResultTypeRepairService(_fieldRepo).RepairTableAsync([lookup]);

        repaired.Should().Be(1);
        await _fieldRepo.Received(1).UpdateSettingsAsync(1, Arg.Is<string>(s => s.Contains("\"resultTypeCode\":\"Number\"")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResultTypeAlreadyRight_IsLeftAlone()
    {
        var lookup = LookupOf(1, 10, sourceFid: 8, sourceType: "Date", resultType: "Date");
        _fieldRepo.ListByTableAsync(349, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Parent(2, 8, "Date") });

        var repaired = await new LookupResultTypeRepairService(_fieldRepo).RepairTableAsync([lookup]);

        repaired.Should().Be(0);
        await _fieldRepo.DidNotReceive().UpdateSettingsAsync(Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LookupOfAFormula_TakesTheFormulasResultType()
    {
        var lookup = LookupOf(1, 10, sourceFid: 12, sourceType: "Formula_Duration", resultType: "Text");
        _fieldRepo.ListByTableAsync(349, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Parent(2, 12, "Formula_Duration") });

        var repaired = await new LookupResultTypeRepairService(_fieldRepo).RepairTableAsync([lookup]);

        repaired.Should().Be(1);
        await _fieldRepo.Received(1).UpdateSettingsAsync(1, Arg.Is<string>(s => s.Contains("\"resultTypeCode\":\"Duration\"")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SourceFieldGone_IsLeftAlone()
    {
        var lookup = LookupOf(1, 10, sourceFid: 99, sourceType: "Number", resultType: "Date");
        _fieldRepo.ListByTableAsync(349, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Parent(2, 8, "Number") });

        var repaired = await new LookupResultTypeRepairService(_fieldRepo).RepairTableAsync([lookup]);

        repaired.Should().Be(0);
    }

    [Fact]
    public async Task RefreshLookupsOf_UpdatesLookupsOfTheChangedField_AndTheirLookups()
    {
        var source = Parent(2, 8, "Number");
        var lookup = LookupOf(1, 10, sourceFid: 8, sourceType: "Number", resultType: "Date");
        var relRepo = Substitute.For<IRelationshipRepository>();
        relRepo.ListByParentTableAsync(349, Arg.Any<CancellationToken>()).Returns(new List<Relationship> { new() { ChildTableId = 5 } });
        _fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(new List<AppField> { lookup });

        var updated = await FieldValueTypes.RefreshLookupsOfAsync(source, "Number", relRepo, _fieldRepo, CancellationToken.None);

        updated.Should().Be(1);
        await _fieldRepo.Received(1).UpdateSettingsAsync(1, Arg.Is<string>(s => s.Contains("\"resultTypeCode\":\"Number\"")), Arg.Any<CancellationToken>());
    }
}
