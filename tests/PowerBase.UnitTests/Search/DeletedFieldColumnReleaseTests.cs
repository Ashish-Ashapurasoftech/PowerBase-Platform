using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Search;
using PowerBase.Application.Search.Commands.BackfillSearchIndex;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Search;

/// <summary>
/// A deleted field's index column is not reserved forever: once its values are emptied from every document it goes back to the pool.
/// New deletes are freed by the indexer at the end of their cleanup; columns of fields deleted earlier are emptied and freed by the
/// admin reindex, and only when every page made it into the index.
/// </summary>
public class DeletedFieldColumnReleaseTests
{
    [Fact]
    public void Clearing_columns_empties_them_in_the_current_index_and_keeps_a_value_a_document_writes()
    {
        var id = Guid.NewGuid();
        var shared = new SearchIndexDocument(1, 2, 3, id, new Dictionary<long, object?>(), new Dictionary<string, object?> { ["str_01"] = "live", ["str_04"] = "reused" });
        var own = new SearchIndexDocument(1, 2, 3, id, new Dictionary<long, object?>(), new Dictionary<string, object?> { ["f_6"] = "x" }, "pb-tbl-3");

        var docs = SearchIndexDocumentBuilder.ClearColumns([shared, own], null, ["str_03", "str_04"]);

        docs[0].SlotValues!.Should().ContainKey("str_03").WhoseValue.Should().BeNull();
        docs[0].SlotValues!["str_04"].Should().Be("reused", "a live field writing the column keeps its value");
        docs[0].SlotValues!["str_01"].Should().Be("live");
        docs[1].Should().BeSameAs(own, "the table's own index has no shared columns");
    }

    private sealed class Fixture
    {
        public readonly IAppRepository Apps = Substitute.For<IAppRepository>();
        public readonly IAppTableRepository Tables = Substitute.For<IAppTableRepository>();
        public readonly IAppFieldRepository Fields = Substitute.For<IAppFieldRepository>();
        public readonly IRecordRepository Records = Substitute.For<IRecordRepository>();
        public readonly IAzureSearchService Search = Substitute.For<IAzureSearchService>();

        public Fixture()
        {
            Apps.ListAsync(Arg.Is(1), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<App> { new() { Id = 2, Name = "A" } });
            Apps.ListAsync(Arg.Is(2), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<App>());
            Tables.ListByAppAsync(2, Arg.Any<CancellationToken>()).Returns(new List<AppTable> { new() { Id = 10, AppId = 2, Name = "T" } });
            Fields.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>
            {
                new() { Id = 1, Fid = 6, TypeCode = "Text", IsSearchable = true, IsFilterable = true, SearchSlot = "str_01" }
            });
            // Field 9 was deleted before freeing existed: it still holds str_03.
            Fields.ListDeletedSlotHoldersAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>
            {
                new() { Id = 2, Fid = 9, TypeCode = "Text", IsDeleted = true, SearchSlot = "str_03" }
            });
            Records.GetTableIndexPageAsync(Arg.Is(1L), Arg.Is(10L), Arg.Is(0L), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<int>())
                .Returns((new List<SearchIndexDocument> { new(1, 2, 10, Guid.NewGuid(), new Dictionary<long, object?> { [6] = "kept" }) }, 1L));
            Search.BulkIndexRecordsWithResultsAsync(Arg.Any<IEnumerable<SearchIndexDocument>>(), Arg.Any<CancellationToken>())
                .Returns(new List<SearchIndexFailure>());
        }

        public Task RunAsync() =>
            new BackfillSearchIndexCommandHandler(Substitute.For<IQueryContext>(), Apps, Tables, Fields, Records, Search,
                Substitute.For<ILogger<BackfillSearchIndexCommandHandler>>()).HandleAsync(new BackfillSearchIndexCommand(1));
    }

    [Fact]
    public async Task The_admin_reindex_empties_a_deleted_fields_old_column_and_then_frees_it()
    {
        var f = new Fixture();

        await f.RunAsync();

        await f.Search.Received(1).BulkIndexRecordsWithResultsAsync(
            Arg.Is<IEnumerable<SearchIndexDocument>>(docs => docs.Single().SlotValues!.ContainsKey("str_03") && docs.Single().SlotValues!["str_03"] == null
                && (string?)docs.Single().SlotValues!["str_01"] == "kept"),
            Arg.Any<CancellationToken>());
        await f.Fields.Received(1).ReleaseDeletedFieldSlotAsync(10, 9, "str_03", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_column_is_not_freed_when_a_page_did_not_make_it_into_the_index()
    {
        var f = new Fixture();
        f.Search.BulkIndexRecordsWithResultsAsync(Arg.Any<IEnumerable<SearchIndexDocument>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("search service down"));

        await f.RunAsync();

        await f.Fields.DidNotReceiveWithAnyArgs().ReleaseDeletedFieldSlotAsync(default, default, default!, default);
    }
}
