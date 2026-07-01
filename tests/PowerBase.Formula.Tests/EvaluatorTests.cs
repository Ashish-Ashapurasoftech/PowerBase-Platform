using System.Globalization;
using FluentAssertions;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Tests;

public class EvaluatorTests
{
    [Theory]
    // Arithmetic + precedence quirks
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("2 ^ 3 ^ 2", "512")]      // right-associative
    [InlineData("-2 ^ 2", "4")]           // unary binds tighter than ^ → (-2)^2
    [InlineData("10 / 4", "2.5")]
    // Math functions
    [InlineData("Abs(-5)", "5")]
    [InlineData("Round(2.5)", "3")]
    [InlineData("Round(-3.5)", "-3")]     // halves round toward +∞
    [InlineData("Round(2.345, 2)", "2.35")]
    [InlineData("RoundDown(2.99)", "2")]
    [InlineData("RoundUp(2.01)", "3")]
    [InlineData("Int(-2.9)", "-2")]
    [InlineData("Mod(7, 3)", "1")]
    [InlineData("Sqrt(9)", "3")]
    [InlineData("Min(3, 1, 2)", "1")]
    [InlineData("Max(3, 1, 2)", "3")]
    [InlineData("Sum(1, 2, 3, 4)", "10")]
    [InlineData("Average(2, 4, 6)", "4")]
    [InlineData("Length(\"hello\")", "5")]
    [InlineData("ToNumber(\"42\")", "42")]
    // Logical / control flow returning numbers
    [InlineData("If(1 > 0, 10, 20)", "10")]
    [InlineData("If(1 < 0, 10, 20)", "20")]
    [InlineData("Nz(ToNumber(\"x\"), 99)", "99")]
    [InlineData("Case(2, 1, 10, 2, 20, 3, 30)", "20")]
    [InlineData("Case(5, 1, 10, 2, 20, 99)", "99")]
    // Date/duration extraction
    [InlineData("Year(ToDate(\"2026-06-11\"))", "2026")]
    [InlineData("Month(ToDate(\"2026-06-11\"))", "6")]
    [InlineData("Quarter(ToDate(\"2026-06-11\"))", "2")]
    [InlineData("ToDays(Hours(48))", "2")]
    [InlineData("DateDiff(ToDate(\"2026-06-11\"), ToDate(\"2026-06-01\"))", "10")]
    // Wave 1 — number
    [InlineData("Rem(7, 3)", "1")]
    [InlineData("Ceil(12, 5)", "15")]
    [InlineData("Ceil(10, 5)", "10")]
    [InlineData("Floor(12, 5)", "10")]
    [InlineData("Ceil(2.3)", "3")]            // default multiple 1
    [InlineData("Floor(2.9)", "2")]
    [InlineData("Ceil(-12, 5)", "-10")]       // toward +∞
    [InlineData("Floor(-12, 5)", "-15")]
    // Wave 1 — text returning numbers
    [InlineData("Find(\"hello world\", \"world\")", "7")]
    [InlineData("Find(\"hello\", \"xyz\")", "0")]
    // Wave 1 — date/time + duration
    [InlineData("DayOfYear(ToDate(\"2026-06-11\"))", "162")]
    [InlineData("Hour(ToTimestamp(\"2026-06-11 14:30:45\"))", "14")]
    [InlineData("Minute(ToTimestamp(\"2026-06-11 14:30:45\"))", "30")]
    [InlineData("Second(ToTimestamp(\"2026-06-11 14:30:45\"))", "45")]
    [InlineData("ToHours(Hours(2))", "2")]
    [InlineData("ToMinutes(Hours(1))", "60")]
    [InlineData("ToSeconds(Minutes(2))", "120")]
    [InlineData("ToWeeks(Days(14))", "2")]
    // Wave 2 — list length
    [InlineData("Count(Split(\"a,b,c\", \",\"))", "3")]
    [InlineData("Size(Split(\"a,b,c\", \",\"))", "3")]
    [InlineData("Count(Split(\"a\", \",\"))", "1")]
    [InlineData("Count(ToUserList(\"a@b.com;c@d.com\"))", "2")]
    public void Numeric_results(string expr, string expected)
    {
        var v = FormulaEval.Const(expr);
        v.Type.Should().Be(FormulaType.Number);
        v.AsNumber().Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("\"a\" & \"b\"", "ab")]
    [InlineData("Left(\"hello\", 2)", "he")]
    [InlineData("Right(\"hello\", 2)", "lo")]
    [InlineData("Mid(\"hello\", 2, 3)", "ell")]
    [InlineData("Upper(\"abc\")", "ABC")]
    [InlineData("Lower(\"ABC\")", "abc")]
    [InlineData("Trim(\"  hi  \")", "hi")]
    [InlineData("Replace(\"a-b-c\", \"-\", \"+\")", "a+b+c")]
    [InlineData("Part(\"a-b-c\", 2, \"-\")", "b")]
    [InlineData("Concat(\"a\", \"b\", \"c\")", "abc")]
    [InlineData("List(\", \", \"a\", \"\", \"b\")", "a, b")]  // empties skipped
    [InlineData("ToText(42)", "42")]
    [InlineData("ToText(2.5)", "2.5")]
    [InlineData("ToText(true)", "true")]
    [InlineData("If(true, \"yes\", \"no\")", "yes")]
    [InlineData("\"x\" & ToText(1 + 2)", "x3")]
    [InlineData("Case(\"b\", \"a\", \"Apple\", \"b\", \"Banana\")", "Banana")]
    // Wave 1 — text
    [InlineData("NotLeft(\"hello\", 2)", "llo")]
    [InlineData("NotRight(\"hello\", 2)", "hel")]
    [InlineData("RegexExtract(\"order-1234\", \"[0-9]+\")", "1234")]
    [InlineData("RegexExtract(\"a1b2\", \"([a-z])([0-9])\")", "a")]   // first capture group
    [InlineData("RegexReplace(\"a1b2c3\", \"[0-9]\", \"-\")", "a-b-c-")]
    [InlineData("RegexExtract(\"abc\", \"(\")", "")]                  // invalid pattern → empty
    [InlineData("HTMLToText(\"<b>Hi</b>&amp;Bye\")", "Hi&Bye")]
    [InlineData("URLEncode(\"a b&c\")", "a%20b%26c")]
    [InlineData("URLDecode(\"a%20b%26c\")", "a b&c")]
    [InlineData("Base64Encode(\"hi\")", "aGk=")]
    [InlineData("Base64Decode(\"aGk=\")", "hi")]
    [InlineData("NameOfDay(ToDate(\"2026-06-11\"))", "Thursday")]
    [InlineData("NameOfMonth(ToDate(\"2026-06-11\"))", "June")]
    // Wave 2 — Split/Join round trips
    [InlineData("Join(Split(\"a-b-c\", \"-\"), \"+\")", "a+b+c")]
    [InlineData("Join(Split(\"a,b,c\", \",\"), \"\")", "abc")]
    [InlineData("ToText(Split(\"a,b\", \",\"))", "a\nb")]   // list → text joins with newline
    public void Text_results(string expr, string expected)
    {
        var v = FormulaEval.Const(expr);
        v.Type.Should().Be(FormulaType.Text);
        v.AsText().Should().Be(expected);
    }

    [Theory]
    [InlineData("1 = 1", true)]
    [InlineData("1 <> 2", true)]
    [InlineData("2 > 1 and 3 > 2", true)]
    [InlineData("1 > 2 or 3 > 2", true)]
    [InlineData("not false", true)]
    [InlineData("Contains(\"hello\", \"ell\")", true)]
    [InlineData("Contains(\"hello\", \"xyz\")", false)]
    [InlineData("IsNull(ToNumber(\"x\"))", true)]
    [InlineData("IsNotNull(5)", true)]
    [InlineData("\"a\" = \"a\"", true)]
    [InlineData("\"a\" = \"A\"", false)]              // text equality is case-sensitive
    [InlineData("true and false", false)]
    [InlineData("1 < 2 = true", true)]               // (1<2)=true
    // Wave 1 — text predicates
    [InlineData("Begins(\"hello\", \"he\")", true)]
    [InlineData("Begins(\"hello\", \"lo\")", false)]
    [InlineData("Ends(\"hello\", \"lo\")", true)]
    [InlineData("Ends(\"hello\", \"he\")", false)]
    [InlineData("RegexMatch(\"abc123\", \"[0-9]+\")", true)]
    [InlineData("RegexMatch(\"abc\", \"[0-9]+\")", false)]
    [InlineData("RegexMatch(\"abc\", \"(\")", false)]   // invalid pattern → false
    // Wave 1 — date + user predicates
    [InlineData("IsWeekday(ToDate(\"2026-06-11\"))", true)]    // Thursday
    [InlineData("IsWeekday(ToDate(\"2026-06-13\"))", false)]   // Saturday
    [InlineData("IsUserEmail(\"a@b.com\")", true)]
    [InlineData("IsUserEmail(\"not-an-email\")", false)]
    [InlineData("IsUserEmail(\"a@b\")", false)]
    public void Bool_results(string expr, bool expected)
    {
        var v = FormulaEval.Const(expr);
        v.Type.Should().Be(FormulaType.Bool);
        v.AsBool().Should().Be(expected);
    }

    [Theory]
    // Wave 1 — date-returning functions (June 11 2026 is a Thursday)
    [InlineData("FirstDayOfMonth(ToDate(\"2026-06-15\"))", "2026-06-01")]
    [InlineData("LastDayOfMonth(ToDate(\"2026-02-10\"))", "2026-02-28")]   // 2026 not leap
    [InlineData("FirstDayOfYear(ToDate(\"2026-06-15\"))", "2026-01-01")]
    [InlineData("LastDayOfYear(ToDate(\"2026-06-15\"))", "2026-12-31")]
    [InlineData("FirstDayOfWeek(ToDate(\"2026-06-11\"))", "2026-06-07")]   // Sunday start
    [InlineData("LastDayOfWeek(ToDate(\"2026-06-11\"))", "2026-06-13")]    // Saturday end
    [InlineData("NextDayOfWeek(ToDate(\"2026-06-11\"), 1)", "2026-06-14")] // next Sunday
    [InlineData("PrevDayOfWeek(ToDate(\"2026-06-11\"), 1)", "2026-06-07")] // prev Sunday
    [InlineData("WeekdayAdd(ToDate(\"2026-06-11\"), 1)", "2026-06-12")]    // Thu +1 = Fri
    [InlineData("WeekdayAdd(ToDate(\"2026-06-12\"), 1)", "2026-06-15")]    // Fri +1 skips weekend → Mon
    [InlineData("WeekdaySub(ToDate(\"2026-06-15\"), 1)", "2026-06-12")]    // Mon -1 skips weekend → Fri
    public void Date_results(string expr, string expected)
    {
        var v = FormulaEval.Const(expr);
        v.Type.Should().Be(FormulaType.Date);
        v.AsDate().Should().Be(DateOnly.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void System_functions_read_runtime_identifiers()
    {
        var opt = new EvaluationOptions { AppId = "42", TableId = "abc-guid", UrlRoot = "https://app.example.com" };
        FormulaEval.Const("AppID()", opt).AsText().Should().Be("42");
        FormulaEval.Const("Dbid()", opt).AsText().Should().Be("abc-guid");
        FormulaEval.Const("URLRoot()", opt).AsText().Should().Be("https://app.example.com");
        FormulaEval.Const("URLRoot() & \"/r/\" & Dbid()", opt).AsText().Should().Be("https://app.example.com/r/abc-guid");
    }

    [Fact]
    public void System_functions_default_to_empty_when_unset()
    {
        FormulaEval.Const("AppID()").AsText().Should().Be(string.Empty);
        FormulaEval.Const("URLRoot()").AsText().Should().Be(string.Empty);
    }

    [Fact]
    public void Split_produces_a_text_list()
    {
        var v = FormulaEval.Const("Split(\"a,b,c\", \",\")");
        v.Type.Should().Be(FormulaType.TextList);
        v.AsTextList().Should().Equal("a", "b", "c");
    }

    [Fact]
    public void ToUserList_builds_users_from_delimited_text()
    {
        var v = FormulaEval.Const("ToUserList(\"a@b.com; c@d.com\")");
        v.Type.Should().Be(FormulaType.UserList);
        v.AsUserList().Select(u => u.UserId).Should().Equal("a@b.com", "c@d.com");
    }

    [Fact]
    public void Field_arithmetic_uses_record_values()
    {
        var v = new Bed().Field("Qty", FormulaType.Number, 10).Field("Price", FormulaType.Number, 5)
            .Eval("[Qty] * [Price]");
        v.AsNumber().Should().Be(50);
    }

    [Fact]
    public void Null_field_propagates_through_arithmetic()
    {
        var v = new Bed().Field("Qty", FormulaType.Number, null).Eval("[Qty] + 1");
        v.IsNull.Should().BeTrue();
        v.Type.Should().Be(FormulaType.Number);
    }

    [Fact]
    public void Nz_substitutes_for_null_field()
    {
        new Bed().Field("Qty", FormulaType.Number, null).Eval("Nz([Qty], 0) + 5").AsNumber().Should().Be(5);
    }

    [Fact]
    public void Null_text_field_concatenates_as_empty()
    {
        new Bed().Field("Name", FormulaType.Text, null).Eval("\"x\" & [Name]").AsText().Should().Be("x");
    }

    [Fact]
    public void Comparison_with_null_is_false()
    {
        new Bed().Field("Qty", FormulaType.Number, null).Eval("[Qty] > 5").AsBool().Should().BeFalse();
    }

    [Fact]
    public void Date_plus_duration_field()
    {
        var v = new Bed().Field("Start", FormulaType.Date, "2026-01-01").Eval("[Start] + Days(10)");
        v.Type.Should().Be(FormulaType.Date);
        v.AsDate().Should().Be(new DateOnly(2026, 1, 11));
    }

    [Fact]
    public void Today_uses_the_clock_from_options()
    {
        var opt = new EvaluationOptions { UtcNow = new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc) };
        FormulaEval.Const("Year(Today())", opt).AsNumber().Should().Be(2026);
    }

    [Fact]
    public void User_functions_read_current_user()
    {
        var opt = new EvaluationOptions { CurrentUser = new UserRef("u1", "a@b.com") };
        FormulaEval.Const("UserToEmail(User())", opt).AsText().Should().Be("a@b.com");
        FormulaEval.Const("UserToID(User())", opt).AsText().Should().Be("u1");
    }

    [Fact]
    public void Evaluate_throws_on_compile_errors()
    {
        var engine = new FormulaEngine();
        var schema = new TestSchema().Add("Qty", FormulaType.Number);
        var compiled = engine.Compile("[Qty] + \"x\"", schema);

        compiled.HasErrors.Should().BeTrue();
        Action act = () => engine.Evaluate(compiled, EmptyContext.Instance, EvaluationOptions.Default);
        act.Should().Throw<FormulaEvaluationException>();
    }
}
