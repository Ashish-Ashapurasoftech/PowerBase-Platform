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

    [Fact]
    public void RestoreRevision_CreatesNewRevisionAndPreservesSelectedOriginalInHistory()
    {
        const string value = "{\"name\":\"v3\",\"path\":\"/files/v3\",\"revisions\":[{\"name\":\"v1\",\"path\":\"/files/v1\"},{\"name\":\"v2\",\"path\":\"/files/v2\"}]}";

        var restored = JsonNode.Parse(FileReservationContract.RestoreRevision(
            value, "/files/v1", "Restoring User", new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc),
            revisionLimit: 4))!.AsObject();

        restored["path"]!.ToString().Should().Be("/files/v1");
        restored["restoredBy"]!.ToString().Should().Be("Restoring User");
        restored["revisions"]!.AsArray().Select(item => item!["path"]!.ToString())
            .Should().Equal("/files/v1", "/files/v2", "/files/v3");
        restored["revisions"]!.AsArray().Select(item => item!["revisionNumber"]!.GetValue<int>())
            .Should().Equal(1, 2, 3);
        restored["revisionNumber"]!.GetValue<int>().Should().Be(4);
        restored["revisionId"]!.ToString().Should().NotBe(
            restored["revisions"]![0]!["revisionId"]!.ToString());
    }

    [Fact]
    public void RestoreRevision_RepeatedRestoresAppendMonotonicRevisionsWithoutChangingOriginalMetadata()
    {
        var uploadedAbc = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc);
        var uploadedXyz = uploadedAbc.AddMinutes(5);
        var firstRestoreTime = uploadedAbc.AddMinutes(10);
        var secondRestoreTime = uploadedAbc.AddMinutes(15);

        var revision1 = FileReservationContract.PreserveReservation(null,
            "{\"name\":\"abc.png\",\"path\":\"/files/abc\",\"size\":10,\"type\":\"image/png\"}",
            "User A", uploadedAbc)!;
        var revision2 = FileReservationContract.PreserveReservation(revision1,
            "{\"name\":\"xyz.png\",\"path\":\"/files/xyz\",\"size\":20,\"type\":\"image/png\"}",
            "User A", uploadedXyz)!;

        var revision3 = FileReservationContract.RestoreRevision(revision2, "/files/abc", "User B", firstRestoreTime);
        var afterFirstRestore = JsonNode.Parse(revision3)!.AsObject();
        afterFirstRestore["revisionNumber"]!.GetValue<int>().Should().Be(3);
        afterFirstRestore["path"]!.ToString().Should().Be("/files/abc");
        afterFirstRestore["revisions"]!.AsArray().Select(item => item!["revisionNumber"]!.GetValue<int>())
            .Should().Equal(1, 2);
        afterFirstRestore["revisions"]![0]!["uploadedOn"]!.GetValue<DateTime>().Should().Be(uploadedAbc);

        var revision2Id = afterFirstRestore["revisions"]![1]!["revisionId"]!.ToString();
        var revision4 = FileReservationContract.RestoreRevision(revision3, revision2Id, "User B", secondRestoreTime,
            revisionLimit: 3);
        var afterSecondRestore = JsonNode.Parse(revision4)!.AsObject();

        afterSecondRestore["revisionNumber"]!.GetValue<int>().Should().Be(4);
        afterSecondRestore["path"]!.ToString().Should().Be("/files/xyz");
        afterSecondRestore["uploadedOn"]!.GetValue<DateTime>().Should().Be(secondRestoreTime);
        afterSecondRestore["uploadedBy"]!.ToString().Should().Be("User B");
        afterSecondRestore["revisions"]!.AsArray().Select(item => item!["revisionNumber"]!.GetValue<int>())
            .Should().Equal(2, 3);
        afterSecondRestore["revisions"]!.AsArray().Select(item => item!["path"]!.ToString())
            .Should().Equal("/files/xyz", "/files/abc");
        afterSecondRestore["revisions"]!.AsArray().Select(item => item!["revisionId"]!.ToString())
            .Append(afterSecondRestore["revisionId"]!.ToString()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void DeleteRevision_UsesLogicalIdentityWhenRestoreSharesPhysicalPath()
    {
        const string value = "{\"name\":\"xyz.png\",\"path\":\"/files/xyz\",\"revisions\":[{\"name\":\"abc.png\",\"path\":\"/files/abc\"}]}";
        var restoredValue = FileReservationContract.RestoreRevision(value, "/files/abc", "User A");
        var restored = JsonNode.Parse(restoredValue)!.AsObject();
        var originalRevisionId = restored["revisions"]![0]!["revisionId"]!.ToString();

        var afterDelete = FileReservationContract.DeleteRevision(restoredValue, originalRevisionId);

        JsonNode.Parse(afterDelete!)!["path"]!.ToString().Should().Be("/files/abc");
        FileReservationContract.ReferencedPaths(afterDelete).Should().Contain("/files/abc");
    }

    [Fact]
    public void RestoreRevision_MaxThreeRetainsLatestRevisionsAndNeverResetsSequence()
    {
        object value = FileReservationContract.PreserveReservation(null,
            "{\"name\":\"A.png\",\"path\":\"/files/A\"}", "User A")!;
        value = FileReservationContract.PreserveReservation(value,
            "{\"name\":\"B.png\",\"path\":\"/files/B\"}", "User A", revisionLimit: 3)!;
        value = FileReservationContract.PreserveReservation(value,
            "{\"name\":\"C.png\",\"path\":\"/files/C\"}", "User A", revisionLimit: 3)!;

        var revision3 = JsonNode.Parse(value.ToString()!)!.AsObject();
        var revision1Id = revision3["revisions"]![0]!["revisionId"]!.ToString();
        var revision2Id = revision3["revisions"]![1]!["revisionId"]!.ToString();
        var revision3Id = revision3["revisionId"]!.ToString();

        value = FileReservationContract.RestoreRevision(value, revision1Id, "User B", revisionLimit: 3);
        AssertRetainedSequence(value, 2, 3, 4);
        value = FileReservationContract.RestoreRevision(value, revision2Id, "User B", revisionLimit: 3);
        AssertRetainedSequence(value, 3, 4, 5);
        value = FileReservationContract.RestoreRevision(value, revision3Id, "User B", revisionLimit: 3);
        AssertRetainedSequence(value, 4, 5, 6);

        var final = JsonNode.Parse(value.ToString()!)!.AsObject();
        final["revisionNumber"]!.GetValue<int>().Should().Be(6);
        final["name"]!.ToString().Should().Be("C.png");
        final["uploadedBy"]!.ToString().Should().Be("User B");

        var revision5Id = final["revisions"]![1]!["revisionId"]!.ToString();
        var afterManualDelete = FileReservationContract.DeleteRevision(value, revision5Id)!;
        var afterDelete = JsonNode.Parse(afterManualDelete)!.AsObject();
        afterDelete["revisions"]!.AsArray().Select(item => item!["revisionNumber"]!.GetValue<int>())
            .Append(afterDelete["revisionNumber"]!.GetValue<int>()).Should().Equal(4, 6);

        var revision7 = JsonNode.Parse(FileReservationContract.PreserveReservation(afterManualDelete,
            "{\"name\":\"D.png\",\"path\":\"/files/D\"}", "User A", revisionLimit: 3)!.ToString()!)!.AsObject();
        revision7["revisionNumber"]!.GetValue<int>().Should().Be(7);
    }

    private static void AssertRetainedSequence(object value, params int[] expected)
    {
        var current = JsonNode.Parse(value.ToString()!)!.AsObject();
        current["revisions"]!.AsArray().Select(item => item!["revisionNumber"]!.GetValue<int>())
            .Append(current["revisionNumber"]!.GetValue<int>()).Should().Equal(expected);
    }

    [Fact]
    public void PreserveReservation_AppliesConfiguredTotalRevisionLimit()
    {
        object value = "{\"name\":\"v1\",\"path\":\"/files/v1\"}";
        value = FileReservationContract.PreserveReservation(value,
            "{\"name\":\"v2\",\"path\":\"/files/v2\"}", revisionLimit: 1)!;
        JsonNode.Parse(value.ToString()!)!["revisions"].Should().BeNull();

        foreach (var version in new[] { 3, 4, 5 })
            value = FileReservationContract.PreserveReservation(value,
                $"{{\"name\":\"v{version}\",\"path\":\"/files/v{version}\"}}", revisionLimit: 4)!;

        JsonNode.Parse(value.ToString()!)!["revisions"]!.AsArray().Count.Should().Be(3);
    }
}
