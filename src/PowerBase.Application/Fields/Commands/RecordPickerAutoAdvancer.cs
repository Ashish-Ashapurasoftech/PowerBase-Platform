using PowerBase.Domain.Entities;

namespace PowerBase.Application.Fields.Commands;

/// <summary>
/// Shared by CreateFieldCommandHandler and BulkCreateFieldsCommandHandler — the only two places
/// that add fields to a table via the normal API (import/PBL restore go through a different path
/// and intentionally don't auto-advance the picker).
///
/// Identifying Records flow: a brand-new table starts with Primary Field pointing at Record ID#
/// (see AppSeeder). The 1st real field added takes over Primary, the 2nd takes Secondary, the 3rd
/// takes Tertiary — mirroring what the picker would show anyway (see
/// GetParentOptionsQueryHandler.ResolveLabelFields) but making the choice explicit and stable.
/// Never overwrites a slot the user has already customized: slot 1 only advances while it's still
/// unset or still the auto-seeded Record ID#, and slots 2/3 only fill in while still unset.
/// </summary>
public static class RecordPickerAutoAdvancer
{
    /// <summary>
    /// Computes the picker slots after adding <paramref name="newFieldId"/>, given the table's
    /// current slot values and the fields that existed on it immediately before this one (system
    /// fields included). Returns null when no slot should change (all 3 already spoken for, or the
    /// relevant slot was already customized by the user).
    /// </summary>
    public static (long? Field1Id, long? Field2Id, long? Field3Id)? NextSlots(
        AppTable table, IReadOnlyList<AppField> fieldsBeforeThisOne, long newFieldId)
    {
        var existingBusinessCount = fieldsBeforeThisOne.Count(f => !f.IsSystem);
        if (existingBusinessCount > 2) return null; // Primary/Secondary/Tertiary are all already spoken for.

        var recordIdField = fieldsBeforeThisOne.FirstOrDefault(f => f.IsSystem && f.Fid == 3);
        var f1 = table.DefaultRecordPickerField1Id;
        var f2 = table.DefaultRecordPickerField2Id;
        var f3 = table.DefaultRecordPickerField3Id;

        if (existingBusinessCount == 0)
        {
            var slot1IsAutoDefault = f1 is null || (recordIdField != null && f1 == recordIdField.Id);
            if (!slot1IsAutoDefault) return null;
            f1 = newFieldId;
        }
        else if (existingBusinessCount == 1)
        {
            if (f2 is not null) return null;
            f2 = newFieldId;
        }
        else
        {
            if (f3 is not null) return null;
            f3 = newFieldId;
        }

        return (f1, f2, f3);
    }
}
