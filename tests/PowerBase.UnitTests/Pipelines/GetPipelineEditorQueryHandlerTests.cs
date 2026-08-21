using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Pipelines.Queries.GetPipelineEditor;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class GetPipelineEditorQueryHandlerTests
{
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAdminRepository _adminRepo = Substitute.For<IAdminRepository>();
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();

    private readonly GetPipelineEditorQueryHandler _handler;

    public GetPipelineEditorQueryHandlerTests()
    {
        _handler = new GetPipelineEditorQueryHandler(
            _pipelineRepo,
            _appRepo,
            _tableRepo,
            _fieldRepo,
            _adminRepo,
            _tenantRepo,
            _queryContext,
            _scopeFactory);
    }

    [Fact]
    public async Task HandleAsync_A1_ExistingPipelineEditorResponse_ReturnsCorrectData()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var appId = 456L;
        var appPublicId = Guid.NewGuid();

        var pipeline = new Pipeline
        {
            Id = pipelineId,
            PublicId = pipelinePublicId,
            AppId = appId,
            Name = "Flow Test",
            Description = "Desc",
            VariablesJson = "{}",
            IsActive = true,
            RowVersion = new byte[] { 1, 2, 3, 4 }
        };

        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>())
            .Returns(pipeline);

        _appRepo.GetPublicIdByIdAsync(appId, Arg.Any<CancellationToken>())
            .Returns(appPublicId);

        var steps = new List<PipelineStep>
        {
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "ref_1001",
                Label = "Step 1",
                DisplayOrder = 1,
                Type = "trigger",
                Subtype = "new-event",
                ConfigJson = "{\"tablePublicId\":\"" + Guid.NewGuid() + "\"}"
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>())
            .Returns(steps);

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.PublicId.Should().Be(pipelinePublicId);
        result.AppPublicId.Should().Be(appPublicId);
        result.Name.Should().Be("Flow Test");
        result.Description.Should().Be("Desc");
        result.IsActive.Should().BeTrue();
        result.Steps.Should().HaveCount(1);
        result.Steps[0].RefId.Should().Be("ref_1001");
        result.Steps[0].Label.Should().Be("Step 1");
    }

    [Fact]
    public async Task HandleAsync_A2_CurrentTenantTableMetadata_ResolvesCorrectly()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var appId = 456L;
        var appPublicId = Guid.NewGuid();
        var tablePublicId = Guid.NewGuid();
        var tableId = 789L;

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId, AppId = appId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);
        _appRepo.GetPublicIdByIdAsync(appId, Arg.Any<CancellationToken>()).Returns(appPublicId);

        var steps = new List<PipelineStep>
        {
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "ref_1",
                Type = "action",
                Subtype = "create-record",
                ConfigJson = "{\"tablePublicId\":\"" + tablePublicId + "\"}"
            }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        var table = new AppTable { Id = tableId, PublicId = tablePublicId, AppId = appId, Name = "Leads", IsShowInBar = true };
        _tableRepo.GetByPublicIdAsync(tablePublicId, Arg.Any<CancellationToken>()).Returns(table);
        _appRepo.GetPublicIdByIdAsync(appId, Arg.Any<CancellationToken>()).Returns(appPublicId);

        var fields = new List<AppField>
        {
            new() { Id = 1, PublicId = Guid.NewGuid(), AppTableId = tableId, Name = "Name", Label = "Name", TypeCode = "string", Fid = 101, IsRequired = true }
        };
        _fieldRepo.ListByTableAsync(tableId, Arg.Any<CancellationToken>()).Returns(fields);

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.EditorTables.Should().HaveCount(1);
        var tableMeta = result.EditorTables[0];
        tableMeta.TablePublicId.Should().Be(tablePublicId);
        tableMeta.TableName.Should().Be("Leads");
        tableMeta.Fields.Should().HaveCount(1);
        tableMeta.Fields[0].Name.Should().Be("Name");
        tableMeta.Fields[0].Fid.Should().Be(101);
    }

    [Fact]
    public async Task HandleAsync_A3_HiddenTable_ResolvesSuccessfully()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var appId = 456L;
        var tablePublicId = Guid.NewGuid();
        var tableId = 789L;

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId, AppId = appId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var steps = new List<PipelineStep>
        {
            new()
            {
                Id = 1,
                Type = "action",
                Subtype = "create-record",
                ConfigJson = "{\"tablePublicId\":\"" + tablePublicId + "\"}"
            }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        var table = new AppTable { Id = tableId, PublicId = tablePublicId, AppId = appId, Name = "HiddenLeads", IsShowInBar = false };
        _tableRepo.GetByPublicIdAsync(tablePublicId, Arg.Any<CancellationToken>()).Returns(table);

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.EditorTables.Should().HaveCount(1);
        result.EditorTables[0].TableName.Should().Be("HiddenLeads");
    }

    [Fact]
    public async Task HandleAsync_A4_MultipleStepsSameTableDeduplicate_ResolvesOnce()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var appId = 456L;
        var tablePublicId = Guid.NewGuid();
        var tableId = 789L;

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId, AppId = appId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var steps = new List<PipelineStep>
        {
            new() { Id = 1, Type = "action", Subtype = "create-record", ConfigJson = "{\"tablePublicId\":\"" + tablePublicId + "\"}" },
            new() { Id = 2, Type = "action", Subtype = "update-record", ConfigJson = "{\"tablePublicId\":\"" + tablePublicId + "\"}" },
            new() { Id = 3, Type = "query", Subtype = "search-records", ConfigJson = "{\"tablePublicId\":\"" + tablePublicId + "\"}" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        var table = new AppTable { Id = tableId, PublicId = tablePublicId, AppId = appId, Name = "Leads" };
        _tableRepo.GetByPublicIdAsync(tablePublicId, Arg.Any<CancellationToken>()).Returns(table);

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.EditorTables.Should().HaveCount(1);
        await _tableRepo.Received(1).GetByPublicIdAsync(tablePublicId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_A5_DifferentTables_ResolvesIndependently()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var appId = 456L;
        var table1PublicId = Guid.NewGuid();
        var table2PublicId = Guid.NewGuid();

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId, AppId = appId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var steps = new List<PipelineStep>
        {
            new() { Id = 1, Type = "action", Subtype = "create-record", ConfigJson = "{\"tablePublicId\":\"" + table1PublicId + "\"}" },
            new() { Id = 2, Type = "action", Subtype = "update-record", ConfigJson = "{\"tablePublicId\":\"" + table2PublicId + "\"}" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        var table1 = new AppTable { Id = 1, PublicId = table1PublicId, AppId = appId, Name = "Leads" };
        var table2 = new AppTable { Id = 2, PublicId = table2PublicId, AppId = appId, Name = "Contacts" };
        _tableRepo.GetByPublicIdAsync(table1PublicId, Arg.Any<CancellationToken>()).Returns(table1);
        _tableRepo.GetByPublicIdAsync(table2PublicId, Arg.Any<CancellationToken>()).Returns(table2);

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.EditorTables.Should().HaveCount(2);
        result.EditorTables.Select(t => t.TablePublicId).Should().Contain(new[] { table1PublicId, table2PublicId });
    }

    [Fact]
    public async Task HandleAsync_B1_And_B2_CrossTenantMetadataResolution_ResolvesCorrectly()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var appId = 456L;
        var targetTenantPublicId = Guid.NewGuid();
        var targetTenantId = 999L;
        var tablePublicId = Guid.NewGuid();
        var tableId = 888L;

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId, AppId = appId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var steps = new List<PipelineStep>
        {
            new()
            {
                Id = 1,
                Type = "action",
                Subtype = "create-record",
                ConfigJson = "{\"connectionPublicId\":\"" + targetTenantPublicId + "\",\"tablePublicId\":\"" + tablePublicId + "\"}"
            }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        _adminRepo.GetTenantIdByPublicIdAsync(targetTenantPublicId, Arg.Any<CancellationToken>()).Returns(targetTenantId);

        // Setup Scoped Services Mocks
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        _scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);

        var scopedQueryContext = Substitute.For<IQueryContext>();
        var scopedTenantRepo = Substitute.For<ITenantRepository>();
        var scopedTableRepo = Substitute.For<IAppTableRepository>();
        var scopedFieldRepo = Substitute.For<IAppFieldRepository>();
        var scopedAppRepo = Substitute.For<IAppRepository>();

        provider.GetService(typeof(IQueryContext)).Returns(scopedQueryContext);
        provider.GetService(typeof(ITenantRepository)).Returns(scopedTenantRepo);
        provider.GetService(typeof(IAppTableRepository)).Returns(scopedTableRepo);
        provider.GetService(typeof(IAppFieldRepository)).Returns(scopedFieldRepo);
        provider.GetService(typeof(IAppRepository)).Returns(scopedAppRepo);

        _queryContext.UserId.Returns(1001L);
        scopedTenantRepo.IsActiveMemberAsync(1001L, Arg.Any<CancellationToken>()).Returns(true);

        var table = new AppTable { Id = tableId, PublicId = tablePublicId, AppId = appId, Name = "CrossLeads" };
        scopedTableRepo.GetByPublicIdAsync(tablePublicId, Arg.Any<CancellationToken>()).Returns(table);

        var fields = new List<AppField>
        {
            new() { Id = 1, PublicId = Guid.NewGuid(), AppTableId = tableId, Name = "CrossName", Label = "CrossName", TypeCode = "string", Fid = 202 }
        };
        scopedFieldRepo.ListByTableAsync(tableId, Arg.Any<CancellationToken>()).Returns(fields);

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.EditorTables.Should().HaveCount(1);
        var tableMeta = result.EditorTables[0];
        tableMeta.ConnectionPublicId.Should().Be(targetTenantPublicId.ToString());
        tableMeta.TablePublicId.Should().Be(tablePublicId);
        tableMeta.TableName.Should().Be("CrossLeads");
        tableMeta.Fields.Should().HaveCount(1);
        tableMeta.Fields[0].Name.Should().Be("CrossName");
        tableMeta.Fields[0].Fid.Should().Be(202);

        scopedQueryContext.Received(1).SetTenantId(targetTenantId);
    }

    [Fact]
    public async Task HandleAsync_B3_TwoTenantsWithSimilarTableIdentity_DoesNotCollide()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var appId = 456L;
        var tenantA_PublicId = Guid.NewGuid();
        var tenantB_PublicId = Guid.NewGuid();
        var tenantA_Id = 111L;
        var tenantB_Id = 222L;
        var sharedTablePublicId = Guid.NewGuid();

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId, AppId = appId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var steps = new List<PipelineStep>
        {
            new() { Id = 1, Type = "action", Subtype = "create-record", ConfigJson = "{\"connectionPublicId\":\"" + tenantA_PublicId + "\",\"tablePublicId\":\"" + sharedTablePublicId + "\"}" },
            new() { Id = 2, Type = "action", Subtype = "update-record", ConfigJson = "{\"connectionPublicId\":\"" + tenantB_PublicId + "\",\"tablePublicId\":\"" + sharedTablePublicId + "\"}" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        _adminRepo.GetTenantIdByPublicIdAsync(tenantA_PublicId, Arg.Any<CancellationToken>()).Returns(tenantA_Id);
        _adminRepo.GetTenantIdByPublicIdAsync(tenantB_PublicId, Arg.Any<CancellationToken>()).Returns(tenantB_Id);

        // Scoped scope setups
        var scopeA = Substitute.For<IServiceScope>();
        var providerA = Substitute.For<IServiceProvider>();
        scopeA.ServiceProvider.Returns(providerA);

        var scopeB = Substitute.For<IServiceScope>();
        var providerB = Substitute.For<IServiceProvider>();
        scopeB.ServiceProvider.Returns(providerB);

        _scopeFactory.CreateScope().Returns(scopeA, scopeB);

        // Tenant A services
        var scopedQueryContextA = Substitute.For<IQueryContext>();
        var scopedTenantRepoA = Substitute.For<ITenantRepository>();
        var scopedTableRepoA = Substitute.For<IAppTableRepository>();
        var scopedFieldRepoA = Substitute.For<IAppFieldRepository>();
        var scopedAppRepoA = Substitute.For<IAppRepository>();
        providerA.GetService(typeof(IQueryContext)).Returns(scopedQueryContextA);
        providerA.GetService(typeof(ITenantRepository)).Returns(scopedTenantRepoA);
        providerA.GetService(typeof(IAppTableRepository)).Returns(scopedTableRepoA);
        providerA.GetService(typeof(IAppFieldRepository)).Returns(scopedFieldRepoA);
        providerA.GetService(typeof(IAppRepository)).Returns(scopedAppRepoA);

        // Tenant B services
        var scopedQueryContextB = Substitute.For<IQueryContext>();
        var scopedTenantRepoB = Substitute.For<ITenantRepository>();
        var scopedTableRepoB = Substitute.For<IAppTableRepository>();
        var scopedFieldRepoB = Substitute.For<IAppFieldRepository>();
        var scopedAppRepoB = Substitute.For<IAppRepository>();
        providerB.GetService(typeof(IQueryContext)).Returns(scopedQueryContextB);
        providerB.GetService(typeof(ITenantRepository)).Returns(scopedTenantRepoB);
        providerB.GetService(typeof(IAppTableRepository)).Returns(scopedTableRepoB);
        providerB.GetService(typeof(IAppFieldRepository)).Returns(scopedFieldRepoB);
        providerB.GetService(typeof(IAppRepository)).Returns(scopedAppRepoB);

        _queryContext.UserId.Returns(1001L);
        scopedTenantRepoA.IsActiveMemberAsync(1001L, Arg.Any<CancellationToken>()).Returns(true);
        scopedTenantRepoB.IsActiveMemberAsync(1001L, Arg.Any<CancellationToken>()).Returns(true);

        scopedTableRepoA.GetByPublicIdAsync(sharedTablePublicId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 1, PublicId = sharedTablePublicId, Name = "Table A" });
        scopedTableRepoB.GetByPublicIdAsync(sharedTablePublicId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, PublicId = sharedTablePublicId, Name = "Table B" });

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.EditorTables.Should().HaveCount(2);
        result.EditorTables.Any(t => t.ConnectionPublicId == tenantA_PublicId.ToString() && t.TableName == "Table A").Should().BeTrue();
        result.EditorTables.Any(t => t.ConnectionPublicId == tenantB_PublicId.ToString() && t.TableName == "Table B").Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_C1_DeletedMissingTable_ReturnsDegradedState()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var tablePublicId = Guid.NewGuid();

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var steps = new List<PipelineStep>
        {
            new() { Id = 1, Type = "action", Subtype = "create-record", ConfigJson = "{\"tablePublicId\":\"" + tablePublicId + "\"}" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        _tableRepo.GetByPublicIdAsync(tablePublicId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AppTable>(new NotFoundException("Table", tablePublicId)));

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.EditorTables.Should().BeEmpty();
        result.ClientResolveRefs.Should().HaveCount(1);
        result.ClientResolveRefs[0].TablePublicId.Should().Be(tablePublicId);
        result.ClientResolveRefs[0].Reason.Should().Be(PipelineEditorRefReason.TableNotFound);
    }

    [Fact]
    public async Task HandleAsync_C2_TenantNotFound_ReturnsTenantNotFoundRef()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var targetTenantPublicId = Guid.NewGuid();
        var tablePublicId = Guid.NewGuid();

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var steps = new List<PipelineStep>
        {
            new() { Id = 1, Type = "action", Subtype = "create-record", ConfigJson = "{\"connectionPublicId\":\"" + targetTenantPublicId + "\",\"tablePublicId\":\"" + tablePublicId + "\"}" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        _adminRepo.GetTenantIdByPublicIdAsync(targetTenantPublicId, Arg.Any<CancellationToken>()).Returns((long?)null);
        _pipelineRepo.GetConnectionByPublicIdAsync(targetTenantPublicId, Arg.Any<CancellationToken>()).Returns((PipelineConnection?)null);

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.ClientResolveRefs.Should().HaveCount(1);
        result.ClientResolveRefs[0].Reason.Should().Be(PipelineEditorRefReason.TenantNotFound);
    }

    [Fact]
    public async Task HandleAsync_C3_AccessDenied_DoesNotLeakMetadata()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var tablePublicId = Guid.NewGuid();

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var steps = new List<PipelineStep>
        {
            new() { Id = 1, Type = "action", Subtype = "create-record", ConfigJson = "{\"tablePublicId\":\"" + tablePublicId + "\"}" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        _tableRepo.GetByPublicIdAsync(tablePublicId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AppTable>(new UnauthorizedActionException("Access denied")));

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.EditorTables.Should().BeEmpty();
        result.ClientResolveRefs.Should().HaveCount(1);
        result.ClientResolveRefs[0].Reason.Should().Be(PipelineEditorRefReason.AccessDenied);
    }

    [Fact]
    public async Task HandleAsync_D1_SavedConnection_ReturnsSavedConnectionRef()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var connectionPublicId = Guid.NewGuid();
        var tablePublicId = Guid.NewGuid();

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var steps = new List<PipelineStep>
        {
            new() { Id = 1, Type = "action", Subtype = "create-record", ConfigJson = "{\"connectionPublicId\":\"" + connectionPublicId + "\",\"tablePublicId\":\"" + tablePublicId + "\"}" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        _adminRepo.GetTenantIdByPublicIdAsync(connectionPublicId, Arg.Any<CancellationToken>()).Returns((long?)null);
        _pipelineRepo.GetConnectionByPublicIdAsync(connectionPublicId, Arg.Any<CancellationToken>())
            .Returns(new PipelineConnection { PublicId = connectionPublicId, Name = "QB Conn" });

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.ClientResolveRefs.Should().HaveCount(1);
        result.ClientResolveRefs[0].Reason.Should().Be(PipelineEditorRefReason.SavedConnection);
    }

    [Fact]
    public async Task HandleAsync_D2_SystemConnection_ClassifiesIndependently()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var connectionPublicId = PipelineStepValidator.SystemConnectionIds.First();
        var tablePublicId = Guid.NewGuid();

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var steps = new List<PipelineStep>
        {
            new() { Id = 1, Type = "action", Subtype = "create-record", ConfigJson = "{\"connectionPublicId\":\"" + connectionPublicId + "\",\"tablePublicId\":\"" + tablePublicId + "\"}" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.ClientResolveRefs.Should().HaveCount(1);
        result.ClientResolveRefs[0].Reason.Should().Be(PipelineEditorRefReason.SystemConnection);
    }

    [Fact]
    public async Task HandleAsync_D3_InvalidReferences_AreNotHealthyClientRefs()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var tablePublicId = Guid.NewGuid();

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var steps = new List<PipelineStep>
        {
            new() { Id = 1, Type = "action", Subtype = "create-record", ConfigJson = "{\"tablePublicId\":\"" + tablePublicId + "\"}" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        _tableRepo.GetByPublicIdAsync(tablePublicId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AppTable>(new Exception("Unknown DB error")));

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.ClientResolveRefs.Should().HaveCount(1);
        result.ClientResolveRefs[0].Reason.Should().Be(PipelineEditorRefReason.ResolutionError);
    }

    [Fact]
    public async Task HandleAsync_E1_E2_E3_NestedPipelineReferenceExtraction_ResolvesAllNestedSteps()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var pipelineId = 123L;
        var appId = 456L;
        var table1PublicId = Guid.NewGuid();
        var table2PublicId = Guid.NewGuid();
        var table3PublicId = Guid.NewGuid();

        var pipeline = new Pipeline { Id = pipelineId, PublicId = pipelinePublicId, AppId = appId };
        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var steps = new List<PipelineStep>
        {
            // Root condition step
            new() { Id = 1, Type = "condition", Subtype = "branch", ConfigJson = "{}" },
            // Child of condition
            new() { Id = 2, ParentStepId = 1, ParentBranch = "children", Type = "action", Subtype = "create-record", ConfigJson = "{\"tablePublicId\":\"" + table1PublicId + "\"}" },
            // Loop step (root)
            new() { Id = 3, Type = "loop", Subtype = "for-each", ConfigJson = "{}" },
            // Child of loop
            new() { Id = 4, ParentStepId = 3, ParentBranch = "children", Type = "action", Subtype = "update-record", ConfigJson = "{\"tablePublicId\":\"" + table2PublicId + "\"}" },
            // Handler branch (elsechildren/errorchildren) child
            new() { Id = 5, ParentStepId = 1, ParentBranch = "errorchildren", Type = "action", Subtype = "delete-record", ConfigJson = "{\"tablePublicId\":\"" + table3PublicId + "\"}" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>()).Returns(steps);

        _tableRepo.GetByPublicIdAsync(table1PublicId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 1, PublicId = table1PublicId, Name = "Table 1" });
        _tableRepo.GetByPublicIdAsync(table2PublicId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 2, PublicId = table2PublicId, Name = "Table 2" });
        _tableRepo.GetByPublicIdAsync(table3PublicId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 3, PublicId = table3PublicId, Name = "Table 3" });

        var query = new GetPipelineEditorQuery(pipelinePublicId);

        // Act
        var result = await _handler.HandleAsync(query, CancellationToken.None);

        // Assert
        result.EditorTables.Should().HaveCount(3);
        result.EditorTables.Select(t => t.TablePublicId).Should().Contain(new[] { table1PublicId, table2PublicId, table3PublicId });
    }
}
