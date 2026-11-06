using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Apps.Commands.CopyApp;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Infrastructure.Services;

namespace PowerBase.UnitTests.Apps;

/// <summary>Copy App with data, "including file attachments": every file and revision gets its own copy (plan D5).
/// Runs against the real local-disk storage in a temporary folder.</summary>
public sealed class AppCopyFileCopierTests : IDisposable
{
    private static readonly Guid CopyAppId = Guid.Parse("0f8a3c2e-5b1d-4e7a-9c6f-2d4b8e1a7c3f");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pb-copyfiles-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileStorageService _storage;

    public AppCopyFileCopierTests()
    {
        Directory.CreateDirectory(_folder);
        _storage = new LocalFileStorageService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:LocalPath"] = _folder })
            .Build());
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string Store(string name, string content)
    {
        File.WriteAllText(Path.Combine(_folder, name), content);
        return $"/files/{name}";
    }

    private string Read(string path) => File.ReadAllText(Path.Combine(_folder, Path.GetFileName(path)));

    private static AppCopyFileCopier Copier(IFileStorageService storage)
        => new(storage, CopyAppId, retryDelay: _ => TimeSpan.Zero);

    [Fact]
    public async Task CopyPageAsync_CopiesTheCurrentFileAndEveryRevisionUnderTheCopysPrefix()
    {
        var current = Store("c.pdf", "version 3");
        var older = Store("a.pdf", "version 1");
        var value = $$"""{"name":"v3.pdf","path":"{{current}}","revisions":[{"name":"v1.pdf","path":"{{older}}"}]}""";

        var page = await Copier(_storage).CopyPageAsync([new AppCopyFileValue(1, value)]);

        page.Missing.Should().BeEmpty();
        page.FilesCopied.Should().Be(2);
        var newPaths = FileReservationContract.AllPaths(page.Values[0].Value);
        newPaths.Should().HaveCount(2).And.NotContain(current).And.NotContain(older);
        newPaths.Should().OnlyContain(p => Path.GetFileName(p).StartsWith(AppCopyFileCopier.FilePrefix(CopyAppId)));
        newPaths.Should().OnlyContain(p => p.EndsWith(".pdf"));
        var copied = JsonNode.Parse(page.Values[0].Value!)!;
        Read(copied["path"]!.GetValue<string>()).Should().Be("version 3");
        Read(copied["revisions"]![0]!["path"]!.GetValue<string>()).Should().Be("version 1");
        Read(current).Should().Be("version 3", "the original's file is not touched");
    }

    [Fact]
    public async Task CopyPageAsync_DeletingTheCopysFile_LeavesTheOriginalsFile()
    {
        var original = Store("doc.txt", "text");
        var page = await Copier(_storage).CopyPageAsync([new AppCopyFileValue(1, $$"""{"name":"doc.txt","path":"{{original}}"}""")]);

        await _storage.DeleteAsync(FileReservationContract.AllPaths(page.Values[0].Value).Single());

        File.Exists(Path.Combine(_folder, "doc.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task CopyPageAsync_MissingRevision_IsLeftOutAndReported()
    {
        var current = Store("c.pdf", "now");
        var value = $$"""{"name":"v2.pdf","path":"{{current}}","revisions":[{"name":"v1.pdf","path":"/files/gone.pdf"}]}""";

        var page = await Copier(_storage).CopyPageAsync([new AppCopyFileValue(5, value)]);

        page.Missing.Should().Equal(new AppCopyMissingFile(5, "v1.pdf (older version)"));
        var copied = JsonNode.Parse(page.Values[0].Value!)!.AsObject();
        copied.ContainsKey("revisions").Should().BeFalse();
        Read(copied["path"]!.GetValue<string>()).Should().Be("now");
    }

    [Fact]
    public async Task CopyPageAsync_MissingCurrentFile_LeavesTheValueEmptyAndDeletesTheUnusedRevisionCopy()
    {
        var older = Store("a.pdf", "old");
        var value = $$"""{"name":"v2.pdf","path":"/files/gone.pdf","revisions":[{"name":"v1.pdf","path":"{{older}}"}]}""";

        var page = await Copier(_storage).CopyPageAsync([new AppCopyFileValue(9, value)]);

        page.Values[0].Value.Should().BeNull();
        page.Missing.Should().Equal(new AppCopyMissingFile(9, "v2.pdf"));
        page.FilesCopied.Should().Be(0);
        Directory.GetFiles(_folder, AppCopyFileCopier.FilePrefix(CopyAppId) + "*").Should().BeEmpty();
    }

    [Fact]
    public async Task CopyPageAsync_SameFileInTwoRecords_IsCopiedOnce()
    {
        var shared = Store("same.png", "png");
        var value = $$"""{"name":"same.png","path":"{{shared}}"}""";

        var page = await Copier(_storage).CopyPageAsync([new AppCopyFileValue(1, value), new AppCopyFileValue(2, value)]);

        page.Values[0].Value.Should().Be(page.Values[1].Value);
        Directory.GetFiles(_folder, AppCopyFileCopier.FilePrefix(CopyAppId) + "*").Should().HaveCount(1);
    }

    [Fact]
    public async Task CopyPageAsync_StorageFailsOnce_RetriesAndCopies()
    {
        var path = Store("x.txt", "x");
        var flaky = new FlakyStorage(_storage, failures: 1);

        var page = await Copier(flaky).CopyPageAsync([new AppCopyFileValue(1, $$"""{"name":"x.txt","path":"{{path}}"}""")]);

        flaky.Attempts.Should().Be(2);
        page.Missing.Should().BeEmpty();
        Read(FileReservationContract.AllPaths(page.Values[0].Value).Single()).Should().Be("x");
    }

    [Fact]
    public async Task CopyPageAsync_StorageKeepsFailing_Throws()
    {
        var path = Store("x.txt", "x");
        var flaky = new FlakyStorage(_storage, failures: int.MaxValue);

        var act = () => Copier(flaky).CopyPageAsync([new AppCopyFileValue(1, $$"""{"name":"x.txt","path":"{{path}}"}""")]);

        await act.Should().ThrowAsync<IOException>();
        flaky.Attempts.Should().Be(AppCopyFileCopier.COPY_ATTEMPTS);
    }

    [Fact]
    public void Constructor_StorageThatCannotCopy_Throws()
    {
        var act = () => new AppCopyFileCopier(new SaveOnlyStorage(), CopyAppId);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task DeleteByPrefixAsync_DeletesOnlyTheCopysFiles()
    {
        Store("keep.txt", "keep");
        var copied = await _storage.CopyAsync("/files/keep.txt", AppCopyFileCopier.FilePrefix(CopyAppId) + "1.txt");
        var otherCopy = await _storage.CopyAsync("/files/keep.txt", AppCopyFileCopier.FilePrefix(Guid.NewGuid()) + "1.txt");

        await _storage.DeleteByPrefixAsync(AppCopyFileCopier.FilePrefix(CopyAppId));

        File.Exists(Path.Combine(_folder, Path.GetFileName(copied))).Should().BeFalse();
        File.Exists(Path.Combine(_folder, Path.GetFileName(otherCopy))).Should().BeTrue();
        File.Exists(Path.Combine(_folder, "keep.txt")).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("appcopy-")]
    [InlineData("appcopy-*-0123456789abcdef")]
    [InlineData("..\\appcopy-0123456789abcdef")]
    public async Task DeleteByPrefixAsync_UnsafePrefix_Throws(string prefix)
    {
        var act = () => _storage.DeleteByPrefixAsync(prefix);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CopyAsync_MissingSource_ThrowsFileNotFound()
    {
        var act = () => _storage.CopyAsync("/files/none.txt", "appcopy-x.txt");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    /// <summary>Fails the first <c>failures</c> copies with an IOException, then copies.</summary>
    private sealed class FlakyStorage(LocalFileStorageService inner, int failures) : IFileStorageService, IFileStorageCopyService
    {
        private int _attempts;
        public int Attempts => _attempts;

        public Task<string> CopyAsync(string sourcePath, string newName, CancellationToken ct = default)
            => Interlocked.Increment(ref _attempts) <= failures
                ? throw new IOException("storage busy")
                : inner.CopyAsync(sourcePath, newName, ct);

        public Task DeleteByPrefixAsync(string prefix, CancellationToken ct = default) => inner.DeleteByPrefixAsync(prefix, ct);
        public Task<StoredFile> SaveAsync(Stream content, string fileName, string? contentType, CancellationToken ct = default,
            string? uniqueKey = null) => inner.SaveAsync(content, fileName, contentType, ct, uniqueKey);
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => inner.DeleteAsync(relativePath, ct);
    }

    private sealed class SaveOnlyStorage : IFileStorageService
    {
        public Task<StoredFile> SaveAsync(Stream content, string fileName, string? contentType, CancellationToken ct = default,
            string? uniqueKey = null) => throw new NotSupportedException();
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
