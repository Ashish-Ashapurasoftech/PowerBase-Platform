using System.Net;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Import;

/// <summary>
/// Cross-application connected tables created by QBL import: a table's source application is resolved against every
/// application the tenant already has, not only the ones in the current file — including an individual, single-app
/// import of the dependent application after the fact — and an unresolved dependency is completed automatically once
/// it is imported, instead of being lost. See <c>docs/</c> connected-tables notes and
/// <c>PblConnectedTablesApplier</c> / <c>PendingConnectedTableResolver</c>.
/// </summary>
[Collection("PowerBase")]
public class ConnectedTableImportTests : IntegrationTestBase
{
    public ConnectedTableImportTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    private record MinimalTableDto(Guid PublicId, string Name);
    private record SkippedDto(string LogicalRef, string Name, string Reason);
    private record ImportReportDto(Guid AppPublicId, string AppName, int TablesCreated, int FieldsCreated, List<SkippedDto> Skipped);

    private static string SourceQbl(string appName, string tableName = "Clients", string fieldLabel = "Name") => $"""
        Version: '0.12'
        Resources:
          $App_Source:
            Type: QB::Application
            Properties:
              Name: {appName}
            Tables:
              $Table_Source:
                Type: QB::Table
                Properties:
                  Name: {tableName}
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Id: 6
                    Properties:
                      Label: {fieldLabel}
        """;

    /// <summary>A consumer app with one table connected to <paramref name="sourceAppName"/>'s table (fid 6, "Name").</summary>
    private static string ConsumerQbl(string appName, string sourceAppName, string sourceTableName = "Clients") => $"""
        Version: '0.12'
        Resources:
          $App_Consumer:
            Type: QB::Application
            Properties:
              Name: {appName}
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                  ConnectedData:
                    DataSource: !Ref
                      DataSource: $DataSource_{sourceAppName.Replace(' ', '_')}
                      RecordSet: $RecordSet_Clients
                    Connected: true
                    UpdateMode: Destructive
                    FieldMap:
                      $Field_Name: !Ref
                        ConnectedColumn: $ConnectedColumn_Name
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Properties:
                      Label: Name
        ExternalResources:
          $DataSource_{sourceAppName.Replace(' ', '_')}:
            Type: QB::ConnectedDataSource::Quickbase
            Properties:
              IsPlaceholder: false
            RecordSets:
              $RecordSet_Clients:
                Type: QB::ConnectedRecordSet
                Properties:
                  Name: {sourceTableName}
                Columns:
                  $ConnectedColumn_Name:
                    Type: QB::ConnectedColumn::Text
                    Id: 6
                    Properties:
                      Name: Name
        """;

    private async Task<ImportReportDto> ImportSingleAsync(string token, string qbl)
    {
        var response = await PostFileAsync("/apps/import", qbl, token: token, fileName: "app.qbl");
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await ReadData<ImportReportDto>(response);
    }

    private async Task<Guid> ClientsTablePublicIdAsync(string token, Guid appPublicId)
    {
        var tables = await ReadListData<MinimalTableDto>(await GetAsync($"/apps/{appPublicId}/tables", token));
        return tables.Should().ContainSingle(t => t.Name == "Clients").Subject.PublicId;
    }

    private async Task<bool> IsReadOnlyAsync(string token, Guid tablePublicId)
    {
        var r = await PostAsync($"/tables/{tablePublicId}/records", new { fields = new Dictionary<string, object?>() }, token);
        if (r.StatusCode == HttpStatusCode.Conflict)
        {
            (await r.Content.ReadAsStringAsync()).Should().Contain("CONNECTED_TABLE_READ_ONLY");
            return true;
        }
        r.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        return false;
    }

    [Fact]
    public async Task IndividualImport_SourceAppAlreadyImportedEarlier_ConnectsAutomatically()
    {
        var (token, _) = await SignupAsync();
        var sourceAppName = $"Source {Guid.NewGuid():N}";
        await ImportSingleAsync(token, SourceQbl(sourceAppName)); // imported first, in its own call

        var consumer = await ImportSingleAsync(token, ConsumerQbl($"Consumer {Guid.NewGuid():N}", sourceAppName));

        consumer.Skipped.Should().NotContain(s => s.Reason.Contains("does not exist yet"));
        var tableId = await ClientsTablePublicIdAsync(token, consumer.AppPublicId);
        (await IsReadOnlyAsync(token, tableId)).Should().BeTrue("the table was converted to a connected table because its source application already existed");
    }

    [Fact]
    public async Task IndividualImport_SourceAppNotImportedYet_StaysOrdinaryThenConnectsOnceItIs()
    {
        var (token, _) = await SignupAsync();
        var sourceAppName = $"Source {Guid.NewGuid():N}";

        var consumer = await ImportSingleAsync(token, ConsumerQbl($"Consumer {Guid.NewGuid():N}", sourceAppName));
        consumer.Skipped.Should().ContainSingle(s => s.Reason.Contains("does not exist yet") && s.Reason.Contains(sourceAppName));
        var tableId = await ClientsTablePublicIdAsync(token, consumer.AppPublicId);
        (await IsReadOnlyAsync(token, tableId)).Should().BeFalse("the dependency does not exist yet, so the table stays an ordinary, writable table");

        await ImportSingleAsync(token, SourceQbl(sourceAppName)); // imported later, in a separate call

        (await IsReadOnlyAsync(token, tableId)).Should().BeTrue("the deferred connection should complete automatically once its source application is imported");
    }

    [Fact]
    public async Task IndividualImport_SourceAppNameAmbiguous_NeverGuessesAndStaysOrdinary()
    {
        var (token, _) = await SignupAsync();
        var baseName = $"Clients Source {Guid.NewGuid():N}";
        // Two existing applications whose names normalize to the same key (punctuation/case only) — no automatic match is possible.
        await ImportSingleAsync(token, SourceQbl(baseName));
        await ImportSingleAsync(token, SourceQbl(baseName.Replace(' ', '-')));

        var consumer = await ImportSingleAsync(token, ConsumerQbl($"Consumer {Guid.NewGuid():N}", baseName));

        consumer.Skipped.Should().ContainSingle(s => s.Reason.Contains("more than one application"));
        var tableId = await ClientsTablePublicIdAsync(token, consumer.AppPublicId);
        (await IsReadOnlyAsync(token, tableId)).Should().BeFalse("an ambiguous source application name must never be guessed");
    }
}
