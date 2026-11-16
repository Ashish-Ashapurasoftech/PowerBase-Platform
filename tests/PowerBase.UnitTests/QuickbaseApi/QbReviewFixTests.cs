using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.Application.Reports;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

public class QbReviewFixTests
{
    private static FilterGroup One(FilterCondition c) => new() { Nodes = { new FilterNode { Condition = c } } };

    [Theory]
    [InlineData("today", null, "{8.OAF.'today'}")]
    [InlineData("yesterday", null, "{8.OAF.'yesterday'}")]
    [InlineData("tomorrow", null, "{8.OAF.'tomorrow'}")]
    public void Serialize_RelativeDayKeywords(string mode, string? value, string expected)
    {
        QbQuerySerializer.Serialize(One(new FilterCondition { FieldId = 8, Operator = "gte", ValueMode = mode, Value = value }), f => f == 8)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("during", "duringCurrent", "1:week", "{8.IR.'this week'}")]
    [InlineData("during", "duringPrevious", "2:month", "{8.IR.'last 2 months'}")]
    [InlineData("during", "duringNext", "1:day", "{8.IR.'next day'}")]
    [InlineData("notDuring", "duringPrevious", "1:year", "{8.XIR.'last year'}")]
    public void Serialize_DuringBecomesInRange(string op, string mode, string value, string expected)
    {
        QbQuerySerializer.Serialize(One(new FilterCondition { FieldId = 8, Operator = op, ValueMode = mode, Value = value }), f => f == 8)
            .Should().Be(expected);
    }

    [Fact]
    public void Serialize_AskTheUser_NumberedPrompts()
    {
        var tree = new FilterGroup
        {
            Nodes =
            {
                new FilterNode { Condition = new FilterCondition { FieldId = 6, Operator = "eq", ValueMode = "ask" } },
                new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "gt", ValueMode = "ask" } },
            },
        };
        QbQuerySerializer.Serialize(tree).Should().Be("{6.EX.'_ask1_'}AND{7.GT.'_ask2_'}");
    }

    [Fact]
    public void Serialize_FieldToField_UsesFidToken()
    {
        QbQuerySerializer.Serialize(One(new FilterCondition { FieldId = 6, Operator = "eq", ValueMode = "field", ValueFieldId = 9 }))
            .Should().Be("{6.EX._FID_9}");
    }

    [Fact]
    public void Serialize_DateLiteral_IsQuickbaseDateFormat_TextIsNot()
    {
        QbQuerySerializer.Serialize(One(new FilterCondition { FieldId = 8, Operator = "eq", Value = "2024-03-05" }), f => f == 8)
            .Should().Be("{8.EX.'03-05-2024'}");
        QbQuerySerializer.Serialize(One(new FilterCondition { FieldId = 6, Operator = "eq", Value = "2024-03-05" }))
            .Should().Be("{6.EX.'2024-03-05'}");
    }

    [Fact]
    public void Serialize_QuoteInValue_IsEscaped()
    {
        QbQuerySerializer.Serialize(One(new FilterCondition { FieldId = 6, Operator = "contains", Value = "O'Brien" }))
            .Should().Be("{6.CT.'O\\'Brien'}");
    }

    [Theory]
    [InlineData(90L, 5_400_000L)]
    [InlineData(1L, 60_000L)]
    public void Format_Duration_MinutesToMilliseconds(long minutes, long expectedMs)
    {
        QbValueFormatter.Format(minutes, "duration").Should().Be(expectedMs);
    }

    [Fact]
    public void SameValue_DateTimeVersusIsoString_IsSameInstant()
    {
        QbRecordService.SameValue(new DateTime(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc), "2026-11-04T10:00:00Z").Should().BeTrue();
        QbRecordService.SameValue(new DateTime(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc), "2026-11-04T11:00:00Z").Should().BeFalse();
    }
}
