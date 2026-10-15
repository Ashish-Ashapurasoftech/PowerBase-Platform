using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>A date in a file may be written any way; the app's date format only settles which of day and month comes first when both would do.</summary>
public class ImportDateParserTests
{
    private static DateTime? Read(string text, string? format = null, bool withTime = false) =>
        ImportDateParser.TryParse(text, format, withTime, out var value) ? value : null;

    [Theory]
    [InlineData("2026-04-05")]
    [InlineData("2026/04/05")]
    [InlineData("2026.04.05")]
    [InlineData("5-Apr-2026")]
    [InlineData("05 April 2026")]
    [InlineData("April 5, 2026")]
    [InlineData("Apr 5 2026")]
    [InlineData("Sun, 5 Apr 2026")]
    [InlineData("2026-04-05T00:00:00.0000000Z")]
    [InlineData("2026-04-05 14:30")]
    [InlineData("  2026-04-05  ")]
    public void Any_way_of_writing_the_day_is_read_as_that_day(string text) => Read(text).Should().Be(new DateTime(2026, 4, 5));

    [Fact]
    public void Where_day_and_month_could_swap_the_apps_format_decides()
    {
        Read("05-04-2026", "DD-MM-YYYY").Should().Be(new DateTime(2026, 4, 5));
        Read("05-04-2026", "MM-DD-YYYY").Should().Be(new DateTime(2026, 5, 4));
        Read("05/04/26", "DD/MM/YY").Should().Be(new DateTime(2026, 4, 5));
        Read("05.04.2026", "DD-MM-YYYY").Should().Be(new DateTime(2026, 4, 5));
        Read("05-04-2026", null).Should().Be(new DateTime(2026, 5, 4), "month first when nothing is known");
        Read("05-04-2026", "YYYY-MM-DD").Should().Be(new DateTime(2026, 5, 4));
    }

    [Fact]
    public void A_date_that_cannot_be_the_apps_order_is_read_the_other_way()
    {
        Read("25-04-2026", "MM-DD-YYYY").Should().Be(new DateTime(2026, 4, 25));
        Read("04-25-2026", "DD-MM-YYYY").Should().Be(new DateTime(2026, 4, 25));
    }

    [Fact]
    public void A_date_and_time_keeps_its_time_and_is_utc_and_a_date_drops_it()
    {
        Read("05/04/2026 14:30", "DD-MM-YYYY", withTime: true).Should().Be(new DateTime(2026, 4, 5, 14, 30, 0, DateTimeKind.Utc));
        Read("05/04/2026 2:30 PM", "DD-MM-YYYY", withTime: true).Should().Be(new DateTime(2026, 4, 5, 14, 30, 0, DateTimeKind.Utc));
        Read("5-Apr-2026 09:05", withTime: true).Should().Be(new DateTime(2026, 4, 5, 9, 5, 0, DateTimeKind.Utc));
        Read("2026-04-05T14:30:00Z", withTime: false).Should().Be(new DateTime(2026, 4, 5));
        Read("05/04/2026 14:30", "DD-MM-YYYY", withTime: false).Should().Be(new DateTime(2026, 4, 5));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("31-02-2026")]
    [InlineData("13-13-2026")]
    [InlineData("45000")]
    [InlineData("2026")]
    [InlineData("5-Foo-2026")]
    [InlineData("05-04")]
    public void Anything_that_is_not_a_date_is_refused(string text) => Read(text, "DD-MM-YYYY").Should().BeNull();

    [Fact]
    public void A_conversion_reads_the_date_with_the_apps_format_and_names_what_it_could_not_read()
    {
        var date = ImportHarness.Field(11, 20, "Born", "Date");

        ImportTypeCompatibility.TryConvert("05-04-2026", date, out var value, out _, "DD-MM-YYYY").Should().BeTrue();
        value.Should().Be(new DateTime(2026, 4, 5));

        ImportTypeCompatibility.TryConvert("someday", date, out _, out var error, "DD-MM-YYYY").Should().BeFalse();
        error.Should().Contain("someday").And.Contain("Born");
    }

    [Fact]
    public async Task The_plan_carries_the_apps_date_format()
    {
        var apps = Substitute.For<IAppRepository>();
        apps.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new App { Formatting = "{\"date\":{\"formatString\":\"DD-MM-YYYY\"}}" });
        var cfg = new ImportDefinitionConfig
        {
            Name = "Copy", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy, Mappings = [new() { DestFid = 6, SourceFid = 6 }]
        };
        var h = ImportHarness.Create(cfg, new HarnessOptions { RowCount = 1, Apps = apps });

        var plan = await h.PlanBuilder.BuildAsync(cfg, ImportHarness.DestTableId, default);

        plan.DateFormat.Should().Be("DD-MM-YYYY");
    }
}
