using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.CreateRelationship;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Relationships;

public class LookupFieldTests
{
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly ISchemaEngineService _schemaEngine = Substitute.For<ISchemaEngineService>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IFieldNameResolver _nameResolver = Substitute.For<IFieldNameResolver>();
    private readonly RelationshipFieldFactory _factory;

    private const long TableId = 10;

    public LookupFieldTests()
    {
        _factory = new RelationshipFieldFactory(
            _fieldRepo, _fieldTypeRepo, _schemaEngine, _formRepo, _queryContext, _nameResolver);

        _fieldTypeRepo.GetByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new FieldType { Id = 1, Code = callInfo.Arg<string>() });

        _nameResolver.GenerateUniqueNameAsync(TableId, Arg.Any<string>(), false, Arg.Any<CancellationToken>())
            .Returns(callInfo => "C_" + callInfo.ArgAt<string>(1).Replace(" ", ""));

        _fieldRepo.GetNextFidAsync(TableId, Arg.Any<CancellationToken>()).Returns(20);
        _fieldRepo.CreateAsync(Arg.Any<AppField>(), Arg.Any<CancellationToken>())
            .Returns((99L, Guid.NewGuid()));
    }

    [Fact]
    public async Task CreateAsync_lookup_with_existing_label_auto_disambiguates_suffix()
    {
        var table = new AppTable { Id = TableId, Name = "Orders" };
        var existingLabel = "Customers - Company Name";

        // "Customers - Company Name" exists, but "Customers - Company Name 1" does not
        _fieldRepo.LabelExistsInTableAsync(TableId, existingLabel, Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _fieldRepo.LabelExistsInTableAsync(TableId, $"{existingLabel} 1", Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var field = await _factory.CreateAsync(table, "Lookup", existingLabel, false, new object(), CancellationToken.None);

        field.Should().NotBeNull();
        field.Label.Should().Be("Customers - Company Name 1");
    }

    [Fact]
    public async Task CreateAsync_lookup_with_existing_suffixed_label_increments_suffix()
    {
        var table = new AppTable { Id = TableId, Name = "Orders" };
        var existingLabel = "Customers - Company Name 1";

        // "Customers - Company Name 1" exists, but "Customers - Company Name 2" does not
        _fieldRepo.LabelExistsInTableAsync(TableId, existingLabel, Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _fieldRepo.LabelExistsInTableAsync(TableId, "Customers - Company Name 2", Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var field = await _factory.CreateAsync(table, "Lookup", existingLabel, false, new object(), CancellationToken.None);

        field.Should().NotBeNull();
        field.Label.Should().Be("Customers - Company Name 2");
    }

    [Fact]
    public async Task CreateAsync_reference_with_existing_label_auto_disambiguates_suffix()
    {
        var table = new AppTable { Id = TableId, Name = "Orders" };
        var existingLabel = "Related Customer";

        _fieldRepo.LabelExistsInTableAsync(TableId, existingLabel, Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _fieldRepo.LabelExistsInTableAsync(TableId, "Related Customer 1", Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var field = await _factory.CreateAsync(table, "Reference", existingLabel, false, new object(), CancellationToken.None);

        field.Should().NotBeNull();
        field.Label.Should().Be("Related Customer 1");
    }

    [Fact]
    public async Task CreateAsync_summary_with_existing_label_throws_duplicate_exception()
    {
        var table = new AppTable { Id = TableId, Name = "Orders" };
        var existingLabel = "Total Amount";

        _fieldRepo.LabelExistsInTableAsync(TableId, existingLabel, Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var act = async () => await _factory.CreateAsync(table, "Summary", existingLabel, false, new object(), CancellationToken.None);

        await act.Should().ThrowAsync<DuplicateException>();
    }
}
