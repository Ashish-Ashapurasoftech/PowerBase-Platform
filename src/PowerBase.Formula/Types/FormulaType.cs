namespace PowerBase.Formula.Types;

/// <summary>
/// The closed set of value types a formula operates on. Mirrors Quickbase's
/// formula type system rather than PowerBase's wider <c>FieldTypeCode</c> set:
/// many field types collapse to the same formula type (e.g. Currency/Percent/
/// Rating → <see cref="Number"/>). <see cref="Null"/> is the type of a typeless
/// null literal until a context gives it a concrete type.
/// </summary>
public enum FormulaType
{
    Text,
    Number,
    Date,
    DateTime,
    Duration,
    Bool,
    User,
    UserList,
    TextList,
    RecordList,
    Null,
}
