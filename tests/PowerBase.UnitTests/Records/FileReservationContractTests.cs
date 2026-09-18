using FluentAssertions;
using PowerBase.Application.Records;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PowerBase.UnitTests.Records;

public class FileReservationContractTests
{
    [Fact]
    public void PreserveReservation_StampsUploaderOnNewRevision()
    {
        var uploadedOn = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

        var value = FileReservationContract.PreserveReservation(
            "{\"name\":\"v1.pdf\",\"path\":\"/files/v1.pdf\"}",
            "{\"name\":\"v2.pdf\",\"path\":\"/files/v2.pdf\"}",
            "Hardik Hardik",
            uploadedOn)!.ToString()!;

        using var document = JsonDocument.Parse(value);
        document.RootElement.GetProperty("uploadedBy").GetString().Should().Be("Hardik Hardik");
        document.RootElement.GetProperty("uploadedOn").GetDateTime().Should().Be(uploadedOn);
    }

    [Fact]
    public void ReserveReadAndRelease_RoundTripReservationMetadata()
    {
        const string attachment = "{\"name\":\"plan.docx\",\"path\":\"/files/plan.docx\",\"size\":10,\"type\":\"application/vnd.openxmlformats-officedocument.wordprocessingml.document\"}";
        var reservedOn = DateTime.UtcNow;

        var reserved = FileReservationContract.Reserve(attachment,
            new FileReservation(42, "Alex", "Editing chapter 2", reservedOn));

        var actual = FileReservationContract.Read(reserved);
        actual.Should().NotBeNull();
        actual!.UserId.Should().Be(42);
        actual.UserName.Should().Be("Alex");
        actual.Comment.Should().Be("Editing chapter 2");
        actual.ReservedOn.Should().BeCloseTo(reservedOn, TimeSpan.FromMilliseconds(1));
        FileReservationContract.Read(FileReservationContract.Release(reserved)).Should().BeNull();
    }

    [Fact]
    public void PreserveReservation_KeepsCheckoutWhenOwnerUploadsNewVersion()
    {
        var oldValue = FileReservationContract.Reserve(
            "{\"name\":\"v1.docx\",\"path\":\"/files/v1.docx\"}",
            new FileReservation(7, "Sam", null, DateTime.UtcNow));

        var nextValue = FileReservationContract.PreserveReservation(
            oldValue, "{\"name\":\"v2.docx\",\"path\":\"/files/v2.docx\"}");

        FileReservationContract.Read(nextValue).Should().NotBeNull();
        nextValue!.ToString().Should().Contain("v2.docx");
    }

    [Fact]
    public void PreserveReservation_KeepsTwoPreviousFilesAndDoesNotVersionMetadataChanges()
    {
        object value = "{\"name\":\"v1.docx\",\"path\":\"/files/v1.docx\",\"size\":10}";
        foreach (var version in new[] { 2, 3, 4 })
            value = FileReservationContract.PreserveReservation(value,
                $"{{\"name\":\"v{version}.docx\",\"path\":\"/files/v{version}.docx\",\"size\":{version * 10}}}")!;

        var current = JsonNode.Parse(value.ToString()!)!.AsObject();
        current["path"]!.ToString().Should().Be("/files/v4.docx");
        current["revisions"]!.AsArray().Select(item => item!["path"]!.ToString())
            .Should().Equal("/files/v2.docx", "/files/v3.docx");

        var reserved = FileReservationContract.Reserve(value, new FileReservation(7, "Sam", null, DateTime.UtcNow));
        var unchanged = FileReservationContract.PreserveReservation(value, reserved)!.ToString()!;
        JsonNode.Parse(unchanged)!["revisions"]!.AsArray().Count.Should().Be(2);
        FileReservationContract.Read(unchanged).Should().NotBeNull();
    }

    [Fact]
    public void DeleteRevision_RemovesOlderVersionAndPromotesPreviousWhenCurrentIsDeleted()
    {
        const string value = "{\"name\":\"v3\",\"path\":\"/files/v3\",\"revisions\":[{\"name\":\"v1\",\"path\":\"/files/v1\"},{\"name\":\"v2\",\"path\":\"/files/v2\"}]}";
        var withoutOld = FileReservationContract.DeleteRevision(value, "/files/v1");
        var current = JsonNode.Parse(withoutOld!)!.AsObject();
        current["path"]!.ToString().Should().Be("/files/v3");
        current["revisions"]!.AsArray().Count.Should().Be(1);

        var promoted = FileReservationContract.DeleteRevision(withoutOld, "/files/v3");
        JsonNode.Parse(promoted!)!["path"]!.ToString().Should().Be("/files/v2");
        FileReservationContract.DeleteRevision(promoted, "/files/v2").Should().BeNull();
    }

    [Fact]
    public void DeleteRevision_PreservesLockOnPromotedFileAndRejectsStaleRevision()
    {
        const string value = "{\"name\":\"v1\",\"path\":\"/files/v1\"}";
        const string withHistory = "{\"name\":\"v2\",\"path\":\"/files/v2\",\"revisions\":[{\"name\":\"v1\",\"path\":\"/files/v1\"}]}";
        var lockedWithHistory = FileReservationContract.Reserve(withHistory, new FileReservation(7, "Sam", null, DateTime.UtcNow));
        FileReservationContract.Read(FileReservationContract.DeleteRevision(lockedWithHistory, "/files/v2"))
            .Should().NotBeNull();
        FluentActions.Invoking(() => FileReservationContract.DeleteRevision(value, "/files/missing"))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DeleteRevisions_RemovesSelectionTogetherAndPromotesNewestRemaining()
    {
        const string value = "{\"name\":\"v3\",\"path\":\"/files/v3\",\"revisions\":[{\"name\":\"v1\",\"path\":\"/files/v1\"},{\"name\":\"v2\",\"path\":\"/files/v2\"}]}";
        var remaining = FileReservationContract.DeleteRevisions(value, ["/files/v1", "/files/v3"]);
        JsonNode.Parse(remaining!)!["path"]!.ToString().Should().Be("/files/v2");
        JsonNode.Parse(remaining!)!["revisions"].Should().BeNull();
        FileReservationContract.DeleteRevisions(value, ["/files/v1", "/files/v2", "/files/v3"])
            .Should().BeNull();
    }

    [Fact]
    public void DeleteRevisions_RejectsStaleSelectionWithoutChangingAnyRevision()
    {
        const string value = "{\"name\":\"v2\",\"path\":\"/files/v2\",\"revisions\":[{\"name\":\"v1\",\"path\":\"/files/v1\"}]}";
        FluentActions.Invoking(() => FileReservationContract.DeleteRevisions(value, ["/files/v1", "/files/missing"]))
            .Should().Throw<InvalidOperationException>();
        JsonNode.Parse(value)!["revisions"]!.AsArray().Count.Should().Be(1);
    }
}
