using System.Net;
using System.Net.Http.Headers;
using Chronicle.Plugin.IMDb.Index;
using Chronicle.Plugins.Models;
using FluentAssertions;
using Xunit;

namespace Chronicle.Plugin.IMDb.Tests;

/// <summary>The sync and ratings tasks against a fake datasets.imdbws.com.</summary>
public class ImdbTaskTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("imdb-task-tests-").FullName;
    private readonly FakeImdb _imdb = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private ImdbSyncDatasetsTask SyncTask(ImdbScope? scope = null)
    {
        var task = new ImdbSyncDatasetsTask();
        task.ConfigureForTesting(_dir, scope ?? ImdbScope.Everything, new ImdbDatasetSource(new HttpClient(_imdb), "https://fake/"));
        return task;
    }

    private ImdbMetadataProvider Provider()
    {
        var p = new ImdbMetadataProvider();
        p.ConfigureForTesting(_dir);
        return p;
    }

    private string[] IndexFiles() => Directory.GetFiles(_dir, "imdb-*.db");

    [Fact]
    public async Task Sync_BuildsTheIndex_DeletesDownloads_AndLogs()
    {
        await SyncTask().RunAsync(CancellationToken.None);

        (await Provider().HealthCheckAsync()).Should().BeTrue();
        (await Provider().GetByIdAsync("tt0133093")).Title.Should().Be("The Matrix");
        IndexFiles().Should().ContainSingle();
        Directory.GetFiles(Path.Combine(_dir, "downloads")).Should().BeEmpty();
        File.ReadAllText(Path.Combine(_dir, "imdb-sync.log")).Should().Contain("Sync: finished");
    }

    [Fact]
    public async Task Sync_SecondRunWithUnchangedFiles_DoesNothing()
    {
        await SyncTask().RunAsync(CancellationToken.None);
        var first = IndexFiles().Single();
        _imdb.Gets = 0;

        await SyncTask().RunAsync(CancellationToken.None);

        IndexFiles().Should().Equal(first);
        _imdb.Gets.Should().Be(0, "only HEAD requests are needed to know nothing changed");
    }

    [Fact]
    public async Task Sync_NewImdbFiles_RebuildAndSwap_OldIndexRemoved()
    {
        await SyncTask().RunAsync(CancellationToken.None);
        var first = IndexFiles().Single();

        _imdb.LastModified = _imdb.LastModified.AddDays(1);
        await SyncTask().RunAsync(CancellationToken.None);

        IndexFiles().Should().ContainSingle().Which.Should().NotBe(first);
    }

    [Fact]
    public async Task Sync_ScopeChange_Rebuilds_WithTheNewScope()
    {
        await SyncTask().RunAsync(CancellationToken.None);
        (await Provider().SearchAsync(new MediaSearchContext("Adult Film X", MediaTypeName: "movies"))).Should().NotBeEmpty();

        await SyncTask(ImdbScope.Everything with { IncludeAdult = false }).RunAsync(CancellationToken.None);

        (await Provider().SearchAsync(new MediaSearchContext("Adult Film X", MediaTypeName: "movies"))).Should().BeEmpty();
        var keanu = await Provider().GetByIdAsync("nm0000206");
        keanu.Title.Should().Be("Keanu Reeves");
        var nobody = () => Provider().GetByIdAsync("nm9999992");
        await nobody.Should().ThrowAsync<ImdbIdMissingException>("a reduced scope drops people nobody kept is credited with");
    }

    [Fact]
    public async Task Sync_FailedBuild_LeavesTheWorkingIndexInPlace()
    {
        await SyncTask().RunAsync(CancellationToken.None);
        var first = IndexFiles().Single();

        _imdb.LastModified = _imdb.LastModified.AddDays(1);
        _imdb.Corrupt = ImdbDatasetSource.Principals;
        var act = () => SyncTask().RunAsync(CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();

        IndexFiles().Should().Equal(first);
        (await Provider().GetByIdAsync("tt0133093")).Title.Should().Be("The Matrix");
    }

    [Fact]
    public async Task Sync_RepeatedKeyInAFile_IsSkippedAndLogged_NotFatal()
    {
        _imdb.Overrides[ImdbDatasetSource.Principals] = ImdbFixture.Principals + "\ntt0133093\t1\tnm0000401\tactor\t\\N\t[\"Duplicate\"]";

        await SyncTask().RunAsync(CancellationToken.None);

        (await Provider().GetByIdAsync("tt0133093")).Cast[0].Role.Should().Be("Neo", "the first row for a key wins");
        File.ReadAllText(Path.Combine(_dir, "imdb-sync.log")).Should().Contain("principals done").And.Contain("1 repeated keys skipped");
    }

    [Fact]
    public async Task Sync_RefusesToRunTwiceAtOnce()
    {
        using var held = new ImdbIndexStore(_dir).TryLock();
        var act = () => SyncTask().RunAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already running*");
    }

    [Fact]
    public async Task Sync_EpisodeCreditsOff_KeepsEpisodesButNotTheirCast()
    {
        await SyncTask(ImdbScope.Everything with { EpisodeCredits = false }).RunAsync(CancellationToken.None);

        var ozymandias = await Provider().GetByIdAsync("tt2301451");
        ozymandias.Title.Should().Be("Ozymandias");
        ozymandias.Rating.Should().Be(10.0);
        ozymandias.Cast.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_TitleTypeAndRegionScope()
    {
        var scope = ImdbScope.FromSettings(new Dictionary<string, string>
        {
            [ImdbScope.KeyTitleTypes] = "movie, tvseries",   // case-insensitive
            [ImdbScope.KeyAkaRegions] = "DE",
        });
        await SyncTask(scope).RunAsync(CancellationToken.None);

        var p = Provider();
        (await p.SearchAsync(new MediaSearchContext("The Matrix: Path of Neo", MediaTypeName: "game"))).Should().BeEmpty();
        var matrix = await p.GetByIdAsync("tt0133093");
        matrix.ExtendedData!.Value.GetProperty("alternateTitles").EnumerateArray()
            .Select(a => a.GetProperty("title").GetString())
            .Should().BeEquivalentTo(["Matrix", "The Matrix"], "the DE title plus the original, which is always kept");
    }

    [Fact]
    public async Task RefreshRatings_UpdatesScoresInPlace()
    {
        await SyncTask().RunAsync(CancellationToken.None);
        var index = IndexFiles().Single();

        _imdb.Overrides[ImdbDatasetSource.Ratings] = "tconst\taverageRating\tnumVotes\ntt0133093\t8.8\t2300000";
        _imdb.LastModified = _imdb.LastModified.AddDays(1);
        var task = new ImdbRefreshRatingsTask();
        task.ConfigureForTesting(_dir, new ImdbDatasetSource(new HttpClient(_imdb), "https://fake/"));
        await task.RunAsync(CancellationToken.None);

        IndexFiles().Should().Equal(index);
        var matrix = await Provider().GetByIdAsync("tt0133093");
        matrix.Rating.Should().Be(8.8);
        matrix.ExtendedData!.Value.GetProperty("votes").GetInt32().Should().Be(2300000);
        (await Provider().GetByIdAsync("tt0903747")).Rating.Should().BeNull("a title missing from the new file has no score");
    }

    [Fact]
    public async Task RefreshRatings_WithoutAnIndex_DoesNothing()
    {
        var task = new ImdbRefreshRatingsTask();
        task.ConfigureForTesting(_dir, new ImdbDatasetSource(new HttpClient(_imdb), "https://fake/"));
        await task.RunAsync(CancellationToken.None);
        IndexFiles().Should().BeEmpty();
    }

    /// <summary>Serves the fixture files with a Last-Modified header, like datasets.imdbws.com.</summary>
    private sealed class FakeImdb : HttpMessageHandler
    {
        public DateTimeOffset LastModified = new(2026, 10, 2, 0, 47, 9, TimeSpan.Zero);
        public string? Corrupt;
        public int Gets;
        public Dictionary<string, string> Overrides { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath).Replace(".tsv.gz", "");
            if (!ImdbFixture.Files.ContainsKey(name))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            if (request.Method == HttpMethod.Get) Gets++;
            var bytes = name == Corrupt
                ? [0x1f, 0x8b, 0x08, 0x00, 0xde, 0xad]   // a truncated gzip stream
                : ImdbFixture.Gzip(Overrides.GetValueOrDefault(name) ?? ImdbFixture.Files[name]);
            var content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : bytes);
            content.Headers.LastModified = LastModified;
            content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
