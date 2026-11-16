using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Reporting;

namespace PowerBase.UnitTests.Import.Reporting;

public class ImportRefResolverTests
{
    [Fact]
    public void Resolve_FieldRef_ReturnsItsTableAndLabel()
    {
        var document = new PblDocument
        {
            Tables =
            [
                new PblTable
                {
                    LogicalRef = "$Table_Clients",
                    Name = "Clients",
                    Fields = [new PblField { LogicalRef = "$Field_Clients_Name", Name = "Name", Label = "Company Name", TypeCode = "Text" }],
                },
            ],
        };

        var resolver = new ImportRefResolver(document);
        var info = resolver.Resolve("$Field_Clients_Name");

        info.Should().NotBeNull();
        info!.TableName.Should().Be("Clients");
        info.FieldName.Should().Be("Company Name");
        info.ElementType.Should().Be("Field");
    }

    [Fact]
    public void Resolve_LookupRef_ReturnsTheChildTableItWasPulledOnto()
    {
        var document = new PblDocument
        {
            Tables =
            [
                new PblTable { LogicalRef = "$Table_Parent", Name = "Clients" },
                new PblTable { LogicalRef = "$Table_Child", Name = "Orders" },
            ],
            Relationships =
            [
                new PblRelationship
                {
                    LogicalRef = "$Rel_1",
                    ParentTableRef = "$Table_Parent",
                    ChildTableRef = "$Table_Child",
                    ReferenceFieldName = "Related Client",
                    Lookups = [new PblLookupField { LogicalRef = "$Lookup_1", Name = "Client Name", SourceFieldName = "Name" }],
                },
            ],
        };

        var resolver = new ImportRefResolver(document);
        var info = resolver.Resolve("$Lookup_1");

        info.Should().NotBeNull();
        info!.TableName.Should().Be("Orders"); // the lookup field lives on the child table
        info.FieldName.Should().Be("Client Name");
        info.ElementType.Should().Be("Lookup Field");
    }

    [Fact]
    public void Resolve_SummaryRef_ReturnsTheParentTableItWasRolledUpOnto()
    {
        var document = new PblDocument
        {
            Tables =
            [
                new PblTable { LogicalRef = "$Table_Parent", Name = "Clients" },
                new PblTable { LogicalRef = "$Table_Child", Name = "Orders" },
            ],
            Relationships =
            [
                new PblRelationship
                {
                    LogicalRef = "$Rel_1",
                    ParentTableRef = "$Table_Parent",
                    ChildTableRef = "$Table_Child",
                    ReferenceFieldName = "Related Client",
                    Summaries = [new PblSummaryField { LogicalRef = "$Summary_1", Name = "Order Count", Function = "Count" }],
                },
            ],
        };

        var resolver = new ImportRefResolver(document);
        var info = resolver.Resolve("$Summary_1");

        info.Should().NotBeNull();
        info!.TableName.Should().Be("Clients"); // the summary field lives on the parent table
        info.ElementType.Should().Be("Summary Field");
    }

    [Fact]
    public void Resolve_BareFieldRefNotMatchingTheQualifiedLogicalRef_FallsBackToTheUnqualifiedLocalPart()
    {
        // QblToPblConverter.Qualify builds the final LogicalRef as "$TableRef::$FieldRef", but several
        // issues (every QBL_EMPTY_FORMULA among them) are raised against the bare, unqualified field ref
        // it had on hand before that qualification happened — this must still resolve, not fall into "General".
        var document = new PblDocument
        {
            Tables =
            [
                new PblTable
                {
                    LogicalRef = "$Table_Activity",
                    Name = "Activity",
                    Fields = [new PblField { LogicalRef = "$Table_Activity::$Field_Backup", Name = "Activity Backup", TypeCode = "Formula" }],
                },
            ],
        };

        var resolver = new ImportRefResolver(document);
        var info = resolver.Resolve("$Field_Backup");

        info.Should().NotBeNull();
        info!.TableName.Should().Be("Activity");
        info.FieldName.Should().Be("Activity Backup");
    }

    [Fact]
    public void Resolve_BareRefSharedByFieldsOnTwoDifferentTables_StaysUnresolvedRatherThanGuessingWrong()
    {
        var document = new PblDocument
        {
            Tables =
            [
                new PblTable { LogicalRef = "$Table_A", Name = "A", Fields = [new PblField { LogicalRef = "$Table_A::$Field_Name", Name = "Name" }] },
                new PblTable { LogicalRef = "$Table_B", Name = "B", Fields = [new PblField { LogicalRef = "$Table_B::$Field_Name", Name = "Name" }] },
            ],
        };

        var resolver = new ImportRefResolver(document);

        resolver.Resolve("$Field_Name").Should().BeNull();
        // The fully-qualified refs are unaffected by the ambiguity.
        resolver.Resolve("$Table_A::$Field_Name")!.TableName.Should().Be("A");
        resolver.Resolve("$Table_B::$Field_Name")!.TableName.Should().Be("B");
    }

    [Fact]
    public void Resolve_UnknownRef_ReturnsNull()
    {
        var resolver = new ImportRefResolver(new PblDocument());
        resolver.Resolve("$Field_Does_Not_Exist").Should().BeNull();
    }

    [Fact]
    public void Resolve_NullDocument_NeverThrowsAndAlwaysReturnsNull()
    {
        var resolver = new ImportRefResolver(null);
        resolver.Resolve("$Field_Anything").Should().BeNull();
    }
}
