using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>The saved definition's JSON columns round-trip, and older definitions (saved before rules and policies existed)
/// still load with safe defaults.</summary>
public class ImportConfigMapperTests
{
    private static ImportDefinitionConfig Config() => new()
    {
        Name = "  Nightly  ", SourceTableId = Guid.NewGuid(), ImportType = ImportTypes.Merge, MergeKeyFid = 6,
        Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 9, SourceFid = 9 }],
        ColumnRules = [new() { DestFid = 9, RequireField = true }, new() { DestFid = 7 }], // the second has nothing switched on
        ConstraintPolicy = ImportConstraintPolicy.AbortIfAnyIssue
    };

    [Fact]
    public void Rules_and_policy_survive_a_save_and_load()
    {
        var config = Config();
        var entity = new ImportDefinition();

        ImportConfigMapper.Apply(entity, config);
        var loaded = ImportConfigMapper.ToConfig(entity, config.SourceTableId);

        loaded.Name.Should().Be("Nightly");
        loaded.ConstraintPolicy.Should().Be(ImportConstraintPolicy.AbortIfAnyIssue);
        loaded.ColumnRules.Should().ContainSingle().Which.Should().BeEquivalentTo(new ImportColumnRule { DestFid = 9, RequireField = true });
        loaded.Mappings.Should().HaveCount(2);
    }

    [Fact]
    public void Nothing_is_stored_for_the_default_policy_or_for_rules_that_do_nothing()
    {
        var config = new ImportDefinitionConfig { Name = "x", ColumnRules = [new() { DestFid = 7 }] };
        var entity = new ImportDefinition();

        ImportConfigMapper.Apply(entity, config);

        entity.ColumnRulesJson.Should().BeNull();
        entity.OptionsJson.Should().BeNull();
    }

    [Fact]
    public void A_definition_saved_before_rules_existed_loads_with_no_rules_and_the_default_policy()
    {
        var old = new ImportDefinition { Name = "Old", ImportType = "copy", FieldMappingJson = "[{\"destFid\":6,\"sourceFid\":6}]" };

        var loaded = ImportConfigMapper.ToConfig(old, Guid.NewGuid());

        loaded.ColumnRules.Should().BeEmpty();
        loaded.ConstraintPolicy.Should().Be(ImportConstraintPolicy.ImportValid);
        loaded.Mappings.Should().ContainSingle();
    }

    [Fact]
    public void A_stored_policy_that_is_no_longer_valid_falls_back_to_the_default()
    {
        var entity = new ImportDefinition { FieldMappingJson = "[]", OptionsJson = "{\"constraintPolicy\":\"somethingRemoved\"}" };

        ImportConfigMapper.ToConfig(entity, Guid.NewGuid()).ConstraintPolicy.Should().Be(ImportConstraintPolicy.ImportValid);
    }
}
