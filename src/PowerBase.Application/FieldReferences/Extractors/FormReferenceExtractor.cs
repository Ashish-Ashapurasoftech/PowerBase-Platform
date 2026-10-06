using PowerBase.Domain.Entities;

namespace PowerBase.Application.FieldReferences.Extractors;

/// <summary>The fields a form places: one reference per Field element in its layout. Despite its
/// name, <c>FormElement.AppFieldId</c> holds the field's per-table <c>Fid</c> (migration 013 switched
/// it from <c>meta.AppField.Id</c>), so it is resolved through the table like every other source.</summary>
public static class FormReferenceExtractor
{
    public static void Extract(IEnumerable<FormSection> layout, TableFieldIndex table, FieldReferenceCollector collector)
    {
        foreach (var element in Elements(layout))
            if (element.ElementType == "Field")
                collector.Add(table.IdOfFid(element.AppFieldId), FieldReferenceUsages.FormElement);
    }

    /// <summary>Every element of a layout (blocks nest the elements; legacy rows hang off the section).</summary>
    public static IEnumerable<FormElement> Elements(IEnumerable<FormSection> layout) =>
        layout.SelectMany(s => s.Blocks).SelectMany(b => b.Elements);
}
