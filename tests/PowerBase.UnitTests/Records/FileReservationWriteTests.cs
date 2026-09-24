using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Records;

public class FileReservationWriteTests
{
    [Fact]
    public async Task ApplyAsync_WhenReservedByAnotherUser_BlocksFileReplacement()
    {
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.UserId.Returns(22L);
        var field = new AppField { Id = 9, Fid = 9, Name = "Document", TypeCode = "File" };
        var table = new AppTable { Id = 3, PublicId = Guid.NewGuid(), Name = "Documents" };
        var recordId = Guid.NewGuid();
        var reservedValue = FileReservationContract.Reserve(
            "{\"name\":\"v1.docx\",\"path\":\"/files/v1.docx\"}",
            new FileReservation(11, "Locker", null, DateTime.UtcNow));
        IReadOnlyDictionary<string, object?> existing = new Dictionary<string, object?>
        {
            ["Id"] = 1L,
            [PhysicalNaming.GetPhysicalColumnName(field)] = reservedValue
        };
        var inner = new RecordWriteService(
            tableRepo, fieldRepo, recordRepo,
            Substitute.For<IRelationshipRepository>(),
            Substitute.For<IAppUserRepository>(), Substitute.For<IUserRepository>(),
            Substitute.For<IAuditRepository>(), Substitute.For<IPipelineTriggerInterceptor>(),
            new FormulaEngine(), Substitute.For<IAppRepository>(), queryContext);
        var service = new FileRecordWriteService(inner, recordRepo, queryContext);

        var action = () => service.ApplyFileWriteAsync(table, new[] { field }, recordId,
            new Dictionary<long, object?>
            {
                [9] = "{\"name\":\"v2.docx\",\"path\":\"/files/v2.docx\"}"
            }, AuditActions.Updated, "Record modified", existingRecord: existing);

        await action.Should().ThrowAsync<UnauthorizedActionException>()
            .WithMessage("*reserved by Locker*");
        await recordRepo.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default,
            default!, default, default, default);
    }
}
