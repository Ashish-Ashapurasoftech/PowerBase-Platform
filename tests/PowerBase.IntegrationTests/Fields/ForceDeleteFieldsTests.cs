using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;
using Xunit;

namespace PowerBase.IntegrationTests.Fields;

[Collection("PowerBase")]
public class ForceDeleteFieldsTests : IntegrationTestBase
{
    public ForceDeleteFieldsTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task DeleteField_ForceTrue_DeactivatesPipelineAndInvalidatesSteps()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);

        // Create field
        var fieldResponse = await PostAsync($"/tables/{tableId}/fields", new
        {
            typeCode = "Text",
            name = "EmailAddress"
        }, token);
        fieldResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var field = await ReadData<FieldDto>(fieldResponse);

        // Create pipeline referencing the field
        var pipelineResponse = await PostAsync($"/apps/{appId}/pipelines", new
        {
            name = "Deactivate Test Pipeline",
            description = "Deactivate on force-delete",
            isActive = true
        }, token);
        pipelineResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var pipeline = await ReadData<PipelineDto>(pipelineResponse);

        // Save step with config referencing the field
        var stepsResponse = await PutAsync($"/pipelines/{pipeline.Id}/steps", new
        {
            steps = new[]
            {
                new
                {
                    label = "Test Step",
                    type = "SendEmail",
                    isValidated = true,
                    configJson = $"{{\"body\": \"Send to {{steps.trigger.fid_{field.Id}}}\"}}"
                }
            }
        }, token);
        stepsResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Perform Force Delete
        var deleteResponse = await DeleteAsync($"/tables/{tableId}/fields/{field.Id}?force=true", token);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify deactivation and invalidation
        var getPipelinesResponse = await GetAsync($"/apps/{appId}/pipelines", token);
        getPipelinesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pipelines = await ReadListData<PipelineDto>(getPipelinesResponse);
        var updatedPipeline = pipelines.Single(p => p.Id == pipeline.Id);
        updatedPipeline.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteField_ForceFalse_WithDependency_Returns400BadRequest()
    {
        var (token, _) = await SignupAsync();
        var appId = await CreateAppAsync(token);
        var tableId = await CreateTableAsync(token, appId);

        // Create field
        var fieldResponse = await PostAsync($"/tables/{tableId}/fields", new
        {
            typeCode = "Text",
            name = "Phone"
        }, token);
        fieldResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var field = await ReadData<FieldDto>(fieldResponse);

        // Create pipeline
        var pipelineResponse = await PostAsync($"/apps/{appId}/pipelines", new
        {
            name = "Dependency Test Pipeline",
            isActive = true
        }, token);
        var pipeline = await ReadData<PipelineDto>(pipelineResponse);

        // Reference the field
        await PutAsync($"/pipelines/{pipeline.Id}/steps", new
        {
            steps = new[]
            {
                new
                {
                    label = "Step A",
                    type = "SMS",
                    isValidated = true,
                    configJson = $"{{\"number\": \"{{steps.trigger.fid_{field.Id}}}\"}}"
                }
            }
        }, token);

        // Non-force delete must be blocked
        var deleteResponse = await DeleteAsync($"/tables/{tableId}/fields/{field.Id}?force=false", token);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private record FieldDto(long Id, Guid PublicId, string Name);
    private record PipelineDto(long Id, Guid PublicId, string Name, bool IsActive);
}
