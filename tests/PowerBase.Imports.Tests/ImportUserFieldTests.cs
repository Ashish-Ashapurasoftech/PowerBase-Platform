using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>A User field is filled with one chosen user, the same for every record: only a fixed value (a virtual column holding the user's public id) can
/// write it. The user is looked up once when the import is planned and every record gets the id the column stores.</summary>
public class ImportUserFieldTests
{
    private const int AppliedUser = 20;
    private static readonly Guid Someone = Guid.NewGuid();

    private static IUserRepository Users()
    {
        var users = Substitute.For<IUserRepository>();
        users.GetByPublicIdAsync(Someone, Arg.Any<CancellationToken>()).Returns(new User { Id = 77, PublicId = Someone, Name = "Someone" });
        users.GetByPublicIdAsync(Arg.Is<Guid>(g => g != Someone), Arg.Any<CancellationToken>()).Returns<User>(_ => throw new NotFoundException("User", Guid.Empty));
        return users;
    }

    private static ImportDefinitionConfig Config(Action<ImportDefinitionConfig>? tweak = null)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "Copy", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy,
            VirtualColumns = [new ImportVirtualColumn { Fid = ImportVirtual.FidBase, Name = "appliedUser", Kind = ImportVirtual.Fixed, StaticValue = Someone.ToString() }],
            Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = AppliedUser, SourceFid = ImportVirtual.FidBase }]
        };
        tweak?.Invoke(cfg);
        return cfg;
    }

    private static ImportHarness Create(ImportDefinitionConfig cfg, bool unique = false) => ImportHarness.Create(cfg, new HarnessOptions
    {
        RowCount = 3, Users = Users(),
        ConfigureDestination = fields =>
        {
            var user = ImportHarness.Field(11, AppliedUser, "appliedUser", "User");
            user.IsUnique = unique;
            fields.Add(user);
        }
    });

    [Fact]
    public async Task A_fixed_user_is_written_to_every_record_as_the_id_the_column_stores()
    {
        var h = Create(Config());

        await h.RunAsync();

        h.Counters.Inserted.Should().Be(3);
        h.Store.Inserted.Should().HaveCount(3).And.OnlyContain(r => Convert.ToInt64(r[AppliedUser]) == 77L);
    }

    [Fact]
    public async Task A_user_field_cannot_be_filled_from_a_source_column_or_a_formula()
    {
        var cfg = Config(c => c.Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = AppliedUser, SourceFid = 8 }]);
        var h = Create(cfg);

        var act = () => h.PlanBuilder.BuildAsync(cfg, ImportHarness.DestTableId, default);

        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*only be given a fixed value*");
    }

    [Fact]
    public async Task A_user_that_no_longer_exists_or_is_not_a_user_stops_the_import_before_it_reads_anything()
    {
        var gone = Config(c => c.VirtualColumns[0].StaticValue = Guid.NewGuid().ToString());
        (await ((Func<Task>)(() => Create(gone).PlanBuilder.BuildAsync(gone, ImportHarness.DestTableId, default))).Should().ThrowAsync<ValidationException>()).WithMessage("*no longer exists*");

        var text = Config(c => c.VirtualColumns[0].StaticValue = "Someone");
        (await ((Func<Task>)(() => Create(text).PlanBuilder.BuildAsync(text, ImportHarness.DestTableId, default))).Should().ThrowAsync<ValidationException>()).WithMessage("*Choose the user*");
    }

    [Fact]
    public async Task A_unique_user_field_cannot_get_one_fixed_user()
    {
        var cfg = Config();
        var act = () => Create(cfg, unique: true).PlanBuilder.BuildAsync(cfg, ImportHarness.DestTableId, default);

        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*must be unique*");
    }
}
