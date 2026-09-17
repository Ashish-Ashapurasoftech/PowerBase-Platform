using System.Data;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Pipelines;

public class CopyRecordsTests
{
    [Fact]
    public async Task CopyRecords_CanBeSavedAsFirstRootActionAndScheduled()
    {
        var config = Config();
        config.AdvancedQueryFields = JsonSerializer.SerializeToElement(Array.Empty<string>());
        var step = new PowerBase.Application.Pipelines.Commands.SavePipelineSteps.SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(), RefId = "copy", Type = "action", Subtype = "copy-records",
            ConfigJson = JsonSerializer.Serialize(config), IsValidated = true
        };
        var validator = new PowerBase.Application.Pipelines.Commands.SavePipelineSteps.SavePipelineStepsCommandValidator();
        var result = await validator.ValidateAsync(new PowerBase.Application.Pipelines.Commands.SavePipelineSteps.SavePipelineStepsCommand(
            Guid.NewGuid(), new() { step }, Array.Empty<byte>()));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.True(PipelineScheduleEligibility.IsPipelineScheduleable(new[] {
            new PipelineStep { Id = 1, Type = "action", Subtype = "copy-records", DisplayOrder = 1 }
        }));
    }

    [Fact]
    public void HandleErrors_WithCopyRecordsAsFirstMonitoredStep_CanBeScheduled()
    {
        var steps = new[]
        {
            new PipelineStep
            {
                Id = 1, Type = "control", Subtype = "handle-errors", DisplayOrder = 0
            },
            new PipelineStep
            {
                Id = 2, ParentStepId = 1, ParentBranch = "children", Type = "action",
                Subtype = "copy-records", DisplayOrder = 0, IsValidated = true
            }
        };

        Assert.True(PipelineScheduleEligibility.IsPipelineScheduleable(steps));
    }

    private static AppField Field(int fid, string name, string type = "Text", bool unique = false) =>
        new() { Id = fid + 100, Fid = fid, Name = name, TypeCode = type, IsUnique = unique };

    [Fact]
    public void Query_PreservesGroupingAndEscapedLiterals()
    {
        var tree = CopyRecordsDefinition.ParseQuery("({6.EX.'O\\'Brien'}OR{'6'.EX.'ગુજરાતી'})AND{7.GTE.'0'}", new[] { Field(6, "Name"), Field(7, "Amount", "Number") });
        var and = tree!.Nodes.Single().Group!;
        Assert.Equal(2, and.Nodes.Count);
        Assert.Equal("O'Brien", and.Nodes[0].Group!.Nodes[0].Group!.Nodes[0].Condition!.Value);
        Assert.Equal("gte", and.Nodes[1].Condition!.Operator);
    }

    [Theory]
    [InlineData("{6.ex.'x'}")]
    [InlineData("{999.EX.'x'}")]
    [InlineData("{6.EX.'unterminated}")]
    [InlineData("{6.EX.'x'} OR 1=1")]
    [InlineData("{6.UNKNOWN.'x'}")]
    public void InvalidQuery_IsNeverAnUnfilteredCopy(string query) =>
        Assert.Throws<ValidationException>(() => CopyRecordsDefinition.ParseQuery(query, new[] { Field(6, "Name") }));

    [Theory]
    [InlineData("{6.WC.'Jo*'}", "wildcard")]
    [InlineData("{6.XWC.'Jo?'}", "notWildcard")]
    [InlineData("{6.GTE.0}", "gte")]
    public void Query_ParsesWildcardAndUnquotedNumbers(string query, string expected)
    {
        var condition = CopyRecordsDefinition.ParseQuery(query, new[] { Field(6, "Value") })!.Nodes[0].Group!.Nodes[0].Condition!;
        Assert.Equal(expected, condition.Operator);
    }

    [Fact]
    public void Query_RecognizesFieldComparisonInsteadOfTreatingItAsText()
    {
        var condition = CopyRecordsDefinition.ParseQuery("{6.EX.'_FID_7'}", new[] { Field(6, "A"), Field(7, "B") })!.Nodes[0].Group!.Nodes[0].Condition!;
        Assert.Equal("field", condition.ValueMode);
        Assert.Equal(7, condition.ValueFieldId);
    }

    [Fact]
    public void MergeMustBeAnEligibleDestinationField()
    {
        var config = Config();
        config.MergeField = "fid_6";
        Assert.Throws<ValidationException>(() => config.ValidateFields(new[] { Field(6, "Source key", unique: true) }, new[] { Field(9, "Destination key", unique: true) }));
        config.MergeField = "fid_9";
        config.ValidateFields(new[] { Field(6, "Source key") }, new[] { Field(9, "Destination key", unique: true) });
    }

    [Fact]
    public void MergeEligibilityUsesMetadataInsteadOfFixedFid()
    {
        var config = Config();
        var primary = Field(42, "Custom primary");
        primary.IsPrimary = true;
        primary.IsSystem = true;
        config.MergeField = "fid_42";
        config.DestinationFields = new() { "fid_42" };
        config.ValidateFields(new[] { Field(6, "Source") }, new[] { primary });
        config.MergeField = "fid_3";
        config.DestinationFields = new() { "fid_3" };
        Assert.Throws<ValidationException>(() => config.ValidateFields(new[] { Field(6, "Source") }, new[] { Field(3, "Ordinary field") }));
        Assert.Equal("", new CopyRecordsDefinition().MergeField);
    }

    [Fact]
    public void MergeAcceptsLegacyIdentityMetadataWithoutChangingSeedData()
    {
        var config = Config();
        var primary = Field(73, "Renamed identity");
        primary.IsSystem = true;
        primary.PhysicalColumnName = "Id";
        config.MergeField = "fid_73";
        config.DestinationFields = new() { "fid_73" };
        config.ValidateFields(new[] { Field(6, "Source") }, new[] { primary });
        primary.PhysicalColumnName = "CreatedOn";
        Assert.Throws<ValidationException>(() => config.ValidateFields(new[] { Field(6, "Source") }, new[] { primary }));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("someoneNoneValue")]
    [InlineData("ગુજરાતી, \"quoted\"\nvalue")]
    [InlineData("")]
    public void TextIsNotCoercedToNull(string value) =>
        Assert.Equal(value, CopyRecordsDefinition.ConvertValue(value, Field(6, "Source"), Field(9, "Destination")));

    [Fact]
    public void NumericToTextPreservesZeroAndTextToNumberUsesCreateRecordParsing()
    {
        Assert.Equal("0", CopyRecordsDefinition.ConvertValue(0, Field(6, "Source", "Number"), Field(9, "Target")));
        Assert.Equal(123m, CopyRecordsDefinition.ConvertValue("123", Field(6, "Source"), Field(9, "Target", "Number")));
        Assert.Throws<ValidationException>(() => CopyRecordsDefinition.ConvertValue("abc", Field(6, "Source"), Field(9, "Target", "Number")));
    }

    private static CopyRecordsDefinition Config() => new()
    {
        SourceTable = Guid.NewGuid().ToString(), DestinationTable = Guid.NewGuid().ToString(),
        SourceFields = new() { "fid_6" }, DestinationFields = new() { "fid_9" }, MergeField = "fid_9"
    };

    [Theory]
    [InlineData("fid_9")]
    [InlineData("Destination")]
    public void DuplicateDestinationMapping_IsRejectedEvenWithDifferentReferences(string duplicate)
    {
        var config = Config();
        config.SourceFields.Add("fid_7");
        config.DestinationFields.Add(duplicate);
        var error = Assert.Throws<ValidationException>(() => config.ValidateFields(
            new[] { Field(6, "Name"), Field(7, "Number", "Number") },
            new[] { Field(9, "Destination", unique: true) }));
        Assert.Contains("mapped more than once", error.Message);
    }

    [Theory]
    [InlineData("Yes")]
    [InlineData("No")]
    public async Task InvalidNumericExport_IsAnErrorWithoutInsertOrUpdate(string setting)
    {
        using var harness = new Harness();
        harness.Config.TerminateOnError = setting;
        harness.SourceFields[0].TypeCode = "Number";
        harness.DestinationField.TypeCode = "Number";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (setting == "Yes")
                Assert.Contains("cannot convert value to Number", (await Assert.ThrowsAsync<PipelineNonRetryableException>(harness.Run)).Message);
            else
            {
                var result = JsonDocument.Parse(await harness.Run()).RootElement;
                Assert.Equal(1, result.GetProperty("ErrorCount").GetInt32());
                Assert.Equal(0, result.GetProperty("InsertedCount").GetInt32());
                Assert.Equal(0, result.GetProperty("UpdatedCount").GetInt32());
            }
        }
        Assert.Empty(harness.Records.ReceivedCalls());
        Assert.Empty(harness.Writes.ReceivedCalls());
    }

    [Fact]
    public void NumericValuesAndActualNull_ArePreserved()
    {
        var source = Field(6, "Source", "Number");
        var destination = Field(9, "Destination", "Number");
        Assert.Equal(12.5m, CopyRecordsDefinition.ConvertValue(JsonSerializer.SerializeToElement(12.5m), source, destination));
        Assert.Equal(0, CopyRecordsDefinition.ConvertValue(0, source, destination));
        Assert.Null(CopyRecordsDefinition.ConvertValue(JsonSerializer.SerializeToElement<object?>(null), source, destination));
    }

    [Fact]
    public void ConversionErrors_UseFieldLabelsInsteadOfInternalNames()
    {
        var source = Field(6, "c_name");
        source.Label = "Name";
        var destination = Field(9, "c_number", "Number");
        destination.Label = "Number";
        var error = Assert.Throws<ValidationException>(() => CopyRecordsDefinition.ConvertValue("abc", source, destination));
        Assert.Contains("'Number'", error.Message);
        Assert.DoesNotContain("c_number", error.Message);
        var config = Config();
        config.SourceFields.Add("fid_7");
        config.DestinationFields.Add("fid_9");
        var duplicate = Assert.Throws<ValidationException>(() => config.ValidateFields(
            new[] { source, Field(7, "other") }, new[] { destination }));
        Assert.Contains("'Number'", duplicate.Message);
        Assert.DoesNotContain("c_number", duplicate.Message);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(250, true)]
    [InlineData(251, false)]
    public void SourceColumnLimit_IsEnforced(int count, bool valid)
    {
        var config = Config();
        config.SourceFields = Enumerable.Range(1, count).Select(i => $"fid_{i}").ToList();
        config.DestinationFields = Enumerable.Range(1, count).Select(i => $"fid_{i + 300}").ToList();
        if (valid) config.ValidateShape();
        else Assert.Throws<ValidationException>(config.ValidateShape);
    }

    [Theory]
    [InlineData("source-table")]
    [InlineData("destination-table")]
    [InlineData("duplicate-source")]
    [InlineData("missing-mapping")]
    [InlineData("blank-mapping")]
    [InlineData("blank-merge")]
    [InlineData("invalid-terminate")]
    [InlineData("legacy-query")]
    public void InvalidConfiguration_IsRejected(string scenario)
    {
        var config = Config();
        switch (scenario)
        {
            case "source-table": config.SourceTable = "invalid"; break;
            case "destination-table": config.DestinationTable = "invalid"; break;
            case "duplicate-source": config.SourceFields.Add("fid_6"); config.DestinationFields.Add("fid_10"); break;
            case "missing-mapping": config.DestinationFields.Clear(); break;
            case "blank-mapping": config.DestinationFields[0] = " "; break;
            case "blank-merge": config.MergeField = " "; break;
            case "invalid-terminate": config.TerminateOnError = "Maybe"; break;
            case "legacy-query": config.AdvancedQueryFields = JsonSerializer.SerializeToElement(new[] { new { expression = "{{a.name}}" } }); break;
        }
        Assert.Throws<ValidationException>(config.ValidateShape);
    }

    [Theory]
    [InlineData("File")]
    [InlineData("Attachment")]
    [InlineData("Formula_Text")]
    public void ReadOnlyOrAttachmentDestination_CannotBeMergeKey(string type)
    {
        Assert.Throws<ValidationException>(() => Config().ValidateFields(
            new[] { Field(6, "Source") }, new[] { Field(9, "Key", type, unique: true) }));
    }

    [Fact]
    public void AttachmentSource_IsRejected()
    {
        Assert.Throws<ValidationException>(() => Config().ValidateFields(
            new[] { Field(6, "File", "File") }, new[] { Field(9, "Key", unique: true) }));
    }

    [Fact]
    public void PrimaryMergeKey_CanBeUnmappedForInsert()
    {
        var key = Field(42, "Key");
        key.IsPrimary = true;
        var config = Config();
        config.MergeField = "fid_42";
        config.ValidateFields(new[] { Field(6, "Source") }, new[] { key, Field(9, "Value") });
    }

    [Fact]
    public void DeletedAndAmbiguousFields_AreRejected()
    {
        var deleted = Field(9, "Key", unique: true);
        deleted.IsDeleted = true;
        Assert.Throws<ValidationException>(() => CopyRecordsDefinition.Field("fid_9", new[] { deleted }));
        Assert.Throws<ValidationException>(() => CopyRecordsDefinition.Field("Key", new[] { Field(9, "Key"), Field(10, "Key") }));
    }

    [Fact]
    public void BlankQueryAndNullValues_ArePreserved()
    {
        Assert.Null(CopyRecordsDefinition.ParseQuery(" ", Array.Empty<AppField>()));
        Assert.Null(CopyRecordsDefinition.ConvertValue(DBNull.Value, Field(6, "Source"), Field(9, "Target")));
        Assert.Equal(false, CopyRecordsDefinition.ConvertValue(false, Field(6, "Source", "Boolean"), Field(9, "Target", "Boolean")));
    }

    [Fact]
    public async Task EmptySnapshot_ProducesZeroCountsWithoutWrites()
    {
        using var harness = new Harness();
        harness.SourceValues.Clear();
        var result = JsonDocument.Parse(await harness.Run()).RootElement;
        Assert.Equal(0, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(0, result.GetProperty("UpdatedCount").GetInt32());
        Assert.Equal(0, result.GetProperty("ErrorCount").GetInt32());
        Assert.Empty(harness.Records.ReceivedCalls());
        Assert.Empty(harness.Writes.ReceivedCalls());
    }

    [Fact]
    public async Task DuplicateDestinationMatches_AreErrorsAndNeverWritten()
    {
        using var harness = new Harness();
        harness.Config.TerminateOnError = "No";
        harness.Records.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<FilterGroup>(), null, null, Arg.Any<CancellationToken>()).Returns(new[] {
                (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["PublicId"] = Guid.NewGuid() },
                new Dictionary<string, object?> { ["PublicId"] = Guid.NewGuid() }
            });
        var result = JsonDocument.Parse(await harness.Run()).RootElement;
        Assert.Equal(1, result.GetProperty("ErrorCount").GetInt32());
        Assert.Equal(0, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(0, result.GetProperty("UpdatedCount").GetInt32());
        Assert.Empty(harness.Writes.ReceivedCalls());
        Assert.DoesNotContain(harness.Records.ReceivedCalls(), c => c.GetMethodInfo().Name == "CreateAsync");
    }

    [Fact]
    public async Task ChangedPrimaryFid_IsUsedForMatchingAndNotWrittenAsIdentity()
    {
        using var harness = new Harness();
        harness.DestinationField.Fid = 42;
        harness.DestinationField.IsPrimary = true;
        harness.DestinationField.IsSystem = true;
        harness.Config.MergeField = "fid_42";
        harness.Config.DestinationFields = new() { "fid_42" };
        var id = Guid.NewGuid();
        harness.Records.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 2,
            Arg.Is<FilterGroup>(f => f.Nodes[0].Condition!.FieldId == 42), null, null, Arg.Any<CancellationToken>())
            .Returns(new[] { (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["PublicId"] = id } });
        var result = JsonDocument.Parse(await harness.Run()).RootElement;
        Assert.Equal(1, result.GetProperty("UpdatedCount").GetInt32());
        await harness.Writes.Received(1).ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), id,
            Arg.Is<IReadOnlyDictionary<long, object?>>(v => !v.ContainsKey(42) && !v.ContainsKey(3)),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction>(), false, null);
    }

    [Fact]
    public async Task Copy_InsertsAndReplayDoesNotReadOrWriteAgain()
    {
        using var harness = new Harness();
        var first = await harness.Run();
        var second = await harness.Run();
        Assert.Equal(first, second);
        Assert.Equal(1, JsonDocument.Parse(first).RootElement.GetProperty("InsertedCount").GetInt32());
        await harness.Records.Received(1).CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>());
        Assert.Single(harness.Search.ReceivedCalls());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnmappedMergeKey_CopiesSelectedColumnsWithoutMatchingAndReplayDoesNotDuplicate(bool primary)
    {
        using var harness = new Harness();
        harness.DestinationField.Fid = 42;
        harness.DestinationField.IsPrimary = primary;
        harness.DestinationField.IsSystem = primary;
        harness.DestinationField.IsUnique = !primary;
        harness.SourceFields.AddRange(new[] { Field(7, "number"), Field(8, "id") });
        harness.DestinationFields.AddRange(new[] { Field(9, "name"), Field(10, "number"), Field(11, "id") });
        harness.Config.SourceFields = new() { "fid_6", "fid_7", "fid_8" };
        harness.Config.DestinationFields = new() { "fid_9", "fid_10", "fid_11" };
        harness.Config.MergeField = "fid_42";
        var first = await harness.Run();
        Assert.Equal(1, JsonDocument.Parse(first).RootElement.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(0, JsonDocument.Parse(first).RootElement.GetProperty("UpdatedCount").GetInt32());
        Assert.Equal(first, await harness.Run());
        await harness.Records.Received(1).CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Is<IReadOnlyDictionary<long, object?>>(v => v.Count == 3 && v.ContainsKey(9) && v.ContainsKey(10) && v.ContainsKey(11) && !v.ContainsKey(42)),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>());
        Assert.DoesNotContain(harness.Records.ReceivedCalls(), c => c.GetMethodInfo().Name == "ListAsync");
        Assert.Empty(harness.Writes.ReceivedCalls());
    }

    [Fact]
    public async Task Copy_UsesDestinationMergeFieldAndUpdatesMatchedRecord()
    {
        using var harness = new Harness();
        var id = Guid.NewGuid();
        harness.Records.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 2,
            Arg.Is<FilterGroup>(f => f.Nodes[0].Condition!.FieldId == 9 && f.Nodes[0].Condition!.Value == "none"),
            null, null, Arg.Any<CancellationToken>()).Returns(new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["PublicId"] = id } });
        var result = JsonDocument.Parse(await harness.Run()).RootElement;
        Assert.Equal(1, result.GetProperty("UpdatedCount").GetInt32());
        await harness.Writes.Received(1).ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), id,
            Arg.Is<IReadOnlyDictionary<long, object?>>(v => v.ContainsKey(9) && !v.ContainsKey(6)), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction>(), false, null);
    }

    [Theory]
    [InlineData("Yes", true)]
    [InlineData("No", false)]
    public async Task UserError_ObeysTerminateSetting(string setting, bool throws)
    {
        using var harness = new Harness();
        harness.Config.TerminateOnError = setting;
        harness.DestinationField.TypeCode = "Number";
        if (throws) await Assert.ThrowsAsync<PipelineNonRetryableException>(harness.Run);
        else Assert.Equal(1, JsonDocument.Parse(await harness.Run()).RootElement.GetProperty("ErrorCount").GetInt32());
        Assert.DoesNotContain(harness.Records.ReceivedCalls(), c => c.GetMethodInfo().Name == "CreateAsync");
    }

    [Fact]
    public async Task InvalidQuery_DoesNotReadSourceOrWriteDestination()
    {
        using var harness = new Harness();
        await Assert.ThrowsAsync<ValidationException>(() => harness.Run("{6.BAD.'x'}"));
        Assert.Empty(harness.Search.ReceivedCalls());
        Assert.Empty(harness.Records.ReceivedCalls());
    }

    [Theory]
    [InlineData("Yes")]
    [InlineData("No")]
    public async Task PartialFailure_CompletesOtherRowsAndReplayDoesNotRepeatWrites(string setting)
    {
        using var harness = new Harness();
        harness.Config.TerminateOnError = setting;
        harness.SourceValues.Clear();
        harness.SourceValues.AddRange(new[] { "bad", "good" });
        harness.Records.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(),
            Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>()).Returns(c =>
            {
                if (Equals(c.Arg<IReadOnlyDictionary<long, object?>>()[9], "bad"))
                    throw CopyRecordsDefinition.Error("Rejected row");
                return Task.FromResult(Guid.NewGuid());
            });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (setting == "Yes") await Assert.ThrowsAsync<PipelineNonRetryableException>(harness.Run);
            else
            {
                var result = JsonDocument.Parse(await harness.Run()).RootElement;
                Assert.Equal(1, result.GetProperty("InsertedCount").GetInt32());
                Assert.Equal(1, result.GetProperty("ErrorCount").GetInt32());
            }
        }
        Assert.Equal(2, harness.Records.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "CreateAsync"));
        Assert.Single(harness.Search.ReceivedCalls());
    }

    [Fact]
    public async Task MultipleSnapshotPages_CopyAllRowsOnce()
    {
        using var harness = new Harness();
        harness.SourceValues.Clear();
        harness.SourceValues.AddRange(Enumerable.Range(0, 251).Select(i => $"key-{i}"));
        var first = await harness.Run();
        Assert.Equal(251, JsonDocument.Parse(first).RootElement.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(first, await harness.Run());
        Assert.Equal(251, harness.Records.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "CreateAsync"));
        Assert.Single(harness.Search.ReceivedCalls());
    }

    [Fact]
    public async Task InterruptedCopy_ResumesSnapshotWithoutRepeatingCommittedRows()
    {
        using var harness = new Harness();
        harness.SourceValues.Clear();
        harness.SourceValues.AddRange(new[] { "first", "second" });
        var interrupt = true;
        harness.Records.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(),
            Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>()).Returns(c =>
            {
                if (interrupt && Equals(c.Arg<IReadOnlyDictionary<long, object?>>()[9], "second"))
                {
                    interrupt = false;
                    throw new InvalidOperationException("Transient database failure");
                }
                return Task.FromResult(Guid.NewGuid());
            });
        await Assert.ThrowsAsync<InvalidOperationException>(harness.Run);
        harness.SourceValues.Clear(); // Retry must use the persisted snapshot.
        var result = JsonDocument.Parse(await harness.Run()).RootElement;
        Assert.Equal(2, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(0, result.GetProperty("ErrorCount").GetInt32());
        Assert.Equal(3, harness.Records.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "CreateAsync"));
        Assert.Single(harness.Search.ReceivedCalls());
    }

    private sealed class Harness : IDisposable
    {
        public CopyRecordsDefinition Config { get; } = CopyRecordsTests.Config();
        public AppField DestinationField { get; } = Field(9, "Destination key", unique: true);
        public IRecordRepository Records { get; } = Substitute.For<IRecordRepository>();
        public IRecordWriteService Writes { get; } = Substitute.For<IRecordWriteService>();
        public IPipelineRecordSearchService Search { get; } = Substitute.For<IPipelineRecordSearchService>();
        public List<string> SourceValues { get; } = new() { "none" };
        public List<AppField> SourceFields { get; } = new() { Field(6, "Source key") };
        public List<AppField> DestinationFields { get; } = new();
        private readonly ServiceProvider provider;
        private readonly Guid step = Guid.NewGuid(), message = Guid.NewGuid();
        public Harness()
        {
            var tables = Substitute.For<IAppTableRepository>();
            var fields = Substitute.For<IAppFieldRepository>();
            var source = new AppTable { Id = 1, AppId = 1, PublicId = Guid.Parse(Config.SourceTable) };
            var destination = new AppTable { Id = 2, AppId = 1, PublicId = Guid.Parse(Config.DestinationTable) };
            tables.GetByPublicIdAsync(source.PublicId, Arg.Any<CancellationToken>()).Returns(source);
            tables.GetByPublicIdAsync(destination.PublicId, Arg.Any<CancellationToken>()).Returns(destination);
            DestinationFields.Add(DestinationField);
            fields.ListByTableAsync(1, Arg.Any<CancellationToken>()).Returns(_ => SourceFields);
            fields.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(_ => DestinationFields);
            var enforcer = Substitute.For<IRolePermissionEnforcer>();
            enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>()).Returns(new TableAccessContext { Unrestricted = true });
            var idempotency = Substitute.For<IPipelineStepIdempotencyRepository>();
            var logs = new Dictionary<string, string>();
            idempotency.GetByExecutionKeyAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<byte[]>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(c => logs.GetValueOrDefault(Convert.ToHexString(c.Arg<byte[]>())));
            idempotency.InsertAsync(Arg.Any<PipelineStepIdempotencyLog>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(c => { var log = c.Arg<PipelineStepIdempotencyLog>(); logs.Add(Convert.ToHexString(log.ExecutionPathHash), log.OutputJson); return Task.CompletedTask; });
            var encryption = Substitute.For<IEncryptionService>();
            encryption.GenerateAndWrapDekAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns("key");
            encryption.EncryptDataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(c => c.ArgAt<string>(0));
            encryption.DecryptDataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(c => c.ArgAt<string>(0));
            Search.ReadCopySnapshotAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>()).Returns(_ => Page());
            Records.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FilterGroup>(), null, null, Arg.Any<CancellationToken>()).Returns(Array.Empty<IReadOnlyDictionary<string, object?>>());
            provider = new ServiceCollection().AddSingleton(tables).AddSingleton(fields).AddSingleton(Records).AddSingleton(Writes).AddSingleton(Search)
                .AddSingleton(Substitute.For<IAppAccessService>()).AddSingleton(enforcer).AddSingleton(Substitute.For<IQueryContext>())
                .AddSingleton(idempotency).AddSingleton(Substitute.For<ITenantUnitOfWork>()).AddSingleton(encryption)
                .AddSingleton(Substitute.For<IUserRepository>()).AddSingleton(Substitute.For<IAuditRepository>())
                .AddSingleton(Substitute.For<IPipelineTriggerInterceptor>()).AddSingleton<FormulaEngine>().BuildServiceProvider();
        }
        private async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Page()
        {
            await Task.CompletedTask;
            foreach (var page in SourceValues.Chunk(250))
                yield return page.Select(value => {
                    var row = SourceFields.ToDictionary(f => PhysicalNaming.GetPhysicalColumnName(f), f => (object?)value);
                    row["PublicId"] = Guid.NewGuid();
                    return (IReadOnlyDictionary<string, object?>)row;
                }).ToList();
        }
        public Task<string> Run() => Run("");
        public Task<string> Run(string query) => new CopyRecordsExecutor(provider).ExecuteAsync(Config, query, step, message, "root", default);
        public void Dispose() => provider.Dispose();
    }
}
