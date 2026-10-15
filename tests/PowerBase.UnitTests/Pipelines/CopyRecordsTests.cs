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
    public void TextTrimsEdgesAndPreservesInternalWhitespace()
    {
        var value = CopyRecordsDefinition.ConvertValue(
            " \t Alice  Smith \r\n",
            Field(6, "Source"),
            Field(9, "Destination"));

        Assert.Equal("Alice  Smith", value);
    }

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
            {
                var message = (await Assert.ThrowsAsync<PipelineNonRetryableException>(harness.Run)).Message;
                Assert.Contains("1 record could not be copied (0 inserted, 0 updated)", message);
                Assert.Contains("First error: Destination field 'Destination key' needs a valid number from source field 'Source key'.", message);
            }
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
        Assert.Contains("Destination field 'Number' needs a valid number from source field 'Name'", error.Message);
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
        harness.Records.FindRecordsByFieldValuesAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new List<(object? Value, Guid PublicId)> { ("none", Guid.NewGuid()), ("none", Guid.NewGuid()) });
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
        harness.Records.FindRecordsByFieldValuesAsync(Arg.Any<AppTable>(), Arg.Is<AppField>(f => f.Fid == 42), Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new List<(object? Value, Guid PublicId)> { ("none", id) });
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
        harness.Records.FindRecordsByFieldValuesAsync(Arg.Any<AppTable>(), Arg.Is<AppField>(f => f.Fid == 9),
            Arg.Is<IReadOnlyCollection<object>>(k => k.Count == 1 && Equals(k.First(), "none")), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new List<(object? Value, Guid PublicId)> { ("none", id) });
        var result = JsonDocument.Parse(await harness.Run()).RootElement;
        Assert.Equal(1, result.GetProperty("UpdatedCount").GetInt32());
        await harness.Writes.Received(1).ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), id,
            Arg.Is<IReadOnlyDictionary<long, object?>>(v => v.ContainsKey(9) && !v.ContainsKey(6)), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction>(), false, null);
    }

    // ── Encrypted-field filtering / merge-key matching regression coverage ──────────────────
    // The source field's physical column stores ciphertext when encrypted, so a "query" filter
    // condition or a merge-key "=" lookup against it can never match in SQL. These mirror the
    // encrypted-field fix applied to CopyRecordsExecutor: filter conditions on encrypted fields
    // are evaluated in memory against decrypted candidate rows, and the merge-key lookup uses a
    // decrypted client-side index instead of a SQL "=" comparison.

    [Fact]
    public async Task Copy_EncryptedQueryFilter_MatchesDecryptedSourceValueInMemoryAndNeverReachesSql()
    {
        using var harness = new Harness();
        harness.SourceFields[0].IsEncrypted = true;
        harness.SourceValues.Clear();
        harness.SourceValues.AddRange(new[] { "Ronak Dhamsaniya", "Someone Else" });

        var result = JsonDocument.Parse(await harness.Run("{6.CT.'ronak'}")).RootElement;

        Assert.Equal(1, result.GetProperty("InsertedCount").GetInt32());
        await harness.Records.Received(1).CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Is<IReadOnlyDictionary<long, object?>>(v => (string)v[9]! == "Ronak Dhamsaniya"),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>());
        // The encrypted condition must never reach the snapshot reader as a physical filter.
        harness.Search.Received(1).ReadCopySnapshotAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Is<FilterGroup?>(f => f == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Copy_EncryptedQueryFilter_CaseInsensitiveNoMatch_InsertsNothing()
    {
        using var harness = new Harness();
        harness.SourceFields[0].IsEncrypted = true;
        harness.SourceValues.Clear();
        harness.SourceValues.AddRange(new[] { "Someone Else", "Another Person" });

        var result = JsonDocument.Parse(await harness.Run("{6.CT.'RONAK'}")).RootElement;

        Assert.Equal(0, result.GetProperty("InsertedCount").GetInt32());
        Assert.Empty(harness.Records.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "CreateAsync"));
    }

    [Fact]
    public async Task Copy_EncryptedMergeField_MatchesExistingRecordByDecryptedValue_UpdatesInsteadOfDuplicating()
    {
        using var harness = new Harness();
        harness.DestinationField.IsEncrypted = true; // merge field ("fid_9") is encrypted
        var id = Guid.NewGuid();
        harness.Records.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, Arg.Any<int>(),
            Arg.Is<FilterGroup?>(f => f == null), null, null, Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["PublicId"] = id, ["f_9"] = "none" } });

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(1, result.GetProperty("UpdatedCount").GetInt32());
        Assert.Equal(0, result.GetProperty("InsertedCount").GetInt32());
        await harness.Writes.Received(1).ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), id,
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction>(), false, null);
        // The merge lookup must never run a SQL "=" condition against the encrypted column.
        await harness.Records.DidNotReceive().ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Is<FilterGroup?>(f => f != null), Arg.Any<IReadOnlyList<SortSpec>>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    // ── Azure AI Search routing for Copy Records' source filter ─────────────────────────────
    // Same rules as the pipeline Search Records step: when UseAzureAiForGridSearch is on and the index is healthy, a filter
    // whose every condition is an "is"/"in" on a plain-text physical field that is both searchable and filterable is
    // answered by the index, which only proposes candidate record ids (all of them, paged). The snapshot read then takes
    // those candidates from SQL with the complete filter still applied. Everything else - and an index that finds nothing
    // or fails - reads the source from SQL.

    private static async IAsyncEnumerable<AiSearchIdPage> AiPages(params AiSearchIdPage[] pages)
    {
        foreach (var page in pages) { yield return page; await Task.Yield(); }
    }

    private static void IndexSource(Harness harness)
    {
        harness.SourceFields[0].IsSearchable = true;
        harness.SourceFields[0].IsFilterable = true;
        harness.AzureSearch.IsGridSearchEnabled.Returns(true);
        harness.AzureSearch.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
    }

    private static FilterCondition IdRestriction(FilterGroup? filter) =>
        filter!.Nodes.Select(n => n.Condition).First(c => c is { FieldId: 3 })!;

    [Fact]
    public async Task Copy_GridSearchEnabled_HealthyIndex_ReadsCandidatesFromSqlWithTheFullFilterStillApplied()
    {
        using var harness = new Harness();
        IndexSource(harness);
        var matchedPublicId = Guid.NewGuid();
        harness.AzureSearch.SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), harness.Source.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => AiPages(new AiSearchIdPage([matchedPublicId], "~")));
        harness.Records.GetIdsByPublicIdsAsync(harness.Source, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<long> { 7 });

        FilterGroup? capturedFilter = null;
        harness.Search.ReadCopySnapshotAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(ci => { capturedFilter = ci.ArgAt<FilterGroup?>(2); return harness.EmptyPage(); });

        await harness.Run("{6.EX.'none'}");

        harness.AzureSearch.Received(1).SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), harness.Source.Id,
            Arg.Is<string>(odata => odata.Contains("search.ismatch('\"none\"', 'f_6', 'full', 'any')")), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await harness.AzureSearch.DidNotReceive().SearchRecordsByFilterAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.NotNull(capturedFilter);
        Assert.Equal("in", IdRestriction(capturedFilter).Operator);
        // The original condition is still there: the index proposes candidates, SQL decides.
        Assert.NotNull(capturedFilter!.Nodes[0].Group);
    }

    [Fact]
    public async Task Copy_GridSearchEnabled_AiSearchThrows_FallsBackToOriginalSqlFilter()
    {
        using var harness = new Harness();
        IndexSource(harness);
        harness.AzureSearch.SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), harness.Source.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ThrowingPages());

        FilterGroup? capturedFilter = null;
        harness.Search.ReadCopySnapshotAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(ci => { capturedFilter = ci.ArgAt<FilterGroup?>(2); return harness.EmptyPage(); });

        var result = JsonDocument.Parse(await harness.Run("{6.EX.'none'}")).RootElement;

        Assert.Equal(0, result.GetProperty("ErrorCount").GetInt32());
        Assert.NotNull(capturedFilter);
        // Fell back to the original condition on field 6, with no Id restriction.
        Assert.Equal(6, capturedFilter!.Nodes[0].Group!.Nodes[0].Group!.Nodes[0].Condition!.FieldId);
        Assert.DoesNotContain(capturedFilter.Nodes, n => n.Condition is { FieldId: 3 });

        static async IAsyncEnumerable<AiSearchIdPage> ThrowingPages()
        {
            await Task.Yield();
            throw new InvalidOperationException("Azure Search unavailable");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task Copy_GridSearchEnabled_NoAiMatches_ReadsTheSourceFromSqlInsteadOfCopyingNothing()
    {
        using var harness = new Harness();
        IndexSource(harness);
        harness.AzureSearch.SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), harness.Source.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => AiPages());   // not indexed (yet)

        var result = JsonDocument.Parse(await harness.Run("{6.EX.'none'}")).RootElement;

        Assert.Equal(1, result.GetProperty("InsertedCount").GetInt32());   // the SQL snapshot still has its row
        harness.Search.Received(1).ReadCopySnapshotAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Is<FilterGroup?>(f => f != null && !f.Nodes.Any(n => n.Condition != null && n.Condition.FieldId == 3)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Copy_GridSearchDisabled_NeverCallsAzureSearch()
    {
        using var harness = new Harness();
        harness.AzureSearch.IsGridSearchEnabled.Returns(false);

        await harness.Run("{6.EX.'none'}");

        harness.AzureSearch.DidNotReceive().SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{6.CT.'none'}", "Text", true, true)]        // contains: the index cannot answer it exactly
    [InlineData("{6.EX.'none'}", "Text", false, true)]       // not searchable
    [InlineData("{6.EX.'none'}", "Text", true, false)]       // not filterable
    [InlineData("{6.EX.'none'}", "Number", true, true)]      // numbers are compared as text in the index
    [InlineData("{6.EX.'none'}", "Formula_Text", true, true)]  // no column
    [InlineData("{6.EX.'none'}", "Lookup", true, true)]
    [InlineData("{6.EX.'none'}", "Reference", true, true)]
    public async Task Copy_GridSearchEnabled_IneligibleQuery_ReadsFromSqlAndNeverTouchesTheIndex(string query, string type, bool searchable, bool filterable)
    {
        using var harness = new Harness();
        IndexSource(harness);
        harness.SourceFields[0].TypeCode = type;
        harness.SourceFields[0].IsSearchable = searchable;
        harness.SourceFields[0].IsFilterable = filterable;

        // Some of these combinations are rejected by Copy Records itself (an unsupported operator or mismatched field types); the
        // point is only that the index is never consulted for a search it cannot answer exactly.
        try { await harness.Run(query); } catch (Exception ex) when (ex is ValidationException or PipelineNonRetryableException) { }

        harness.AzureSearch.DidNotReceive().SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Copy_GridSearchEnabled_EncryptedField_CandidatesAreStillCheckedInMemoryAgainstTheDecryptedValue()
    {
        // The index proposes a candidate; the encrypted source value does not actually equal the query, so it is dropped.
        using var harness = new Harness();
        IndexSource(harness);
        harness.SourceFields[0].IsEncrypted = true;
        harness.SourceValues.Clear();
        harness.SourceValues.AddRange(new[] { "Someone Else" });
        harness.AzureSearch.SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), harness.Source.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => AiPages(new AiSearchIdPage([Guid.NewGuid()], "~")));
        harness.Records.GetIdsByPublicIdsAsync(harness.Source, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<long> { 7 });

        var result = JsonDocument.Parse(await harness.Run("{6.EX.'none'}")).RootElement;

        Assert.Equal(0, result.GetProperty("InsertedCount").GetInt32());
        Assert.DoesNotContain(harness.Records.ReceivedCalls(), c => c.GetMethodInfo().Name == "CreateAsync");
    }

    [Fact]
    public async Task Copy_GridSearchEnabled_MoreMatchesThanOnePageOrSqlChunk_CopiesAllOfThemWithoutTruncating()
    {
        // Regression: an earlier version capped AI Search matches (50,000 in the service, 2,000 here) and silently dropped
        // the rest from the copy. Every page must be read and every candidate copied, via chunked reads.
        using var harness = new Harness();
        IndexSource(harness);
        const int pageCount = 5, perPage = 1000;   // 5,000 candidates: three 2,000-id SQL chunks
        harness.AzureSearch.SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), harness.Source.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => AiPages(Enumerable.Range(0, pageCount)
                .Select(_ => new AiSearchIdPage(Enumerable.Range(0, perPage).Select(_ => Guid.NewGuid()).ToList(), "x")).ToArray()));
        long nextId = 0;
        harness.Records.GetIdsByPublicIdsAsync(harness.Source, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(1).Select(_ => Interlocked.Increment(ref nextId)).ToList());

        var snapshotCallCount = 0;
        long idsRequested = 0;
        harness.Search.ReadCopySnapshotAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                snapshotCallCount++;
                var chunkIds = JsonSerializer.Deserialize<long[]>(IdRestriction(ci.ArgAt<FilterGroup?>(2)).Value!)!;
                idsRequested += chunkIds.Length;
                return ChunkPage(chunkIds.Length);
            });

        var result = JsonDocument.Parse(await harness.Run("{6.EX.'none'}")).RootElement;

        Assert.Equal(pageCount * perPage, idsRequested);
        Assert.Equal(5, snapshotCallCount);   // 5,000 candidates in chunks of 1,000: room under SQL Server's 2,100 parameters for the filter
        Assert.Equal(pageCount * perPage, result.GetProperty("InsertedCount").GetInt32());

        static async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ChunkPage(int count)
        {
            await Task.CompletedTask;
            yield return Enumerable.Range(0, count).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["f_6"] = "none" + Guid.NewGuid().ToString("N"),   // distinct merge values: two rows with one value would merge into one record
                ["PublicId"] = Guid.NewGuid()
            }).ToList();
        }
    }

    [Fact]
    public async Task Copy_GridSearchEnabled_MoreThanFiftyThousandCandidates_AllReachTheSqlReadInOrder()
    {
        // 60,000 candidates: past the 50,000 the old search method silently stopped at. Nothing is copied here; the point is
        // that every candidate id is handed to the SQL snapshot read, in ascending id order, in 1,000-id chunks.
        using var harness = new Harness();
        IndexSource(harness);
        const int pageCount = 60, perPage = 1000;
        harness.AzureSearch.SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), harness.Source.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => AiPages(Enumerable.Range(0, pageCount)
                .Select(_ => new AiSearchIdPage(Enumerable.Range(0, perPage).Select(_ => Guid.NewGuid()).ToList(), "x")).ToArray()));
        long nextId = pageCount * perPage + 1;   // handed out descending, so the executor must sort them
        harness.Records.GetIdsByPublicIdsAsync(harness.Source, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(1).Select(_ => Interlocked.Decrement(ref nextId)).ToList());

        var requested = new List<long>();
        var chunkSizes = new List<int>();
        harness.Search.ReadCopySnapshotAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var chunk = JsonSerializer.Deserialize<long[]>(IdRestriction(ci.ArgAt<FilterGroup?>(2)).Value!)!;
                chunkSizes.Add(chunk.Length);
                requested.AddRange(chunk);
                return harness.EmptyPage();
            });

        await harness.Run("{6.EX.'none'}");

        Assert.Equal(pageCount * perPage, requested.Count);
        Assert.Equal(requested.OrderBy(i => i), requested);
        Assert.Equal(pageCount * perPage / PipelineEngine.AiCandidateRecordIdChunkSize, chunkSizes.Count);
        Assert.All(chunkSizes, size => Assert.True(size <= PipelineEngine.AiCandidateRecordIdChunkSize));
        Assert.Equal(pageCount * perPage, requested.Distinct().Count());
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

    // ── Batch (whole page) write path ───────────────────────────────────────────────────────
    // A destination with nothing that makes one row depend on another (no unique / reference field, no data rule) is written a
    // page at a time: one insert for the page, one lookup of the merge values, one receipt write, one audit write, one count update.

    [Fact]
    public async Task Copy_ExportingAFormulaOverStoredFields_ProjectsOnlyThatFormula_NeverTheSourcesSummaries()
    {
        // Order → Order Detail: the copy exports "ORD000" & ToText([Record ID#]); Order also has a Summary over Order Detail.
        // That Summary is computed in memory over every Order Detail row (encrypted app), page after page — it must not run.
        using var harness = BatchHarness(300);
        harness.SourceFields[0].TypeCode = "Formula_Text";
        harness.SourceFields[0].Settings = "{\"expression\":\"\\\"ORD000\\\" & ToText([Record ID#])\"}";
        harness.SourceFields.Add(new AppField { Id = 103, Fid = 3, Name = "Record ID#", TypeCode = "RecordId", IsSystem = true, PhysicalColumnName = "Id" });
        harness.SourceFields.Add(new AppField { Id = 110, Fid = 10, Name = "Sum of amount", TypeCode = "Summary", Settings = "{\"childTableId\":6,\"function\":\"Sum\"}" });
        harness.FormulaProjector.GetFormulaDependencies(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IEnumerable<long>>())
            .Returns(new HashSet<long> { 6, 3 });
        var number = 0;
        harness.FormulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable>())
            .Returns(c => c.Arg<IReadOnlyList<IReadOnlyDictionary<string, object?>>>()
                .Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [6] = $"ORD000{++number}" }).ToList());

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(300, result.GetProperty("InsertedCount").GetInt32());
        Assert.Empty(harness.RelationalProjector.ReceivedCalls());
        var projected = harness.FormulaProjector.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "Project")
            .Select(c => (IReadOnlyList<AppField>)c.GetArguments()[0]!).ToList();
        Assert.NotEmpty(projected);
        Assert.All(projected, fields => { Assert.Contains(fields, f => f.Fid == 6); Assert.DoesNotContain(fields, f => f.Fid == 10); });
    }

    /// <summary>The field lists the relationship projector was asked to project.</summary>
    private static List<IReadOnlyList<AppField>> RelationalCalls(Harness harness) => harness.RelationalProjector.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == "ProjectAsync").Select(c => (IReadOnlyList<AppField>)c.GetArguments()[1]!).ToList();

    private static List<IReadOnlyList<AppField>> FormulaCalls(Harness harness) => harness.FormulaProjector.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == "Project").Select(c => (IReadOnlyList<AppField>)c.GetArguments()[0]!).ToList();

    [Fact]
    public async Task Copy_ExportingAFormulaThatReadsAnotherFormula_ProjectsBothFormulas_AndNoSummary()
    {
        // A = B & "-x", B = "ORD000" & ToText([Record ID#]): both formulas, still nothing relational.
        using var harness = BatchHarness(20);
        harness.SourceFields[0].TypeCode = "Formula_Text";
        harness.SourceFields[0].Settings = "{\"expression\":\"[Order no] & \\\"-x\\\"\"}";
        harness.SourceFields.Add(new AppField { Id = 107, Fid = 7, Name = "Order no", TypeCode = "Formula_Text", Settings = "{\"expression\":\"\\\"ORD000\\\" & ToText([Record ID#])\"}" });
        harness.SourceFields.Add(new AppField { Id = 103, Fid = 3, Name = "Record ID#", TypeCode = "RecordId", IsSystem = true, PhysicalColumnName = "Id" });
        harness.SourceFields.Add(new AppField { Id = 110, Fid = 10, Name = "Sum of amount", TypeCode = "Summary", Settings = "{\"childTableId\":6,\"function\":\"Sum\"}" });
        harness.FormulaProjector.GetFormulaDependencies(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IEnumerable<long>>()).Returns(new HashSet<long> { 6, 7, 3 });

        await harness.Run();

        Assert.Empty(RelationalCalls(harness));
        Assert.All(FormulaCalls(harness), fields =>
        {
            Assert.Contains(fields, f => f.Fid == 6);
            Assert.Contains(fields, f => f.Fid == 7);
            Assert.DoesNotContain(fields, f => f.Fid == 10);
        });
    }

    [Fact]
    public async Task Copy_ExportingASummary_ProjectsThatSummaryOnly_NotTheSourcesOtherSummaries()
    {
        using var harness = BatchHarness(10);
        harness.SourceFields[0].TypeCode = "Summary";
        harness.SourceFields[0].Settings = "{\"childTableId\":6,\"function\":\"Sum\"}";
        harness.SourceFields.Add(new AppField { Id = 111, Fid = 11, Name = "Line count", TypeCode = "Summary", Settings = "{\"childTableId\":7,\"function\":\"Count\"}" });
        harness.SourceFields.Add(new AppField { Id = 112, Fid = 12, Name = "Customer", TypeCode = "Reference", Settings = "{\"parentTableId\":8}" });

        await harness.Run();

        var calls = RelationalCalls(harness);
        Assert.NotEmpty(calls);
        Assert.All(calls, fields =>
        {
            Assert.Contains(fields, f => f.Fid == 6);
            Assert.DoesNotContain(fields, f => f.Fid == 11);
            Assert.DoesNotContain(fields, f => f.Fid == 12);   // a Reference nobody exports or reads is not labelled either
        });
    }

    [Fact]
    public async Task Copy_ExportingALookup_ProjectsThatLookupOnly()
    {
        using var harness = BatchHarness(10);
        harness.SourceFields[0].TypeCode = "Lookup";
        harness.SourceFields[0].Settings = "{\"sourceTableId\":8,\"referenceFid\":12,\"sourceFid\":6}";
        harness.SourceFields.Add(new AppField { Id = 112, Fid = 12, Name = "Customer", TypeCode = "Reference", Settings = "{\"parentTableId\":8}" });
        harness.SourceFields.Add(new AppField { Id = 110, Fid = 10, Name = "Sum of amount", TypeCode = "Summary", Settings = "{\"childTableId\":6,\"function\":\"Sum\"}" });

        await harness.Run();

        var calls = RelationalCalls(harness);
        Assert.NotEmpty(calls);
        Assert.All(calls, fields => { Assert.Contains(fields, f => f.Fid == 6); Assert.DoesNotContain(fields, f => f.Fid == 10); });
    }

    [Fact]
    public async Task Copy_FormulaWhoseInputsCannotBeListed_StillGetsTheFullProjection()
    {
        // GetFormulaDependencies answers null (e.g. a field chosen per record): project everything, as before.
        using var harness = BatchHarness(10);
        harness.SourceFields[0].TypeCode = "Formula_Text";
        harness.SourceFields[0].Settings = "{\"expression\":\"ToText([Record ID#])\"}";
        harness.SourceFields.Add(new AppField { Id = 110, Fid = 10, Name = "Sum of amount", TypeCode = "Summary", Settings = "{\"childTableId\":6,\"function\":\"Sum\"}" });
        harness.FormulaProjector.GetFormulaDependencies(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IEnumerable<long>>()).Returns((IReadOnlySet<long>?)null);

        await harness.Run();

        var calls = RelationalCalls(harness);
        Assert.NotEmpty(calls);
        Assert.All(calls, fields => Assert.Contains(fields, f => f.Fid == 10));
    }

    private const int RowByRowGuardFid = 99;

    private static Harness BatchHarness(int rows)
    {
        var harness = new Harness();
        harness.DestinationFields.RemoveAll(f => f.Fid == RowByRowGuardFid);   // nothing but the merge field is unique: the batch path
        harness.SourceValues.Clear();
        harness.SourceValues.AddRange(Enumerable.Range(0, rows).Select(i => "key" + i));
        return harness;
    }

    private static int Calls(Harness harness, string method) => harness.Records.ReceivedCalls().Count(c => c.GetMethodInfo().Name == method);

    [Fact]
    public async Task Batch_WritesWholePagesWithoutPerRowCalls()
    {
        using var harness = BatchHarness(600);   // pages of 250, 250 and 100
        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(600, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(0, result.GetProperty("ErrorCount").GetInt32());
        Assert.Equal(3, Calls(harness, "CreateManyAsync"));
        Assert.Equal(3, Calls(harness, "FindRecordsByFieldValuesAsync"));   // one merge lookup per page, not per row
        Assert.Equal(0, Calls(harness, "CreateAsync"));
        Assert.Equal(0, Calls(harness, "ListAsync"));
        Assert.Equal(0, Calls(harness, "GetActiveRecordIdByPublicIdAsync"));
        var audits = harness.Audit.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "LogActivitiesAsync").ToList();
        Assert.Equal(3, audits.Count);
        Assert.Equal(600, audits.Sum(c => ((IReadOnlyCollection<string>)c.GetArguments()[2]!).Count));
        Assert.DoesNotContain(harness.Audit.ReceivedCalls(), c => c.GetMethodInfo().Name == "LogActivityAsync");
        var counts = harness.Tables.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "IncrementRecordCountByAsync").Select(c => (int)c.GetArguments()[1]!).ToList();
        Assert.Equal(new[] { 250, 250, 100 }, counts);
        await harness.Tables.DidNotReceive().IncrementRecordCountAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        // The created records' events are handed over a page at a time (3 pages), not one call per record.
        var added = harness.Interceptor.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "InterceptAddedManyAsync").ToList();
        Assert.Equal(3, added.Count);
        Assert.Equal(600, added.Sum(c => ((System.Collections.ICollection)c.GetArguments()[2]!).Count));
        Assert.DoesNotContain(harness.Interceptor.ReceivedCalls(), c => c.GetMethodInfo().Name == "InterceptAsync");
    }

    [Fact]
    public async Task Batch_ReplayReadsReceiptsAndWritesNothingAgain()
    {
        using var harness = BatchHarness(300);
        var first = await harness.Run();
        var second = await harness.Run();

        Assert.Equal(first, second);
        Assert.Equal(2, Calls(harness, "CreateManyAsync"));   // 250 + 50, only from the first run
    }

    [Fact]
    public async Task Batch_InterruptedAfterSomePagesResumesOnlyTheRest()
    {
        using var harness = BatchHarness(500);
        var attempts = 0;
        harness.Records.CreateManyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>())
            .Returns(c =>
            {
                // The second page fails in the batch AND in the row-by-row retry (a database that is down), so the step fails and is retried.
                if (++attempts == 2) throw new InvalidOperationException("Transient database failure");
                return Task.FromResult((IReadOnlyList<Guid>)c.Arg<IReadOnlyList<IReadOnlyDictionary<long, object?>>>().Select(_ => Guid.NewGuid()).ToList());
            });
        var createCalls = 0;
        harness.Records.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(),
            Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>())
            .Returns(_ => ++createCalls == 1 ? throw new InvalidOperationException("Transient database failure") : Task.FromResult(Guid.NewGuid()));

        await Assert.ThrowsAsync<InvalidOperationException>(harness.Run);
        harness.SourceValues.Clear();   // the retry must use the saved snapshot
        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(500, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(0, result.GetProperty("ErrorCount").GetInt32());
        // Page 1 was committed by the first run; the retry did not insert it again.
        Assert.Equal(3, Calls(harness, "CreateManyAsync"));   // page 1, page 2 (failed), page 2 again on the retry
        Assert.Equal(1, createCalls);                      // the row-by-row attempt of the failed page
    }

    [Fact]
    public async Task Batch_RowRejectedByValidationIsAnErrorAndTheRestAreStored()
    {
        using var harness = BatchHarness(0);
        harness.Config.TerminateOnError = "No";
        harness.DestinationField.IsRequired = true;
        harness.SourceValues.AddRange(new[] { "a", "", "b" });
        IReadOnlyList<IReadOnlyDictionary<long, object?>>? written = null;
        harness.Records.CreateManyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Do<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(r => written = r),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>())
            .Returns(c => (IReadOnlyList<Guid>)c.Arg<IReadOnlyList<IReadOnlyDictionary<long, object?>>>().Select(_ => Guid.NewGuid()).ToList());

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(2, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("ErrorCount").GetInt32());
        Assert.Equal(new object?[] { "a", "b" }, written!.Select(r => r[9]).ToArray());
        Assert.Equal(0, Calls(harness, "CreateAsync"));
        var again = JsonDocument.Parse(await harness.Run()).RootElement;   // the rejected row's receipt is stored too
        Assert.Equal(1, again.GetProperty("ErrorCount").GetInt32());
        Assert.Equal(1, Calls(harness, "CreateManyAsync"));
    }

    [Fact]
    public async Task Batch_FailureOfTheBatchRedoesThePageRowByRow()
    {
        using var harness = BatchHarness(3);
        harness.Records.CreateManyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>())
            .Returns<IReadOnlyList<Guid>>(_ => throw new PowerBase.Domain.Exceptions.ConflictException("unique value in use"));

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(3, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(3, Calls(harness, "CreateAsync"));
        await harness.Audit.DidNotReceive().LogActivitiesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>());
        await harness.Tables.DidNotReceive().IncrementRecordCountByAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_MatchedRecordIsUpdatedAndNewOnesAreInserted()
    {
        using var harness = BatchHarness(3);   // key0, key1, key2
        var existing = Guid.NewGuid();
        harness.Records.FindRecordsByFieldValuesAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new List<(object? Value, Guid PublicId)> { ("KEY1 ", existing) });   // same value as key1: case and trailing spaces do not matter

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(2, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("UpdatedCount").GetInt32());
        await harness.Writes.Received(1).ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), existing,
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction>(), false, null);
        Assert.Equal(1, Calls(harness, "CreateManyAsync"));
    }

    [Fact]
    public async Task Batch_DuplicateMatchesInTheDestinationRejectOnlyThatRow()
    {
        using var harness = BatchHarness(2);
        harness.Config.TerminateOnError = "No";
        harness.Records.FindRecordsByFieldValuesAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new List<(object? Value, Guid PublicId)> { ("key0", Guid.NewGuid()), ("key0", Guid.NewGuid()) });

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(1, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("ErrorCount").GetInt32());
        Assert.Empty(harness.Writes.ReceivedCalls());
    }

    [Fact]
    public async Task Batch_TwoNewRowsWithTheSameMergeValueAreDoneRowByRowSoTheSecondUpdatesTheFirst()
    {
        using var harness = BatchHarness(0);
        harness.SourceValues.AddRange(new[] { "same", "same" });
        var created = Guid.NewGuid();
        harness.Records.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(),
            Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>()).Returns(created);

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(1, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("UpdatedCount").GetInt32());
        Assert.Equal(0, Calls(harness, "CreateManyAsync"));
        await harness.Writes.Received(1).ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), created,
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction>(), false, null);
    }

    [Fact]
    public async Task Batch_UniqueDestinationFieldDoesNotForceTheRowByRowPath()
    {
        using var harness = BatchHarness(5);
        harness.DestinationFields.Add(Field(RowByRowGuardFid, "other", unique: true));   // a second unique field: checked per page, so the batch path still applies

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(5, result.GetProperty("InsertedCount").GetInt32());
        Assert.True(Calls(harness, "CreateManyAsync") > 0);
        Assert.Equal(0, Calls(harness, "CreateAsync"));
    }

    [Fact]
    public async Task Batch_ReferenceFieldPointingAtAnotherTableDoesNotForceTheRowByRowPath()
    {
        using var harness = BatchHarness(5);
        harness.DestinationFields.Add(new AppField { Id = 500, Fid = RowByRowGuardFid, Name = "Related", TypeCode = "Reference", Settings = "{\"parentTableId\":77}" });

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(5, result.GetProperty("InsertedCount").GetInt32());
        Assert.True(Calls(harness, "CreateManyAsync") > 0);
        Assert.Equal(0, Calls(harness, "CreateAsync"));
    }

    [Fact]
    public async Task Batch_ReferenceFieldPointingAtTheDestinationKeepsTheRowByRowPath()
    {
        using var harness = BatchHarness(5);
        harness.DestinationFields.Add(new AppField { Id = 500, Fid = RowByRowGuardFid, Name = "Parent", TypeCode = "Reference", Settings = "{\"parentTableId\":2}" });

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(5, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(0, Calls(harness, "CreateManyAsync"));
        Assert.Equal(5, Calls(harness, "CreateAsync"));
    }

    // ── Unique destination fields on the batch path ─────────────────────────────────────────
    // A unique field other than the merge field is checked a page at a time (one query for the page's values, plus the values earlier rows of the
    // same page are about to take) — or, for a field stored encrypted, against one in-memory copy of its values — instead of one table scan per record.

    /// <summary>Source rows "a1", "b1", ... mapped to the merge field (distinct) and, through <paramref name="uniqueOf"/>, to a unique destination field "name".</summary>
    private static Harness UniqueHarness(IEnumerable<string> rows, Func<string, string> uniqueOf, bool encrypted = false, bool batch = true)
    {
        var harness = batch ? BatchHarness(0) : new Harness();
        harness.SourceValues.Clear();
        harness.SourceValues.AddRange(rows);
        harness.SourceFields.Add(Field(7, "src name"));
        var unique = Field(10, "name", unique: true);
        unique.IsEncrypted = encrypted;
        harness.DestinationFields.Add(unique);
        harness.Config.SourceFields = new() { "fid_6", "fid_7" };
        harness.Config.DestinationFields = new() { "fid_9", "fid_10" };
        harness.Config.TerminateOnError = "No";
        harness.ColumnValue = (fid, value) => fid == 7 ? uniqueOf(value) : value;
        return harness;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>Per-record database checks of one unique destination field (the merge field is still checked by the database, as before).</summary>
    private static int DuplicateChecks(Harness harness, int fid) => harness.Records.ReceivedCalls()
        .Count(c => c.GetMethodInfo().Name == "HasValueDuplicateAsync" && ((AppField)c.GetArguments()[1]!).Fid == fid);

    private static string ReceiptKey(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)));

    [Fact]
    public async Task BatchUnique_ValueAlreadyInTheTableIsRejectedAndTheRestAreInserted()
    {
        using var harness = UniqueHarness(new[] { "a1", "b1", "c1" }, v => v[..1]);   // unique values a, b, c
        harness.Records.GetExistingValueIndexesAsync(Arg.Any<AppTable>(), Arg.Is<AppField>(f => f.Fid == 10), Arg.Any<IReadOnlyList<object>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlySet<int>)c.Arg<IReadOnlyList<object>>().Select((v, i) => (v, i)).Where(x => (string)x.v == "b").Select(x => x.i).ToHashSet());

        var result = Parse(await harness.Run());

        Assert.Equal(2, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("ErrorCount").GetInt32());
        Assert.Contains("'name' must be unique", result.GetProperty("Errors")[0].GetString());
        Assert.True(Calls(harness, "CreateManyAsync") > 0);
        Assert.Equal(0, Calls(harness, "CreateAsync"));
        Assert.Equal(1, Calls(harness, "GetExistingValueIndexesAsync"));   // one query for the whole page, not one per record
        Assert.Equal(0, DuplicateChecks(harness, 10));
        var failed = result.GetProperty("FailedRecords");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.Equal("b1", failed[0].GetProperty("MergeValue").GetString());
        Assert.Contains("'name' must be unique", failed[0].GetProperty("Reason").GetString());
    }

    [Fact]
    public async Task BatchUnique_ValueRepeatedInTheSamePageIsRejected()
    {
        using var harness = UniqueHarness(new[] { "a1", "a2", "b1" }, v => v[..1]);   // unique values a, a, b

        var result = Parse(await harness.Run());

        Assert.Equal(2, result.GetProperty("InsertedCount").GetInt32());   // a1 and b1
        Assert.Equal(1, result.GetProperty("ErrorCount").GetInt32());      // a2 repeats a1's value
        Assert.Equal("a2", result.GetProperty("FailedRecords")[0].GetProperty("MergeValue").GetString());
        Assert.True(Calls(harness, "CreateManyAsync") > 0);
        Assert.Equal(0, Calls(harness, "CreateAsync"));
    }

    [Fact]
    public async Task BatchUnique_ValueWrittenByAnEarlierPageIsSeenByLaterPages()
    {
        var written = new HashSet<string>();
        using var harness = UniqueHarness(Enumerable.Range(0, 300).Select(i => "k" + i), v => "u" + int.Parse(v[1..]) % 260);   // rows 260..299 repeat rows 0..39
        harness.Records.CreateManyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>())
            .Returns(c =>
            {
                var rows = c.Arg<IReadOnlyList<IReadOnlyDictionary<long, object?>>>();
                foreach (var row in rows) written.Add((string)row[10]!);
                return (IReadOnlyList<Guid>)rows.Select(_ => Guid.NewGuid()).ToList();
            });
        harness.Records.GetExistingValueIndexesAsync(Arg.Any<AppTable>(), Arg.Is<AppField>(f => f.Fid == 10), Arg.Any<IReadOnlyList<object>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlySet<int>)c.Arg<IReadOnlyList<object>>().Select((v, i) => (v, i)).Where(x => written.Contains((string)x.v)).Select(x => x.i).ToHashSet());

        var result = Parse(await harness.Run());

        Assert.Equal(260, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(40, result.GetProperty("ErrorCount").GetInt32());
        Assert.Equal(2, Calls(harness, "GetExistingValueIndexesAsync"));   // two pages, one query each
    }

    [Fact]
    public async Task BatchUnique_EncryptedField_IsReadOnceAndCheckedInMemoryAcrossPages()
    {
        using var harness = UniqueHarness(Enumerable.Range(0, 300).Select(i => "k" + i), v => "u" + int.Parse(v[1..]) % 260, encrypted: true);
        harness.Records.GetAllValuesForUniqueCheckAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<string>)new List<string> { "u5" });   // already in the table

        var result = Parse(await harness.Run());

        // u5 is taken (row 5), and rows 260..299 repeat u0..u39 which pages 1 wrote — except u5, which row 265 finds taken in the table.
        Assert.Equal(259, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(41, result.GetProperty("ErrorCount").GetInt32());
        Assert.Equal(1, Calls(harness, "GetAllValuesForUniqueCheckAsync"));   // the column is decrypted once for the whole run
        Assert.Equal(0, DuplicateChecks(harness, 10));            // never one decrypting scan per record
        Assert.Equal(0, Calls(harness, "GetExistingValueIndexesAsync"));
        Assert.True(Calls(harness, "CreateManyAsync") > 0);
        Assert.Equal(0, Calls(harness, "CreateAsync"));
    }

    [Fact]
    public async Task RowByRowUnique_EncryptedField_IsCheckedInMemoryToo()
    {
        using var harness = UniqueHarness(new[] { "a1", "a2", "b1" }, v => v[..1], encrypted: true, batch: false);   // the self-pointing Reference keeps it row by row

        var result = Parse(await harness.Run());

        Assert.Equal(2, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("ErrorCount").GetInt32());
        Assert.Equal(2, Calls(harness, "CreateAsync"));
        Assert.Equal(0, DuplicateChecks(harness, 10));
        Assert.Equal(1, Calls(harness, "GetAllValuesForUniqueCheckAsync"));
    }

    [Fact]
    public async Task BatchUnique_AnUpdateThatMayChangeAUniqueValueMakesTheIndexBeReadAgainBeforeItIsTrusted()
    {
        using var harness = UniqueHarness(new[] { "a1", "b1" }, v => v[..1], encrypted: true);
        var existing = Guid.NewGuid();
        harness.Records.FindRecordsByFieldValuesAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new List<(object? Value, Guid PublicId)> { ("a1", existing) });   // a1 already exists: it is updated, b1 is new

        var result = Parse(await harness.Run());

        Assert.Equal(1, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("UpdatedCount").GetInt32());
        Assert.Equal(0, result.GetProperty("ErrorCount").GetInt32());
        Assert.True(DuplicateChecks(harness, 10) > 0);   // until the index is read again, the database answers
    }

    [Fact]
    public async Task Batch_WritesOneReceiptPerPage_AndAReplayDoesNotWriteTheRowsAgain()
    {
        using var harness = BatchHarness(600);   // 3 pages

        var first = Parse(await harness.Run());

        Assert.Equal(600, first.GetProperty("InsertedCount").GetInt32());
        await harness.Idempotency.DidNotReceive().InsertManyAsync(Arg.Any<IReadOnlyCollection<PipelineStepIdempotencyLog>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        Assert.All(new[] { 0, 1, 2 }, page => Assert.True(harness.Logs.ContainsKey(ReceiptKey($"root/copy/page/{page}"))));
        Assert.Equal(3 + 1 + 3 + 1, harness.Logs.Count);   // 3 snapshot pages, the snapshot manifest, 3 page receipts, the summary — and no per-row receipts
        // The run is interrupted just before it finished: its summary is gone, its page receipts are not.
        harness.Logs.Remove(ReceiptKey("root/copy/complete"));
        harness.Records.ClearReceivedCalls();

        var replay = Parse(await harness.Run());

        Assert.Equal(600, replay.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(0, Calls(harness, "CreateManyAsync"));   // nothing was written a second time
        Assert.Equal(0, Calls(harness, "CreateAsync"));
    }

    [Fact]
    public async Task FailedRecords_AreKeptInPages_AndTheOutputCarriesOnlyAPreview()
    {
        using var harness = UniqueHarness(Enumerable.Range(0, 600).Select(i => "k" + i), v => "same");
        harness.Records.GetExistingValueIndexesAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<IReadOnlyList<object>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlySet<int>)Enumerable.Range(0, c.Arg<IReadOnlyList<object>>().Count).ToHashSet());   // every value is taken

        var result = Parse(await harness.Run());

        Assert.Equal(0, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(600, result.GetProperty("ErrorCount").GetInt32());
        Assert.Equal(CopyFailedRecordStore.PreviewCount, result.GetProperty("FailedRecords").GetArrayLength());
        Assert.Equal(3, result.GetProperty("FailedRecordPages").GetInt32());
        Assert.Equal(600, result.GetProperty("FailedRecordsStored").GetInt64());
        var counts = Enumerable.Range(0, 3)
            .Select(i => JsonSerializer.Deserialize<List<CopyFailedRecord>>(harness.Logs[ReceiptKey("root/copy" + CopyFailedRecordStore.PageSuffix(i))])!).ToList();
        Assert.Equal(new[] { 250, 250, 100 }, counts.Select(c => c.Count));
        var first = counts[0][0];
        Assert.Equal("k0", first.MergeValue);
        Assert.Contains("'name' must be unique", first.Reason);
        Assert.NotEqual(Guid.Empty.ToString(), first.SourcePublicId);
        Assert.Equal("same", first.Values!["src name"]);
        Assert.Equal(600, counts.SelectMany(c => c).Select(c => c.SourcePublicId).Distinct().Count());
    }

    [Fact]
    public async Task FailedRecords_TerminateOnError_ThrowsTheSameExceptionTypeCarryingTheDetails()
    {
        using var harness = UniqueHarness(new[] { "a1", "a2" }, v => "same");
        harness.Config.TerminateOnError = "Yes";
        harness.Records.GetExistingValueIndexesAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<IReadOnlyList<object>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlySet<int>)Enumerable.Range(0, c.Arg<IReadOnlyList<object>>().Count).ToHashSet());

        var ex = await Assert.ThrowsAsync<PipelineNonRetryableException>(harness.Run);   // exactly this type, as before

        Assert.Contains("2 records could not be copied", ex.Message);
        Assert.NotNull(ex.FailureDetails);
        Assert.Equal(2L, ex.FailureDetails!["ErrorCount"]);
        Assert.Equal(1, ex.FailureDetails["FailedRecordPages"]);
        Assert.Equal(2, ((List<CopyFailedRecord>)ex.FailureDetails["FailedRecords"]!).Count);
    }

    [Fact]
    public async Task FailedRecords_ARunThatRejectsNothingStoresNoFailedPages()
    {
        using var harness = UniqueHarness(new[] { "a1", "b1" }, v => v[..1]);

        var result = Parse(await harness.Run());

        Assert.Equal(0, result.GetProperty("ErrorCount").GetInt32());
        Assert.Equal(0, result.GetProperty("FailedRecordPages").GetInt32());
        Assert.DoesNotContain(harness.Logs.Keys, k => k == ReceiptKey("root/copy" + CopyFailedRecordStore.PageSuffix(0)));
    }

    [Fact]
    public void FailedRecordStore_KeysAreTheExecutorsOwnReceiptNamespace()
    {
        Assert.Equal("root/ref_1/copy", CopyFailedRecordStore.Prefix("root/ref_1"));
        Assert.Equal("/failed/7", CopyFailedRecordStore.PageSuffix(7));
        Assert.Equal(ReceiptKey("abc"), Convert.ToHexString(CopyFailedRecordStore.Hash("abc")));
    }

    [Fact]
    public async Task Batch_RejectedRecordsDoNotAbortThePage_AndAreNotWritten()
    {
        using var harness = UniqueHarness(Enumerable.Range(0, 5).Select(i => "k" + i), v => "u" + v[1..]);
        // Required Text field left blank by the source for every other row.
        var required = Field(11, "must have", "Text"); required.IsRequired = true;
        harness.DestinationFields.Add(required);
        harness.SourceFields.Add(Field(8, "src req"));
        harness.Config.SourceFields = new() { "fid_6", "fid_7", "fid_8" };
        harness.Config.DestinationFields = new() { "fid_9", "fid_10", "fid_11" };
        harness.ColumnValue = (fid, value) => fid == 7 ? "u" + value[1..] : fid == 8 ? (int.Parse(value[1..]) % 2 == 0 ? "" : "x") : value;

        var result = Parse(await harness.Run());

        Assert.Equal(2, result.GetProperty("InsertedCount").GetInt32());   // k1 and k3
        Assert.Equal(3, result.GetProperty("ErrorCount").GetInt32());      // k0, k2, k4
        Assert.All(result.GetProperty("FailedRecords").EnumerateArray(), f => Assert.Contains("'must have' is required", f.GetProperty("Reason").GetString()));
    }

    [Fact]
    public async Task BatchTriggers_AreHandedOverOncePerPage_WithEveryCreatedRecord()
    {
        using var harness = BatchHarness(600);

        await harness.Run();

        var calls = harness.Interceptor.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "InterceptAddedManyAsync").ToList();
        Assert.Equal(new[] { 250, 250, 100 }, calls.Select(c => ((System.Collections.ICollection)c.GetArguments()[2]!).Count));
        var ids = calls.SelectMany(c => (IReadOnlyList<(Guid RecordPublicId, IReadOnlyDictionary<long, object?> Values)>)c.GetArguments()[2]!).Select(r => r.RecordPublicId).ToList();
        Assert.Equal(600, ids.Distinct().Count());
        Assert.DoesNotContain(ids, id => id == Guid.Empty);
    }

    [Fact]
    public async Task RowByRow_TriggersStillFirePerRecord()
    {
        using var harness = UniqueHarness(new[] { "a1", "b1", "c1" }, v => v[..1], batch: false);

        await harness.Run();

        Assert.Equal(3, harness.Interceptor.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "InterceptAsync"));
        Assert.DoesNotContain(harness.Interceptor.ReceivedCalls(), c => c.GetMethodInfo().Name == "InterceptAddedManyAsync");
    }

    [Fact]
    public async Task Batch_RecordIdIsReadBackForTheTriggersAndMatchedByNumber()
    {
        using var harness = BatchHarness(0);
        harness.SourceValues.AddRange(new[] { "1", "2" });
        var idField = new AppField { Id = 3 + 100, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, IsPrimary = true, PhysicalColumnName = "Id" };
        harness.DestinationFields.Add(idField);
        harness.SourceFields.Add(new AppField { Id = 3 + 100, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id" });
        harness.DestinationField.IsPrimary = false;
        harness.DestinationField.IsUnique = false;   // the Record ID# is the only key: the batch path
        harness.Config.MergeField = "fid_3";
        var ids = new Dictionary<Guid, long>();
        harness.Records.CreateManyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>())
            .Returns(c =>
            {
                var created = c.Arg<IReadOnlyList<IReadOnlyDictionary<long, object?>>>().Select(_ => Guid.NewGuid()).ToList();
                foreach (var (id, n) in created.Select((id, n) => (id, n))) ids[id] = 500 + n;
                return (IReadOnlyList<Guid>)created;
            });
        harness.Records.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlyDictionary<Guid, long>)c.Arg<IReadOnlyCollection<Guid>>().ToDictionary(g => g, g => ids[g]));
        harness.Config.SourceFields = new() { "fid_6", "fid_3" };
        harness.Config.DestinationFields = new() { "fid_9", "fid_3" };

        await harness.Run();

        var eventRecords = harness.Interceptor.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "InterceptAddedManyAsync")
            .SelectMany(c => (IReadOnlyList<(Guid RecordPublicId, IReadOnlyDictionary<long, object?> Values)>)c.GetArguments()[2]!).ToList();
        Assert.Equal(2, eventRecords.Count);
        Assert.All(eventRecords, r => Assert.True(r.Values.ContainsKey(3) && (Convert.ToInt64(r.Values[3]) == 500 || Convert.ToInt64(r.Values[3]) == 501)));
    }

    [Fact]
    public async Task Batch_NumberMergeValuesMatchByValueNotByText()
    {
        using var harness = BatchHarness(0);
        harness.DestinationField.TypeCode = "Number";
        harness.SourceFields[0].TypeCode = "Number";
        harness.SourceValues.AddRange(new[] { "5", "6" });
        var existing = Guid.NewGuid();
        harness.Records.FindRecordsByFieldValuesAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(new List<(object? Value, Guid PublicId)> { (5.0000m, existing) });   // the database returns DECIMAL(18,4)

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(1, result.GetProperty("UpdatedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("InsertedCount").GetInt32());
        await harness.Records.Received(1).FindRecordsByFieldValuesAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(),
            Arg.Is<IReadOnlyCollection<object>>(k => k.All(x => x is decimal)), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_RowMatchingTheRecordIdOfAnEarlierNewRowOfThePageIsDoneRowByRow()
    {
        // Row by row, the second source row (Record ID# 500) matches the record the first row just created as #500 and updates it.
        using var harness = BatchHarness(0);
        harness.SourceValues.AddRange(new[] { "501", "500" });
        var idField = new AppField { Id = 103, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, IsPrimary = true, PhysicalColumnName = "Id" };
        harness.DestinationFields.Add(idField);
        harness.SourceFields.Add(new AppField { Id = 103, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id" });
        harness.DestinationField.IsPrimary = false;
        harness.DestinationField.IsUnique = false;
        harness.Config.MergeField = "fid_3";
        harness.Config.SourceFields = new() { "fid_6", "fid_3" };
        harness.Config.DestinationFields = new() { "fid_9", "fid_3" };
        var ids = new Dictionary<Guid, long>();
        long nextId = 500;
        harness.Records.CreateManyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>())
            .Returns(c =>
            {
                var created = c.Arg<IReadOnlyList<IReadOnlyDictionary<long, object?>>>().Select(_ => Guid.NewGuid()).ToList();
                foreach (var id in created) ids[id] = nextId++;
                return (IReadOnlyList<Guid>)created;
            });
        harness.Records.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlyDictionary<Guid, long>)c.Arg<IReadOnlyCollection<Guid>>().ToDictionary(g => g, g => ids[g]));
        var createdByRow = new List<Guid>();
        long rowId = 500;
        harness.Records.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(),
            Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>())
            .Returns(_ => { var g = Guid.NewGuid(); createdByRow.Add(g); return g; });
        harness.Records.GetActiveRecordIdByPublicIdAsync(Arg.Any<AppTable>(), Arg.Any<Guid>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(_ => rowId++);

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(1, Calls(harness, "CreateManyAsync"));   // the batch was tried, found to depend on row order and thrown away
        Assert.Equal(1, Calls(harness, "CreateAsync"));
        Assert.Equal(1, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("UpdatedCount").GetInt32());
        await harness.Writes.Received(1).ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), createdByRow[0],
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction>(), false, null);
    }

    [Fact]
    public async Task Batch_EncryptedMergeFieldUsesTheInMemoryIndexAndStillWritesPages()
    {
        using var harness = BatchHarness(3);
        harness.DestinationField.IsEncrypted = true;
        var existing = Guid.NewGuid();
        harness.Records.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, Arg.Any<int>(), Arg.Any<FilterGroup>(), null, null, Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["f_9"] = "key2", ["PublicId"] = existing } });

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(2, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("UpdatedCount").GetInt32());
        Assert.Equal(0, Calls(harness, "FindRecordsByFieldValuesAsync"));
        Assert.Equal(1, Calls(harness, "CreateManyAsync"));
    }

    [Fact]
    public async Task Batch_SearchIndexUpdatesAreQueuedOncePerPageAfterTheCommit()
    {
        using var harness = BatchHarness(3);
        harness.Records.CreateManyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>())
            .Returns(c =>
            {
                var created = c.Arg<IReadOnlyList<IReadOnlyDictionary<long, object?>>>().Select(_ => Guid.NewGuid()).ToList();
                var callback = c.Arg<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>();
                foreach (var id in created) callback(new PowerBase.Application.Common.Models.SearchIndexMessage { RecordPublicId = id });
                return (IReadOnlyList<Guid>)created;
            });

        await harness.Run();

        var published = harness.Publisher.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "PublishBatchAsync").ToList();
        Assert.Single(published);
        Assert.Equal(3, ((IEnumerable<PowerBase.Application.Common.Models.SearchIndexMessage>)published[0].GetArguments()[0]!).Count());
    }

    [Fact]
    public async Task Batch_NoPipelineListensToTheDestination_TriggersAreAskedOncePerPageAndNeverFiredPerRecord()
    {
        using var harness = BatchHarness(600);   // 3 pages
        harness.Interceptor.HasActiveSubscriptionsAsync(Arg.Any<AppTable>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(600, result.GetProperty("InsertedCount").GetInt32());
        await harness.Interceptor.Received(3).HasActiveSubscriptionsAsync(Arg.Any<AppTable>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(harness.Interceptor.ReceivedCalls(), c => c.GetMethodInfo().Name == "InterceptAsync");
    }

    [Fact]
    public async Task RowByRow_NoPipelineListensToTheDestination_TriggersAreAskedOncePerPageAndNeverFiredPerRecord()
    {
        using var harness = new Harness();   // a unique field besides the merge field keeps it on the row-by-row path
        harness.SourceValues.Clear();
        harness.SourceValues.AddRange(Enumerable.Range(0, 300).Select(i => "key" + i));   // 2 pages
        harness.Interceptor.HasActiveSubscriptionsAsync(Arg.Any<AppTable>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = JsonDocument.Parse(await harness.Run()).RootElement;

        Assert.Equal(300, result.GetProperty("InsertedCount").GetInt32());
        Assert.Equal(300, Calls(harness, "CreateAsync"));
        await harness.Interceptor.Received(2).HasActiveSubscriptionsAsync(Arg.Any<AppTable>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(harness.Interceptor.ReceivedCalls(), c => c.GetMethodInfo().Name == "InterceptAsync");
    }

    [Fact]
    public async Task RowByRow_SomePipelineListens_EveryCreatedRecordFiresItsEvent()
    {
        using var harness = new Harness();
        harness.SourceValues.Clear();
        harness.SourceValues.AddRange(Enumerable.Range(0, 5).Select(i => "key" + i));

        await harness.Run();

        Assert.Equal(5, harness.Interceptor.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "InterceptAsync"));
    }

    private sealed class Harness : IDisposable
    {
        public CopyRecordsDefinition Config { get; } = CopyRecordsTests.Config();
        public AppField DestinationField { get; } = Field(9, "Destination key", unique: true);
        public IRecordRepository Records { get; } = Substitute.For<IRecordRepository>();
        public IRecordWriteService Writes { get; } = Substitute.For<IRecordWriteService>();
        public IPipelineRecordSearchService Search { get; } = Substitute.For<IPipelineRecordSearchService>();
        public IAzureSearchService AzureSearch { get; } = Substitute.For<IAzureSearchService>();
        public IAuditRepository Audit { get; } = Substitute.For<IAuditRepository>();
        public IAppTableRepository Tables { get; } = Substitute.For<IAppTableRepository>();
        public IPipelineTriggerInterceptor Interceptor { get; } = Substitute.For<IPipelineTriggerInterceptor>();
        public IMessagePublisher Publisher { get; } = Substitute.For<IMessagePublisher>();
        public List<string> SourceValues { get; } = new() { "none" };
        public List<AppField> SourceFields { get; } = new() { Field(6, "Source key") };
        public List<AppField> DestinationFields { get; } = new();
        public AppTable Source { get; }
        /// <summary>Lets a test give a source column a value other than the row's own (fid, row value -> column value), e.g. to repeat a unique value.</summary>
        public Func<int, string, string>? ColumnValue { get; set; }
        /// <summary>Every receipt written so far, by the hex of its execution-path hash.</summary>
        public Dictionary<string, string> Logs { get; } = new();
        public IPipelineStepIdempotencyRepository Idempotency { get; } = Substitute.For<IPipelineStepIdempotencyRepository>();
        public PowerBase.Application.Relationships.IRelationalProjector RelationalProjector { get; } = Substitute.For<PowerBase.Application.Relationships.IRelationalProjector>();
        public PowerBase.Application.Formulas.IFormulaProjector FormulaProjector { get; } = Substitute.For<PowerBase.Application.Formulas.IFormulaProjector>();
        private readonly ServiceProvider provider;
        private readonly Guid step = Guid.NewGuid(), message = Guid.NewGuid();
        public Harness()
        {
            var tables = Tables;
            Interceptor.HasActiveSubscriptionsAsync(Arg.Any<AppTable>(), Arg.Any<CancellationToken>()).Returns(true);   // someone listens, unless a test says otherwise
            var fields = Substitute.For<IAppFieldRepository>();
            Records.FindRecordsByFieldValuesAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(new List<(object? Value, Guid PublicId)>());
            Records.GetExistingValueIndexesAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<IReadOnlyList<object>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(_ => (IReadOnlySet<int>)new HashSet<int>());   // nothing is taken, unless a test says otherwise
            Records.GetAllValuesForUniqueCheckAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(_ => (IReadOnlyList<string>)new List<string>());
            Records.CreateManyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(),
                Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>>())
                .Returns(c => (IReadOnlyList<Guid>)c.Arg<IReadOnlyList<IReadOnlyDictionary<long, object?>>>().Select(_ => Guid.NewGuid()).ToList());
            var source = Source = new AppTable { Id = 1, AppId = 1, PublicId = Guid.Parse(Config.SourceTable) };
            var destination = new AppTable { Id = 2, AppId = 1, PublicId = Guid.Parse(Config.DestinationTable) };
            tables.GetByPublicIdAsync(source.PublicId, Arg.Any<CancellationToken>()).Returns(source);
            tables.GetByPublicIdAsync(destination.PublicId, Arg.Any<CancellationToken>()).Returns(destination);
            DestinationFields.Add(DestinationField);
            // A Reference field that points back at the destination itself (never mapped) keeps these tests on the row-by-row path, because such a
            // field can tie one row to another written in the same page; BatchHarness removes it.
            DestinationFields.Add(new AppField { Id = RowByRowGuardFid + 100, Fid = RowByRowGuardFid, Name = "Row-by-row guard", TypeCode = "Reference", Settings = "{\"parentTableId\":2}" });
            fields.ListByTableAsync(1, Arg.Any<CancellationToken>()).Returns(_ => SourceFields);
            fields.ListByTableAsync(2, Arg.Any<CancellationToken>()).Returns(_ => DestinationFields);
            var enforcer = Substitute.For<IRolePermissionEnforcer>();
            enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>()).Returns(new TableAccessContext { Unrestricted = true });
            var idempotency = Idempotency;
            var logs = Logs;
            idempotency.GetByExecutionKeyAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<byte[]>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(c => logs.GetValueOrDefault(Convert.ToHexString(c.Arg<byte[]>())));
            idempotency.InsertAsync(Arg.Any<PipelineStepIdempotencyLog>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(c => { var log = c.Arg<PipelineStepIdempotencyLog>(); logs.Add(Convert.ToHexString(log.ExecutionPathHash), log.OutputJson); return Task.CompletedTask; });
            idempotency.GetManyByExecutionKeysAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<byte[]>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(c => (IReadOnlyDictionary<string, string>)c.Arg<IReadOnlyCollection<byte[]>>()
                    .Select(Convert.ToHexString).Where(logs.ContainsKey).ToDictionary(h => h, h => logs[h]));
            idempotency.InsertManyAsync(Arg.Any<IReadOnlyCollection<PipelineStepIdempotencyLog>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(c =>
                {
                    foreach (var log in c.Arg<IReadOnlyCollection<PipelineStepIdempotencyLog>>()) logs.Add(Convert.ToHexString(log.ExecutionPathHash), log.OutputJson);
                    return Task.CompletedTask;
                });
            var encryption = Substitute.For<IEncryptionService>();
            encryption.GenerateAndWrapDekAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns("key");
            encryption.EncryptDataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(c => c.ArgAt<string>(0));
            encryption.DecryptDataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(c => c.ArgAt<string>(0));
            Search.ReadCopySnapshotAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>()).Returns(_ => Page());
            Records.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FilterGroup>(), null, null, Arg.Any<CancellationToken>()).Returns(Array.Empty<IReadOnlyDictionary<string, object?>>());
            var relationalProjector = RelationalProjector;
            relationalProjector.ProjectAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
                .Returns(c => c.Arg<IReadOnlyList<IReadOnlyDictionary<string, object?>>>()
                    .Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());
            var formulaProjector = FormulaProjector;
            formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable>())
                .Returns(c => c.Arg<IReadOnlyList<IReadOnlyDictionary<string, object?>>>()
                    .Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());
            provider = new ServiceCollection().AddSingleton(tables).AddSingleton(fields).AddSingleton(Records).AddSingleton(Writes).AddSingleton(Search)
                .AddSingleton(Substitute.For<IAppAccessService>()).AddSingleton(enforcer).AddSingleton(Substitute.For<IQueryContext>())
                .AddSingleton(idempotency).AddSingleton(Substitute.For<ITenantUnitOfWork>()).AddSingleton(encryption)
                .AddSingleton(Substitute.For<IUserRepository>()).AddSingleton(Audit)
                .AddSingleton(Substitute.For<IRelationshipRepository>())
                .AddSingleton(relationalProjector).AddSingleton(formulaProjector).AddSingleton(AzureSearch)
                .AddSingleton(Interceptor).AddSingleton(Publisher).AddSingleton<FormulaEngine>().BuildServiceProvider();
        }
        private async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Page()
        {
            await Task.CompletedTask;
            foreach (var page in SourceValues.Chunk(250))
                yield return page.Select(value => {
                    var row = SourceFields.ToDictionary(f => PhysicalNaming.GetPhysicalColumnName(f), f => (object?)(ColumnValue?.Invoke(f.Fid!.Value, value) ?? value));
                    row["PublicId"] = Guid.NewGuid();
                    return (IReadOnlyDictionary<string, object?>)row;
                }).ToList();
        }
        public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> EmptyPage()
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<string> Run() => Run("");
        public Task<string> Run(string query) => new CopyRecordsExecutor(provider).ExecuteAsync(Config, query, step, message, "root", default);
        public void Dispose() => provider.Dispose();
    }
}
