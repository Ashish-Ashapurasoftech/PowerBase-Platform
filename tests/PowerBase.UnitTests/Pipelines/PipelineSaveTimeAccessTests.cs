using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Saving a Create Record / Copy Records step must follow the platform's record-access model: app
/// membership plus the role's table-level access. Regular roles (e.g. Participant with custom table
/// permissions) do not carry the flat records:* codes, so requiring them rejected users who really
/// do have access ("You do not have permission to perform this action in this app").
/// </summary>
public class PipelineSaveTimeAccessTests
{
    private readonly Guid _tableId = Guid.NewGuid();
    private readonly Guid _sourceId = Guid.NewGuid();
    private readonly IAppAccessService _access = Substitute.For<IAppAccessService>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly PipelineStepValidator _validator;
    private readonly AppTable _table;
    private readonly AppTable _source;

    public PipelineSaveTimeAccessTests()
    {
        _table = new AppTable { Id = 20, PublicId = _tableId, Name = "A2" };
        _source = new AppTable { Id = 21, PublicId = _sourceId, Name = "A1" };
        _tables.GetByPublicIdAsync(_tableId, Arg.Any<CancellationToken>()).Returns(_table);
        _tables.GetByPublicIdAsync(_sourceId, Arg.Any<CancellationToken>()).Returns(_source);
        _fields.ListByTableAsync(20, Arg.Any<CancellationToken>()).Returns(new List<AppField>
            { new() { Fid = 6, Name = "name", Label = "name", TypeCode = "Text" } });
        _fields.ListByTableAsync(21, Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        // The flat records:* codes are never granted in these tests.
        _access.RequirePermissionByTablePublicIdAsync(Arg.Any<Guid>(), Arg.Is<string>(c => c.StartsWith("records:")), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new UnauthorizedActionException("You do not have permission to perform this action in this app."));
        _validator = new PipelineStepValidator(Substitute.For<IPipelineRepository>(), Substitute.For<IAppRepository>(),
            _tables, _fields, _access, Substitute.For<ITenantRepository>(), Substitute.For<IQueryContext>(),
            enforcer: _enforcer);
    }

    private string CreateConfig() => JsonSerializer.Serialize(new
    {
        connectionPublicId = PipelineStepValidator.SystemConnectionIds.First(),
        tablePublicId = _tableId,
        fieldMappings = new[] { new { field = "fid_6", value = "x" } }
    });

    private void TableAccess(AppTable table, TableAccessContext access) =>
        _enforcer.GetTableAccessAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>()).Returns(access);

    [Fact]
    public async Task CreateRecord_RoleWithTableLevelAdd_SavesWithoutFlatRecordsPermission()
    {
        TableAccess(_table, new TableAccessContext { Unrestricted = false, CanAdd = true });

        await _validator.ValidateCreateRecordRequiredFieldsAsync(CreateConfig(), CancellationToken.None);

        await _access.DidNotReceive().RequirePermissionByTablePublicIdAsync(Arg.Any<Guid>(), PermissionCodes.RecordsCreate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRecord_RoleWithoutTableLevelAdd_IsRejected()
    {
        TableAccess(_table, new TableAccessContext { Unrestricted = false, CanAdd = false });

        var act = () => _validator.ValidateCreateRecordRequiredFieldsAsync(CreateConfig(), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedActionException>().WithMessage("*add records to table 'A2'*");
    }

    [Fact]
    public async Task CreateRecord_NonMember_IsRejected()
    {
        _access.RequireMembershipByTablePublicIdAsync(_tableId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new UnauthorizedActionException("You are not a member of this app."));

        var act = () => _validator.ValidateCreateRecordRequiredFieldsAsync(CreateConfig(), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedActionException>().WithMessage("*not a member*");
    }

    [Fact]
    public async Task CreateRecord_UnrestrictedRole_Saves()
    {
        TableAccess(_table, new TableAccessContext { Unrestricted = true });

        await _validator.ValidateCreateRecordRequiredFieldsAsync(CreateConfig(), CancellationToken.None);
    }

    [Fact]
    public async Task CreateRecord_WithoutEnforcer_StillRequiresMembership()
    {
        var validator = new PipelineStepValidator(Substitute.For<IPipelineRepository>(), Substitute.For<IAppRepository>(),
            _tables, _fields, _access, Substitute.For<ITenantRepository>(), Substitute.For<IQueryContext>());

        await validator.ValidateCreateRecordRequiredFieldsAsync(CreateConfig(), CancellationToken.None);

        await _access.Received(1).RequireMembershipByTablePublicIdAsync(_tableId, Arg.Any<CancellationToken>());
    }

    private string CopyConfig() => JsonSerializer.Serialize(new
    {
        connectionPublicId = PipelineStepValidator.SystemConnectionIds.First(),
        sourceTable = _sourceId, destinationTable = _tableId,
        sourceFields = new[] { "fid_6" }, destinationFields = new[] { "fid_6" }, mergeField = "fid_6"
    });

    [Fact]
    public async Task CopyRecords_ChecksTableLevelViewOnSourceAndAddModifyOnDestination()
    {
        TableAccess(_source, new TableAccessContext { Unrestricted = false, ViewScope = RecordScopes.None });
        TableAccess(_table, new TableAccessContext { Unrestricted = false, CanAdd = true, ModifyScope = RecordScopes.AllRecords });

        var act = () => _validator.ValidateCopyRecordsStepAsync(CopyConfig(), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedActionException>().WithMessage("*view records in table 'A1'*");
    }

    [Fact]
    public async Task CopyRecords_DestinationWithoutModify_IsRejected()
    {
        TableAccess(_source, new TableAccessContext { Unrestricted = true });
        TableAccess(_table, new TableAccessContext { Unrestricted = false, CanAdd = true, ModifyScope = RecordScopes.None });

        var act = () => _validator.ValidateCopyRecordsStepAsync(CopyConfig(), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedActionException>().WithMessage("*add and modify records in table 'A2'*");
    }

    [Theory]
    [InlineData(PipelineRecordAccessKind.View, true)]
    [InlineData(PipelineRecordAccessKind.Add, false)]
    [InlineData(PipelineRecordAccessKind.Modify, false)]
    [InlineData(PipelineRecordAccessKind.Delete, false)]
    [InlineData(PipelineRecordAccessKind.AddAndModify, false)]
    public void ViewOnlyRole_OnlyMayView(PipelineRecordAccessKind kind, bool expected)
    {
        var access = new TableAccessContext
        {
            Unrestricted = false, CanAdd = false, CanDelete = false,
            ViewScope = RecordScopes.AllRecords, ModifyScope = RecordScopes.None
        };

        PipelineRecordAccess.IsAllowed(access, kind).Should().Be(expected);
    }
}
