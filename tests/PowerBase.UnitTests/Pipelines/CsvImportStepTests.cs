using System.Text;
using System.Text.Json;
using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>The Quickbase-style Import with CSV step: reading the CSV, building the imports, and what it reports.</summary>
public class CsvImportStepTests
{
    private static CsvImportDefinition Config(string? json = null) => CsvImportDefinition.Read(json ?? """
        { "app": "1239d978-d5e8-f111-bc01-002324be71d7", "tableLabel": "1639d978-d5e8-f111-bc01-002324be71d7", "mergeField": "fid_3",
          "csvUrl": "https://x.test/a.csv", "headerRow": "Key,Text", "skipFirstLine": true,
          "columnMappings": [ { "column": "Key", "field": "fid_6" }, { "column": "Text", "field": "fid_7" } ] }
        """);

    [Fact]
    public void Parse_CommaAndDoubleQuotes_FollowsPythonCsvRules()
    {
        var rows = CsvImportFile.Parse("a,\"b, c\",\"say \"\"hi\"\"\",\"l1\nl2\"\r\n\n\nx,y\n", ',', '"');
        rows.Should().HaveCount(2);
        rows[0].Should().Equal("a", "b, c", "say \"hi\"", "l1\nl2");
        rows[1].Should().Equal("x", "y");
    }

    [Theory]
    [InlineData(";", "a;b", "a", "b")]
    [InlineData("\\t", "a\tb", "a", "b")]
    [InlineData(",", "a,b", "a", "b")]
    public void Delimiter_RowSeparatorChoices(string separator, string text, string first, string second)
    {
        var config = Config();
        config.RowSeparator = separator;
        CsvImportFile.Parse(text, config.Delimiter, config.QuoteChar)[0].Should().Equal(first, second);
    }

    [Theory]
    [InlineData("'", "k,'a, b ''q'''", "a, b 'q'")]
    [InlineData("|", "k,|x, y|", "x, y")]
    public void QuoteChar_SingleQuoteAndPipe(string quote, string text, string expected)
    {
        var config = Config();
        config.RowQuoteChar = quote;
        CsvImportFile.Parse(text, config.Delimiter, config.QuoteChar)[0][1].Should().Be(expected);
    }

    [Fact]
    public void Parse_ByteOrderMark_IsNotPartOfTheFirstValue() =>
        CsvImportFile.Parse("﻿Key,Text\n", ',', '"')[0][0].Should().Be("Key");

    [Fact]
    public void Prepare_FirstRowIsFieldNames_SkipsIt_AndKeepsAtMost10000()
    {
        var text = "Key,Text\n" + string.Join("\n", Enumerable.Range(1, 10_005).Select(i => $"K{i},t"));
        var rows = CsvImportFile.Prepare(text, Config());
        rows.TotalRows.Should().Be(10_005);
        rows.Rows.Should().HaveCount(CsvImportFile.MaxRecords);
        rows.Rows[0][0].Should().Be("K1");
    }

    [Fact]
    public void Prepare_FirstRowNotFieldNames_ImportsIt()
    {
        var config = Config();
        config.SkipFirstLine = JsonDocument.Parse("false").RootElement;
        CsvImportFile.Prepare("Key,Text\nK1,t\n", config).Rows.Should().HaveCount(2);
    }

    [Fact]
    public void SkipFirstLine_AcceptsBooleanOrQuickbaseString()
    {
        Config("""{ "skipFirstLine": "true" }""").FirstRowIsFieldNames.Should().BeTrue();
        Config("""{ "skipFirstLine": true }""").FirstRowIsFieldNames.Should().BeTrue();
        Config("""{ "skipFirstLine": false }""").FirstRowIsFieldNames.Should().BeFalse();
        Config("""{ }""").FirstRowIsFieldNames.Should().BeFalse();
    }

    [Fact]
    public void FieldsByColumn_BlankMappingSkipsTheColumn_AndFidFormsAreRead()
    {
        var config = Config("""{ "columnMappings": [ { "column": "a", "field": "fid_6" }, { "column": "b", "field": "" }, { "column": "c", "field": "8" } ] }""");
        config.FieldsByColumn(["a", "b", "c", "d"]).Should().BeEquivalentTo(new Dictionary<int, int> { [0] = 6, [2] = 8 });
    }

    [Fact]
    public void FieldsByColumn_ObjectShapeByColumnName_AsTheEditorSavesIt()
    {
        var config = Config("""{ "columnMappings": { "Name": "fid_6", "Skip": "", "Status": "fid_8" } }""");
        config.FieldsByColumn(["name", "Skip", "Status"]).Should().BeEquivalentTo(new Dictionary<int, int> { [0] = 6, [2] = 8 });
    }

    [Fact]
    public void FieldsByColumn_SameNameTwice_EachHasItsOwnMapping_AsQuickbaseKeysThem()
    {
        var config = Config("""{ "columnMappings": { "Name": "fid_6", "Name_0": "fid_7", "Name_1": "" } }""");
        CsvImportDefinition.ColumnKeys(["Name", "Key", "name", "Name"]).Should().Equal("Name", "Key", "name_0", "Name_1");
        config.FieldsByColumn(["Name", "Name", "Name"]).Should().BeEquivalentTo(new Dictionary<int, int> { [0] = 6, [1] = 7 });
    }

    [Fact]
    public void Validate_MissingOptions_GivesQuickbaseValidationError()
    {
        var act = () => Config("""{ }""").Validate();
        act.Should().Throw<PipelineNonRetryableException>().WithMessage("Validation error: {*'merge_field': 'required field'*'csv_url': 'required field'*'header_row': 'required field'}");
    }

    [Fact]
    public void BuildParts_MergeColumnNotMapped_AddsEveryRowByPosition()
    {
        var config = Config();
        var header = CsvImportDefinition.HeaderNames(config.HeaderRow);
        var parts = CsvImportFile.BuildParts(config, header, config.FieldsByColumn(header), [["K1", "t1"], ["K2", "t2"]]);
        parts.Should().ContainSingle();
        var import = parts[0].Import;
        import.ImportType.Should().Be(ImportTypes.Copy);
        import.File!.ColumnBy.Should().Be(ImportSourceKinds.ColumnByPosition);
        import.File.Columns.Should().Equal("Key", "Text");
        import.Mappings.Select(m => (m.DestFid, m.SourceFid)).Should().Equal((6, ImportFileColumns.FidOf(1)), (7, ImportFileColumns.FidOf(2)));
        Encoding.UTF8.GetString(parts[0].Csv).Should().Be("\"Key\",\"Text\"\r\n\"K1\",\"t1\"\r\n\"K2\",\"t2\"\r\n");
    }

    [Fact]
    public void BuildParts_MergeOnUniqueField_RowsWithAKeyMerge_BlankKeysAreAdded()
    {
        var config = Config();
        config.MergeField = "fid_6";
        var header = CsvImportDefinition.HeaderNames(config.HeaderRow);
        var parts = CsvImportFile.BuildParts(config, header, config.FieldsByColumn(header), [["K1", "a"], ["", "b"], ["K3", "c"]]);
        parts.Should().HaveCount(2);
        parts[0].Import.ImportType.Should().Be(ImportTypes.Merge);
        parts[0].Import.MergeKeyFid.Should().Be(6);
        parts[0].Rows.Should().Be(2);
        parts[1].Import.ImportType.Should().Be(ImportTypes.Copy);
        parts[1].Rows.Should().Be(1);
    }

    [Fact]
    public void BuildParts_MergeOnRecordId_RowsWithoutOneAreAddedWithoutTheRecordId()
    {
        var config = Config("""{ "mergeField": "fid_3", "columnMappings": [ { "column": "RID", "field": "fid_3" }, { "column": "Text", "field": "fid_7" } ] }""");
        string[] header = ["RID", "Text"];
        var parts = CsvImportFile.BuildParts(config, header, config.FieldsByColumn(header), [["2", "u"], ["", "n"]], RecordIdFields);
        parts[0].Import.MergeKeyFid.Should().Be(3);
        parts[1].Import.Mappings.Select(m => m.DestFid).Should().Equal(7);
    }

    /// <summary>The Record ID# is found from the table's fields (the built-in field), never by a fixed field id: here it is fid 101 and
    /// fid 3 is an ordinary unique field.</summary>
    [Fact]
    public void RecordId_IsReadFromTheFields_NotAssumedToBeFid3()
    {
        var fields = new Dictionary<int, CsvImportFile.FieldInfo>
        {
            [101] = new("Number", "Record ID#", false, IsRecordId: true), [3] = new("Text", "Code", true), [7] = new("Text", "Text", false)
        };
        CsvImportFile.RecordIdFid(fields).Should().Be(101);
        CsvImportFile.RecordIdFid(new Dictionary<int, CsvImportFile.FieldInfo>()).Should().BeNull();

        // Merge on the real Record ID# (101) with no column for it: every row is added.
        var onRecordId = Config();
        onRecordId.MergeField = "fid_101";
        var ok = () => CsvImportFile.CheckBeforeImport(onRecordId, new Dictionary<int, int> { [0] = 7 }, [["a"]], fields);
        ok.Should().NotThrow();
        // Merge on fid 3, which here is not the Record ID#: not mapped, so the Quickbase error.
        var onCode = Config();
        onCode.MergeField = "fid_3";
        var fails = () => CsvImportFile.CheckBeforeImport(onCode, new Dictionary<int, int> { [0] = 7 }, [["a"]], fields);
        fails.Should().Throw<PipelineNonRetryableException>().WithMessage("Invalid input; The merge field is missing from your import data.; Error Code: 2*");
        // fid 3 is unique here, so its duplicates are checked (the Record ID# column never is).
        var dup = () => CsvImportFile.CheckBeforeImport(onCode, new Dictionary<int, int> { [0] = 3, [1] = 7 }, [["C1", "a"], ["C1", "b"]], fields);
        dup.Should().Throw<PipelineNonRetryableException>().WithMessage("*Field name: Code*");
        // Rows without a Record ID# are added without the Record ID# column (101); fid 3 stays mapped.
        var parts = CsvImportFile.BuildParts(onRecordId, ["RID", "Code"], new Dictionary<int, int> { [0] = 101, [1] = 3 }, [["5", "C1"], ["", "C2"]], fields);
        parts[0].Import.MergeKeyFid.Should().Be(101);
        parts[1].Import.Mappings.Select(m => m.DestFid).Should().Equal(3);
    }

    [Fact]
    public void HeaderNames_SplitWithTheRowSeparator_AsQuickbaseDoes()
    {
        CsvImportDefinition.HeaderNames("Key;Name", ';').Should().Equal("Key", "Name");
        CsvImportDefinition.HeaderNames("Key,Name", ';').Should().Equal("Key,Name");
        CsvImportDefinition.HeaderNames("Key\tName", '\t').Should().Equal("Key", "Name");
    }

    [Theory]
    [InlineData("abc", "Number", "")]
    [InlineData("$1,234.50", "Currency", "1234.5")]
    [InlineData("1e3", "Number", "1000")]
    [InlineData("-7", "Number", "-7")]
    [InlineData("maybe", "Boolean", "false")]
    [InlineData("yes", "Boolean", "true")]
    [InlineData("1", "Boolean", "true")]
    [InlineData("0", "Boolean", "false")]
    [InlineData("notadate", "Date", "")]
    [InlineData("2025-01-31", "Date", "01-31-2025")]
    [InlineData("1/31/2025", "Date", "01-31-2025")]
    [InlineData("31-01-2025", "Date", "01-31-2025")]
    [InlineData("02-03-2025", "Date", "02-03-2025")]
    [InlineData("20250131", "Date", "01-01-1970")]
    [InlineData("2025-02-30", "Date", "02-28-2025")]
    [InlineData("13/13/2025", "Date", "")]
    [InlineData("31.01.2025", "Date", "")]
    [InlineData("January 31, 2025", "Date", "01-31-2025")]
    [InlineData("notanemail", "Email", "notanemail")]
    public void CleanValue_StoresWhatQuickbaseStores(string value, string type, string expected) =>
        CsvImportFile.CleanValue(value, type).Should().Be(expected);

    private static readonly Dictionary<int, CsvImportFile.FieldInfo> KeyFields = new() { [6] = new("Text", "Key", true), [7] = new("Text", "Text", false) };
    private static readonly Dictionary<int, CsvImportFile.FieldInfo> RecordIdFields = new()
    {
        [3] = new("Number", "Record ID#", false, IsRecordId: true), [6] = new("Text", "Key", true), [7] = new("Text", "Text", false)
    };

    [Fact]
    public void CheckBeforeImport_DuplicateUniqueValues_FailsBeforeImporting()
    {
        var config = Config();
        config.MergeField = "fid_6";
        var act = () => CsvImportFile.CheckBeforeImport(config, new Dictionary<int, int> { [0] = 6, [1] = 7 }, [["B20", "a"], ["B20", "b"]], KeyFields);
        act.Should().Throw<PipelineNonRetryableException>()
            .WithMessage("Quickbase reported an error: Trying to add a non-unique value to a field marked unique; Your import data has duplicate values for the following unique fields\n\nField name: Key\nDuplicate values:\n\t\tB20; Error Code: 51\n-- Processed: 0 - Added: 0 - Updated: 0 - Unchanged: 0");
    }

    [Fact]
    public void CheckBeforeImport_MergeFieldNotMapped_Fails()
    {
        var config = Config();
        config.MergeField = "fid_6";
        var act = () => CsvImportFile.CheckBeforeImport(config, new Dictionary<int, int> { [0] = 7 }, [["a"]], KeyFields);
        act.Should().Throw<PipelineNonRetryableException>().WithMessage("Invalid input; The merge field is missing from your import data.; Error Code: 2*");
    }

    /// <summary>Quickbase, 2026-11-27 (pipeline 6038216900722688): merge field Record ID#, no column mapped to it → success, every row added.</summary>
    [Fact]
    public void CheckBeforeImport_MergeOnRecordIdNotMapped_ImportsEveryRowAsNew()
    {
        var config = Config();
        config.MergeField = "fid_3";
        var act = () => CsvImportFile.CheckBeforeImport(config, new Dictionary<int, int> { [0] = 6 }, [["John"], ["Jack"]], RecordIdFields);
        act.Should().NotThrow();
        var parts = CsvImportFile.BuildParts(config, ["First", "Last"], new Dictionary<int, int> { [0] = 6 }, [["John", "Doe"], ["Jack", "M"]], RecordIdFields);
        parts.Should().ContainSingle().Which.Import.MergeKeyFid.Should().BeNull();
        parts[0].Rows.Should().Be(2);
    }

    /// <summary>Quickbase, 2026-11-27: of 6 CSV rows the one whose mapped column (First) was empty was not imported (processed 5, added 5).</summary>
    [Fact]
    public void WithMappedValues_RowWithEveryMappedColumnEmpty_IsLeftOut()
    {
        var rows = new List<string[]> { new[] { "John", "Doe" }, new[] { "", "Blankman" }, new[] { "  ", "x" }, new[] { "Joan", "" } };
        CsvImportFile.WithMappedValues(rows, new Dictionary<int, int> { [0] = 6 }).Select(r => r[0]).Should().Equal("John", "Joan");
        CsvImportFile.WithMappedValues(rows, new Dictionary<int, int> { [0] = 6, [1] = 7 }).Should().HaveCount(4);
    }

    private static ImportRunStatusResponse Status(string status, long read, long inserted, long updated = 0, long unchanged = 0, long errored = 0) =>
        new(Guid.NewGuid(), status, 100, read, inserted, updated, 0, errored, null, null, null, false, unchanged);

    [Fact]
    public void Outcome_Success_GivesQuickbaseOutputKeys()
    {
        var json = PipelineEngine.CsvImportOutcome([Status("success", 3, 1, 1, 1)], 3);
        json.Should().Be("""{"records_processed":3,"records_added":1,"records_updated":1,"records_unchanged":1}""");
        CsvImportCounts.None.Output().Should().Be("""{"records_processed":0,"records_added":0,"records_updated":0,"records_unchanged":0}""");
    }

    [Fact]
    public void Outcome_RowsRejected_FailsWithReasonAndCounts()
    {
        var act = () => PipelineEngine.CsvImportOutcome([Status("partial", 2, 1, errored: 1)], 2, "'x' is not a valid number for 'N'.");
        act.Should().Throw<PipelineNonRetryableException>()
            .WithMessage("Invalid input: 'x' is not a valid number for 'N'. 1 of 2 records could not be imported*\n-- Processed: 1 - Added: 1 - Updated: 0 - Unchanged: 0");
    }

    [Fact]
    public void Outcome_MoreThan10000_FailsAfterImportingTheFirst10000()
    {
        var act = () => PipelineEngine.CsvImportOutcome([Status("success", 10_000, 10_000)], 10_005);
        act.Should().Throw<PipelineNonRetryableException>().WithMessage("*maximum of 10000 records*\n-- Processed: 10000 - Added: 10000 - Updated: 0 - Unchanged: 0");
    }
}
