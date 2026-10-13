using FluentAssertions;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

/// <summary>
/// A helper field (the "(original)" Text field a relationship conversion creates) carries
/// <see cref="AppField.IsHidden"/> and must stay out of every user-facing field list / mapping target.
/// </summary>
public class HelperFieldsTests
{
    private static AppField Field(int fid, bool hidden = false) =>
        new() { Id = fid, Fid = fid, Name = $"F{fid}", Label = $"F{fid}", TypeCode = "Text", IsHidden = hidden };

    [Fact]
    public void IsHidden_ReflectsTheFlag()
    {
        HelperFields.IsHidden(Field(6, hidden: true)).Should().BeTrue();
        HelperFields.IsHidden(Field(6)).Should().BeFalse();
    }

    [Fact]
    public void IsHidden_IgnoresSettingsContent()
    {
        var field = Field(6);
        field.Settings = "{\"hiddenFromFieldList\":true}";

        HelperFields.IsHidden(field).Should().BeFalse();
    }

    [Fact]
    public void Visible_DropsHiddenFields()
    {
        var visible = Field(6);
        var hidden = Field(7, hidden: true);

        HelperFields.Visible([visible, hidden]).Should().ContainSingle().Which.Should().BeSameAs(visible);
    }

    [Fact]
    public void Visible_NoHiddenFields_ReturnsSameInstance()
    {
        IReadOnlyList<AppField> fields = [Field(6), Field(7)];

        HelperFields.Visible(fields).Should().BeSameAs(fields);
    }

    [Fact]
    public void CopyRecordsDefinition_IsWritable_RejectsHiddenField()
    {
        CopyRecordsDefinition.IsWritable(Field(6)).Should().BeTrue();
        CopyRecordsDefinition.IsWritable(Field(7, hidden: true)).Should().BeFalse();
    }
}
