using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

public class CreateRecordOutputTests
{
    [Fact]
    public void IncludesPersistedSystemAndCustomFieldsUsingCanonicalFids()
    {
        var publicId = Guid.NewGuid();
        var created = new DateTime(2026, 9, 22, 14, 40, 10, DateTimeKind.Utc);
        var fields = new List<AppField>
        {
            new() { Fid = 1, Name = "S_date_created", Label = "Date Created", IsSystem = true, PhysicalColumnName = "CreatedOn" },
            new() { Fid = 2, Name = "S_date_modified", Label = "Date Modified", IsSystem = true, PhysicalColumnName = "ModifiedOn" },
            new() { Fid = 3, Name = "S_record_id", Label = "Record ID#", IsSystem = true, PhysicalColumnName = "Id" },
            new() { Fid = 6, Name = "customer_name", Label = "Customer", PhysicalColumnName = "f_6" },
            new() { Fid = 7, Name = "optional", Label = "Optional", PhysicalColumnName = "f_7" }
        };
        var persisted = new Dictionary<string, object?>
        {
            ["Id"] = 19L, ["PublicId"] = publicId, ["CreatedOn"] = created,
            ["ModifiedOn"] = created, ["f_6"] = "Original customer", ["f_7"] = null
        };

        var output = PipelineEngine.BuildCreatedRecordOutput(publicId, 19, fields, new Dictionary<long, object?> { [6] = "Submitted customer" }, persisted);

        Assert.Equal(19L, output["fid_3"]);
        Assert.Equal(created, output["fid_1"]);
        Assert.Equal(created, output["fid_2"]);
        Assert.Equal("Original customer", output["fid_6"]);
        Assert.Null(output["fid_7"]);
        Assert.Equal(publicId.ToString(), output["RecordPublicId"]);
    }

    [Fact]
    public void FallsBackToSubmittedValuesWhenRepositoryRowIsUnavailable()
    {
        var fields = new List<AppField> { new() { Fid = 8, Name = "status", PhysicalColumnName = "f_8" } };
        var output = PipelineEngine.BuildCreatedRecordOutput(Guid.NewGuid(), 20, fields, new Dictionary<long, object?> { [8] = "Open" }, null);
        Assert.Equal("Open", output["fid_8"]);
    }
}
