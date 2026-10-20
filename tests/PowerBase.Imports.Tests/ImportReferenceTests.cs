using System.Text;
using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>A Reference field (the child side of a relationship, e.g. Student → Course) is a destination. A value from the source is the parent's key
/// (what the relationship shows for a record, e.g. the course's title) or its Record ID#; it is turned into the Record ID# the field stores. A value
/// that finds no parent record does not reject the row: the reference is left blank and the text is kept in the field's hidden "(original)" copy.</summary>
public class ImportReferenceTests
{
    private const long CoursesId = 12;
    private const int Course = 20;
    private const int Original = 30;
    private const int TitleFid = 6;
    private static readonly Guid CoursesPublicId = Guid.NewGuid();

    /// <param name="encrypted">The Title is encrypted (the app is): its values can only be read decrypted, never searched in the database.</param>
    /// <param name="keyed">The table has a key field set (Title).</param>
    /// <param name="titled">The table has a Title field at all (without a key set it is the field its records are labelled with).</param>
    private static HarnessDestination Courses(bool keyed = true, bool titled = true, bool encrypted = false)
    {
        var title = ImportHarness.Field(CoursesId, TitleFid, "Title", "Text");
        title.IsEncrypted = encrypted;
        return new()
        {
            Id = CoursesId, PublicId = CoursesPublicId, Name = "Courses",
            Fields = titled ? [ImportHarness.RecordId(CoursesId), title] : [ImportHarness.RecordId(CoursesId)],
            KeyFieldId = keyed ? CoursesId * 100 + TitleFid : null,
            // A field that is not encrypted is searched in the database (the substitute answers from this); an encrypted one is read, decrypted.
            KeyValues = encrypted ? new(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase) { ["Maths"] = 2, ["Physics"] = 3, ["101"] = 7 },
            Rows = encrypted ? [new Dictionary<string, object?> { ["Id"] = 2L, ["f_6"] = "Maths" }, new Dictionary<string, object?> { ["Id"] = 3L, ["f_6"] = "Physics" }] : null
        };
    }

    private static ImportDefinitionConfig Config(Action<ImportDefinitionConfig>? tweak = null)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "Students", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy,
            File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "Course"] },
            Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = Course, SourceFid = 1002 }]
        };
        tweak?.Invoke(cfg);
        return cfg;
    }

    /// <param name="withOriginal">The Reference was converted from a text field, so it has a hidden "(original)" field.</param>
    private static ImportHarness Run(string csv, string? settings = "ORIGINAL", bool keyed = true, bool titled = true, bool encrypted = false, bool required = false, Action<ImportDefinitionConfig>? tweak = null)
    {
        var h = ImportHarness.Create(Config(tweak), new HarnessOptions
        {
            UploadedFile = Encoding.UTF8.GetBytes(csv),
            ExtraDestinations = [Courses(keyed, titled, encrypted)],
            ConfigureDestination = fields =>
            {
                var reference = ImportHarness.Field(11, Course, "Course", "Reference", required: required);
                reference.Settings = settings == "ORIGINAL" ? $"{{\"parentTableId\":12,\"originalFid\":{Original}}}" : settings;
                fields.Add(reference);
                var original = ImportHarness.Field(11, Original, "Course (original)", "Text");
                original.IsHidden = true;
                fields.Add(original);
            }
        });
        h.Store.ExistingRecordIds[CoursesId] = [1, 2, 3];
        return h;
    }

    /// <summary>Makes (and counts the making of) the hidden "(original)" field for a Reference that has none.</summary>
    private sealed class FakeOriginals(bool allowed = true) : PowerBase.Application.Imports.IReferenceOriginalFields
    {
        public int Calls;
        public const int MadeFid = 31;
        public Task<AppField?> EnsureAsync(AppTable child, AppField reference, CancellationToken ct)
        {
            Calls++;
            if (!allowed) return Task.FromResult<AppField?>(null);
            var field = ImportHarness.Field(child.Id, MadeFid, "Course (original)", "Text");
            field.IsHidden = true;
            return Task.FromResult<AppField?>(field);
        }
    }

    private static ImportHarness RunWithMaking(string csv, FakeOriginals originals, Action<ImportDefinitionConfig>? tweak = null)
    {
        var h = ImportHarness.Create(Config(tweak), new HarnessOptions
        {
            UploadedFile = System.Text.Encoding.UTF8.GetBytes(csv), ExtraDestinations = [Courses()], ReferenceOriginals = originals,
            ConfigureDestination = fields =>
            {
                var reference = ImportHarness.Field(11, Course, "Course", "Reference");
                reference.Settings = "{\"parentTableId\":12}"; // created straight into a relationship: no hidden copy yet
                fields.Add(reference);
            }
        });
        h.Store.ExistingRecordIds[CoursesId] = [1, 2, 3];
        return h;
    }

    [Fact]
    public async Task A_reference_with_no_hidden_copy_gets_one_when_a_value_finds_no_parent_and_the_text_is_kept_in_it()
    {
        var made = new FakeOriginals();
        var h = RunWithMaking("Name,Course\nAda,Maths\nBen,Finance\nCy,Sales\n", made);

        await h.RunAsync();

        made.Calls.Should().Be(1, "made once, for the whole run");
        h.Store.Inserted.Single(r => Equals(r[6], "Ben"))[FakeOriginals.MadeFid].Should().Be("Finance");
        h.Store.Inserted.Single(r => Equals(r[6], "Cy"))[FakeOriginals.MadeFid].Should().Be("Sales");
        CourseOf(h.Store.Inserted.Single(r => Equals(r[6], "Ben"))).Should().BeNull();
        CourseOf(h.Store.Inserted.Single(r => Equals(r[6], "Ada"))).Should().Be(2L);
        h.DetailsFiles[0].Should().NotContain("was left blank", "the text is kept");
    }

    [Fact]
    public async Task Nothing_is_made_when_every_value_finds_its_parent()
    {
        var made = new FakeOriginals();
        var h = RunWithMaking("Name,Course\nAda,Maths\nBen,Physics\n", made);

        await h.RunAsync();

        made.Calls.Should().Be(0);
    }

    [Fact]
    public async Task When_the_hidden_copy_cannot_be_made_the_row_is_still_imported_and_the_details_file_says_the_text_was_not_kept()
    {
        var made = new FakeOriginals(allowed: false);
        var h = RunWithMaking("Name,Course\nAda,Finance\n", made);

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.DetailsFiles[0].Should().Contain("was left blank");
    }

    [Fact]
    public async Task A_check_that_runs_before_the_import_makes_nothing()
    {
        var made = new FakeOriginals();
        var h = RunWithMaking("Name,Course\nAda,Finance\n", made, c => c.ConstraintPolicy = ImportConstraintPolicy.AbortIfAnyIssue);

        await h.RunAsync();

        made.Calls.Should().Be(1, "only the real run makes it, not the check before it");
        h.Store.Inserted.Single()[FakeOriginals.MadeFid].Should().Be("Finance");
    }

    [Fact]
    public void The_reference_settings_keep_everything_else_when_the_hidden_copy_is_added()
    {
        var patched = PowerBase.Application.Imports.ReferenceOriginalFields.WithOriginal("{\"relationshipId\":5,\"parentTableId\":12,\"OriginalFid\":9}", 31);

        var node = System.Text.Json.Nodes.JsonNode.Parse(patched)!.AsObject();
        ((int)node["originalFid"]!).Should().Be(31);
        ((int)node["parentTableId"]!).Should().Be(12);
        ((int)node["relationshipId"]!).Should().Be(5);
        node.Count.Should().Be(3, "the old spelling of the key is replaced, not kept beside the new one");
        PowerBase.Application.Imports.ReferenceOriginalFields.WithOriginal(null, 31).Should().Be("{\"originalFid\":31}");
    }

    private static object? CourseOf(IReadOnlyDictionary<long, object?> row) => row.GetValueOrDefault(Course);
    private static object? OriginalOf(IReadOnlyDictionary<long, object?> row) => row.GetValueOrDefault(Original);

    [Fact]
    public async Task A_value_that_is_the_key_of_a_parent_record_fills_the_reference_with_its_record_id()
    {
        var h = Run("Name,Course\nAda,Maths\nBen,physics\n");

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.Inserted.Select(CourseOf).Should().Equal(2L, 3L);
        h.Store.Inserted.Select(OriginalOf).Should().OnlyContain(v => v == null);
    }

    [Fact]
    public async Task A_value_that_is_the_record_id_of_a_parent_record_is_accepted_when_the_parent_has_no_key_field()
    {
        var h = Run("Name,Course\nAda,1\nBen,3.0\n", keyed: false);

        await h.RunAsync();

        h.Store.Inserted.Select(CourseOf).Should().Equal(1L, 3L);
    }

    [Fact]
    public async Task When_the_parent_has_a_key_field_a_value_is_looked_up_by_that_key_and_never_taken_for_a_record_id()
    {
        // "101" is the key of course 7; 2 is a live Record ID# but no course has the key "2", so it is not that record.
        var h = Run("Name,Course\nAda,101\nBen,2\n");

        await h.RunAsync();

        h.Store.Inserted.Select(CourseOf).Should().Equal(7L, null);
        OriginalOf(h.Store.Inserted.Single(r => Equals(r[6], "Ben"))).Should().Be("2");
        h.Store.ParentChecks.Should().Be(0, "no Record ID# is looked for when the relationship has a key");
    }

    [Fact]
    public async Task A_value_that_matches_no_parent_record_is_still_imported_with_the_text_kept_in_the_original_field()
    {
        var h = Run("Name,Course\nAda,Maths\nBen,Chemistry\n");

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.Inserted.Should().HaveCount(2);
        var ben = h.Store.Inserted.Single(r => Equals(r[6], "Ben"));
        CourseOf(ben).Should().BeNull();
        OriginalOf(ben).Should().Be("Chemistry");
        OriginalOf(h.Store.Inserted.Single(r => Equals(r[6], "Ada"))).Should().BeNull();
    }

    [Fact]
    public async Task A_reference_with_no_original_field_is_imported_blank_and_the_details_file_says_so()
    {
        var h = Run("Name,Course\nAda,Chemistry\n", settings: "{\"parentTableId\":12}");

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        CourseOf(h.Store.Inserted.Single()).Should().BeNull();
        h.DetailsFiles[0].Should().Contain("'Chemistry' matches no Courses record (by Title)").And.Contain("was left blank");
    }

    [Fact]
    public async Task A_parent_with_no_key_set_is_matched_by_the_field_its_records_are_labelled_with()
    {
        // The Course table of the business case: no key was ever set, but its records are shown by their name.
        var h = Run("Name,Course\nAda,Maths\nBen,2\n", keyed: false);

        await h.RunAsync();

        CourseOf(h.Store.Inserted.Single(r => Equals(r[6], "Ada"))).Should().Be(2L);
        CourseOf(h.Store.Inserted.Single(r => Equals(r[6], "Ben"))).Should().Be(2L);
    }

    [Fact]
    public async Task A_parent_with_no_field_to_look_a_value_up_in_is_matched_on_its_record_id_only()
    {
        var h = Run("Name,Course\nAda,Maths\nBen,2\n", keyed: false, titled: false);

        await h.RunAsync();

        CourseOf(h.Store.Inserted.Single(r => Equals(r[6], "Ada"))).Should().BeNull();
        OriginalOf(h.Store.Inserted.Single(r => Equals(r[6], "Ada"))).Should().Be("Maths");
        CourseOf(h.Store.Inserted.Single(r => Equals(r[6], "Ben"))).Should().Be(2L);
    }

    [Fact]
    public async Task A_key_that_is_encrypted_is_read_decrypted_and_searched_here_because_the_database_cannot_search_ciphertext()
    {
        // The app is encrypted: the course names are ciphertext in the database. Only the Record ID# is not.
        var h = Run("Name,Course\nAda,Maths\nBen,physics\nCy,Chemistry\n", encrypted: true);

        await h.RunAsync();

        h.Store.Inserted.Single(r => Equals(r[6], "Ada"))[Course].Should().Be(2L);
        h.Store.Inserted.Single(r => Equals(r[6], "Ben"))[Course].Should().Be(3L);
        var cy = h.Store.Inserted.Single(r => Equals(r[6], "Cy"));
        CourseOf(cy).Should().BeNull();
        OriginalOf(cy).Should().Be("Chemistry");
    }

    [Fact]
    public async Task A_blank_leaves_the_reference_empty_and_keeps_nothing()
    {
        var h = Run("Name,Course\nAda,\n");

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        CourseOf(h.Store.Inserted.Single()).Should().BeNull();
        OriginalOf(h.Store.Inserted.Single()).Should().BeNull();
    }

    [Fact]
    public async Task A_required_reference_that_finds_no_parent_rejects_the_row_like_any_missing_required_value()
    {
        var h = Run("Name,Course\nAda,Maths\nBen,Chemistry\n", required: true);

        await h.RunAsync();

        h.Store.Inserted.Should().ContainSingle();
        h.Issues.Should().ContainSingle(i => i.ReasonCode == ImportReason.RequiredMissing);
    }

    [Fact]
    public async Task A_parent_used_by_many_rows_is_looked_up_once()
    {
        var h = Run("Name,Course\n" + string.Join('\n', Enumerable.Range(1, 30).Select(i => $"s{i},1")) + "\n", keyed: false);

        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(30);
        h.Store.ParentChecks.Should().Be(1);
    }

    [Fact]
    public async Task A_fixed_value_can_name_the_parent_every_row_gets()
    {
        var h = Run("Name,Course\nAda,\nBen,\n",
            tweak: c => c.Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = Course, Source = ImportMappingSource.Static, StaticValue = "Maths" }]);

        await h.RunAsync();

        h.Store.Inserted.Select(CourseOf).Should().Equal(2L, 2L);
    }

    [Fact]
    public async Task A_reference_that_only_accepts_some_parent_records_is_refused()
    {
        var h = Run("Name,Course\nAda,1\n",
            settings: "{\"parentTableId\":12,\"filterTree\":\"{\\\"nodes\\\":[{\\\"condition\\\":{\\\"fieldId\\\":6,\\\"operator\\\":\\\"eq\\\",\\\"value\\\":\\\"x\\\"}}]}\"}");

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("only accepts some records");
        h.Store.Inserted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reference_that_is_not_connected_to_a_parent_table_is_refused()
    {
        var h = Run("Name,Course\nAda,1\n", settings: null);

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("not connected to a parent table");
    }

    [Fact]
    public void A_reference_can_be_written_but_never_be_what_records_are_matched_on_and_a_hidden_field_cannot_be_written()
    {
        var reference = ImportHarness.Field(11, Course, "Course", "Reference", unique: true);
        var hidden = ImportHarness.Field(11, Original, "Course (original)", "Text");
        hidden.IsHidden = true;

        ImportTypeCompatibility.IsWritable(reference).Should().BeTrue();
        ImportTypeCompatibility.IsKeyCandidate(reference).Should().BeFalse();
        ImportTypeCompatibility.IsWritable(hidden).Should().BeFalse("a hidden helper field is never offered as a destination");
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Number")]
    public void Text_and_numbers_can_fill_a_reference_but_other_types_cannot(string sourceType)
    {
        var source = ImportHarness.Field(10, 6, "Code", sourceType);
        var reference = ImportHarness.Field(11, Course, "Course", "Reference");

        ImportTypeCompatibility.CheckMapping(source, reference).Should().BeNull();
        ImportTypeCompatibility.CheckMapping(ImportHarness.Field(10, 7, "Flag", "Boolean"), reference).Should().NotBeNull();
        ImportTypeCompatibility.CheckMapping(ImportHarness.Field(10, 8, "On", "Date"), reference).Should().NotBeNull();
    }
}
