using System.Text.Json;
using Chronicle.Plugins.Models;
using FluentAssertions;
using Xunit;

namespace Chronicle.Plugin.IMDb.Tests;

/// <summary>End to end: fixture TSVs → real index build → provider lookups.</summary>
public class ImdbMetadataProviderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("imdb-provider-tests-").FullName;
    private readonly ImdbMetadataProvider _provider = new();

    public ImdbMetadataProviderTests()
    {
        ImdbFixture.BuildIndex(_dir);
        _provider.ConfigureForTesting(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static MediaSearchContext Ctx(string name, int? year = null, string? type = "movies",
        Dictionary<string, string>? ids = null, int level = 0, int? number = null, string? precise = null) =>
        new(name, Year: year, MediaTypeName: type, KnownExternalIds: ids, HierarchyLevel: level,
            ItemNumber: number, PreciseName: precise, AltTitles: [name]);

    private static JsonElement Ext(MediaMetadata m) => m.ExtendedData!.Value;

    // ── Search: shared cascade ────────────────────────────────────────────────

    [Fact]
    public async Task Search_Movie_ExactTitleAndYear_ScoresLikeTmdb()
    {
        var results = await _provider.SearchAsync(Ctx("The Matrix", 1999));

        results[0].Metadata.ExternalId.Should().Be("imdb:tt0133093");
        results[0].Score.Should().Be(80);
        results[0].ScoreReason.Should().Be("title exact, year exact");
    }

    [Theory]
    [InlineData("movies", 1)]        // a movie inside a collection
    [InlineData("anime_movies", 2)]  // inside a nested collection
    public async Task Search_MovieInsideACollection_IsStillMatchedByTitle(string type, int level)
    {
        var results = await _provider.SearchAsync(Ctx("The Matrix", 1999, type: type, level: level));
        results[0].Metadata.ExternalId.Should().Be("imdb:tt0133093");
    }

    [Fact]
    public async Task Search_Movie_NeverReturnsTheSameNamedSeriesOrGame()
    {
        var results = await _provider.SearchAsync(Ctx("The Matrix", 2020));

        results.Select(r => r.Metadata.ExternalId).Should().NotContain(["imdb:tt9999901", "imdb:tt9999906"]);
        results.Should().Contain(r => r.Metadata.ExternalId == "imdb:tt0133093");
    }

    [Fact]
    public async Task Search_Tv_FindsTheSeriesNotTheMovie()
    {
        var results = await _provider.SearchAsync(Ctx("The Matrix", 2020, type: "tv"));

        results.Should().ContainSingle().Which.Metadata.ExternalId.Should().Be("imdb:tt9999901");
    }

    [Fact]
    public async Task Search_WithoutYearHit_FallsBackToYearlessStage()
    {
        // No title "The Matrix" from 1990; stage 1b finds it with a year-mismatch penalty.
        var results = await _provider.SearchAsync(Ctx("The Matrix", 1990));

        var matrix = results.Single(r => r.Metadata.ExternalId == "imdb:tt0133093");
        matrix.Score.Should().Be(50);
        matrix.ScoreReason.Should().Be("title exact, year mismatch");
    }

    [Fact]
    public async Task Search_ContainsMatch_ScoresThirtyAndRanksByVotes()
    {
        var results = await _provider.SearchAsync(Ctx("Matrix Reloaded"));

        results[0].Metadata.ExternalId.Should().Be("imdb:tt0234215");
        results[0].Score.Should().Be(30);
    }

    [Fact]
    public async Task Search_ByOriginalOrAlternateTitle_ScoresAsExact()
    {
        var byOriginal = await _provider.SearchAsync(Ctx("Le fabuleux destin d'Amélie Poulain", 2001));
        byOriginal[0].Metadata.ExternalId.Should().Be("imdb:tt0211915");
        byOriginal[0].Score.Should().Be(80);
        byOriginal[0].Metadata.Title.Should().Be("Amélie", "the candidate carries IMDb's primary title; precedence decides display");

        var byAka = await _provider.SearchAsync(Ctx("Матрица", 1999));
        byAka[0].Metadata.ExternalId.Should().Be("imdb:tt0133093");
        byAka[0].Score.Should().Be(80);
    }

    [Fact]
    public async Task Search_TiedScores_PreferTheTitlesOwnNameOverARegionalAlternate()
    {
        // Real data: Amour has a regional title "Love" and more votes than Love (2015).
        var results = await _provider.SearchAsync(Ctx("Love"));

        results.Take(2).Select(r => (r.Metadata.ExternalId, r.Score))
            .Should().Equal(("imdb:tt3774694", 60), ("imdb:tt1602620", 60));
    }

    [Fact]
    public async Task Search_DiacriticsMatchThroughFullText()
    {
        var results = await _provider.SearchAsync(Ctx("Amelie", 2001));
        results[0].Metadata.ExternalId.Should().Be("imdb:tt0211915");
    }

    [Fact]
    public async Task Search_PreciseName_AddsTheTmdbBonus()
    {
        var results = await _provider.SearchAsync(Ctx("The Matrix", 1999, precise: "The Matrix"));
        results[0].Score.Should().Be(95);
        results[0].ScoreReason.Should().Be("title exact, year exact, precise name exact");
    }

    [Fact]
    public async Task Search_YearSuffixInTheName_IsStrippedAndUsed()
    {
        var results = await _provider.SearchAsync(new MediaSearchContext("The Matrix (1999)", MediaTypeName: "movies"));
        results[0].Metadata.ExternalId.Should().Be("imdb:tt0133093");
        results[0].ScoreReason.Should().StartWith("title exact");
    }

    [Fact]
    public async Task Search_QuotesInTitles_DontBreakFullTextQueries()
    {
        var results = await _provider.SearchAsync(Ctx("\"Quoted\" Title", 2015));
        results[0].Metadata.ExternalId.Should().Be("imdb:tt9999907");
    }

    [Fact]
    public async Task Search_KnownImdbId_IsUsedDirectly()
    {
        var results = await _provider.SearchAsync(Ctx("Something Else", ids: new() { ["imdb"] = "tt0133093" }));

        results.Should().ContainSingle();
        results[0].Score.Should().Be(100);
        results[0].Metadata.ExternalId.Should().Be("imdb:tt0133093");
    }

    [Fact]
    public async Task Search_KnownImdbIdOfTheWrongKind_LeavesTheItemUnmatched()
    {
        // A series id on a movie item: searching would overwrite the shared imdb id other
        // plugins rely on, so IMDb stays out of it.
        var results = await _provider.SearchAsync(Ctx("The Matrix", 1999, ids: new() { ["imdb"] = "tt9999901" }));
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_KnownImdbIdImdbDropped_FallsBackToSearch()
    {
        var results = await _provider.SearchAsync(Ctx("The Matrix", 1999, ids: new() { ["imdb"] = "tt0000001" }));
        results[0].Metadata.ExternalId.Should().Be("imdb:tt0133093");
        results[0].ScoreReason.Should().Be("title exact, year exact");
    }

    // ── Music videos and games ────────────────────────────────────────────────

    [Fact]
    public async Task Search_MusicVideos_OnlyMatchMusicGenreVideos_AndMoviesExcludeThem()
    {
        var mv = await _provider.SearchAsync(Ctx("Bohemian Rhapsody", type: "music_videos"));
        mv.Should().ContainSingle().Which.Metadata.ExternalId.Should().Be("imdb:tt9999904");

        var movies = await _provider.SearchAsync(Ctx("Bohemian Rhapsody", type: "movies"));
        movies.Select(r => r.Metadata.ExternalId).Should().Equal("imdb:tt9999905");
    }

    [Fact]
    public async Task Search_Game_FindsVideoGames()
    {
        var results = await _provider.SearchAsync(Ctx("The Matrix: Path of Neo", 2005, type: "game"));
        results.Should().ContainSingle().Which.Metadata.ExternalId.Should().Be("imdb:tt9999906");
    }

    [Fact]
    public async Task Search_UnsupportedMediaType_ReturnsNothing()
    {
        var results = await _provider.SearchAsync(Ctx("The Matrix", type: "music"));
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetById_MusicVideo_FillsArtistFromSelfCredits()
    {
        var m = await _provider.GetByIdAsync("imdb:tt9999904");
        Ext(m).GetProperty("artist")[0].GetProperty("name").GetString().Should().Be("Queen");
        Ext(m).GetProperty("artist")[0].GetProperty("externalPersonId").GetString().Should().Be("imdb:nm9999990");
    }

    // ── Get by id: titles ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_Movie_MapsEveryDatum()
    {
        var m = await _provider.GetByIdAsync("https://www.imdb.com/title/tt0133093/?ref_=x");

        m.ExternalId.Should().Be("imdb:tt0133093");
        m.Source.Should().Be("imdb");
        m.Title.Should().Be("The Matrix");
        m.Year.Should().Be(1999);
        m.RuntimeMinutes.Should().Be(136);
        m.Rating.Should().Be(8.7);
        m.Genres.Should().Equal("Action", "Sci-Fi");
        m.Overview.Should().BeNull();
        m.PosterUrl.Should().BeNull();

        m.Cast.Should().Equal(
            new CastMember("Keanu Reeves", "Neo", "imdb:nm0000206"),
            new CastMember("Laurence Fishburne", "Morpheus", "imdb:nm0000401"));
        m.Crew.Should().Contain(new CrewMember("Lana Wachowski", "Director", "imdb:nm0905154"));
        m.Crew.Should().Contain(new CrewMember("Lilly Wachowski", "Director", "imdb:nm0905152"));
        m.Crew.Should().Contain(new CrewMember("Lilly Wachowski", "Writer", "imdb:nm0905152"));
        m.Crew.Should().Contain(new CrewMember("Joel Silver", "Executive Producer", "imdb:nm0005428"));
        m.Crew.Count(c => c.Name == "Lana Wachowski" && c.Job == "Director").Should().Be(1,
            "a director in both principals and title.crew is credited once");

        var ext = Ext(m);
        ext.GetProperty("ids").GetProperty("imdb").GetString().Should().Be("tt0133093");
        ext.GetProperty("originalTitle").GetString().Should().Be("The Matrix");
        ext.GetProperty("titleFormat").GetString().Should().Be("movie");
        ext.GetProperty("isAdult").GetBoolean().Should().BeFalse();
        ext.GetProperty("votes").GetInt32().Should().Be(2282340);
        ext.GetProperty("ratings")[0].GetProperty("url").GetString().Should().Be("https://www.imdb.com/title/tt0133093/");
        ext.GetProperty("alternateTitles").GetArrayLength().Should().Be(3);
        ext.GetProperty("principals").GetArrayLength().Should().Be(4);
        ext.GetProperty("attribution").GetString().Should().Contain("Information courtesy of IMDb");
    }

    [Fact]
    public async Task GetById_AlternateTitles_SplitImdbsMultiValueSeparator()
    {
        var m = await _provider.GetByIdAsync("tt0211915");

        var amelie = Ext(m).GetProperty("alternateTitles").EnumerateArray()
            .Single(a => a.GetProperty("title").GetString() == "Amelie");
        amelie.GetProperty("types").EnumerateArray().Select(t => t.GetString()).Should().Equal("imdbDisplay", "working");
        amelie.GetProperty("region").GetString().Should().Be("US");
        amelie.GetProperty("language").GetString().Should().Be("en");
        m.AlternateNames.Should().Equal("Le fabuleux destin d'Amélie Poulain");
    }

    [Fact]
    public async Task GetById_MultiCharacterCredit_KeepsEveryCharacter()
    {
        var m = await _provider.GetByIdAsync("tt9999906");
        m.Cast.Single().Role.Should().Be("Neo / Thomas Anderson");
        Ext(m).GetProperty("principals")[0].GetProperty("characters").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task GetById_Series_HasEndYearEpisodeCountAndUnplacedEpisodes()
    {
        var m = await _provider.GetByIdAsync("imdb:tt0903747");
        var ext = Ext(m);

        ext.GetProperty("endYear").GetInt32().Should().Be(2013);
        ext.GetProperty("episodeCount").GetInt32().Should().Be(4);
        ext.GetProperty("seasonCount").GetInt32().Should().Be(2);
        var unplaced = ext.GetProperty("unplacedEpisodes");
        unplaced.GetArrayLength().Should().Be(1);
        unplaced[0].GetProperty("id").GetString().Should().Be("tt9999902");
        unplaced[0].GetProperty("title").GetString().Should().Be("Good Cop Bad Cop");
        m.Crew.Should().ContainSingle(c => c.Name == "Vince Gilligan" && c.Job == "Writer");
    }

    [Fact]
    public async Task GetById_TitleImdbDropped_KeepsTheItemsDataInsteadOfLettingChronicleWipeIt()
    {
        // Not KeyNotFoundException: Chronicle would re-search and, finding nothing, delete the
        // item's IMDb data and its shared imdb id (PLUGIN_IMDB.md §10.3).
        var act = () => _provider.GetByIdAsync("tt0000001");
        (await act.Should().ThrowAsync<ImdbIdMissingException>()).Which.Message.Should().StartWith("imdb-missing: imdb:tt0000001");
        await act.Should().NotThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetById_SeasonImdbDoesntNumber_IsKeyNotFoundSoChronicleLooksAgain()
    {
        var act = () => _provider.GetByIdAsync("imdb:tt0903747/season:3");
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetById_Garbage_ThrowsArgumentException()
    {
        var act = () => _provider.GetByIdAsync("https://www.imdb.com/list/ls055592025/");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Seasons & episodes ────────────────────────────────────────────────────

    [Fact]
    public async Task Search_Season_UsesTheParentShowsId()
    {
        var results = await _provider.SearchAsync(Ctx("Season 1", type: "tv", level: 1, number: 1,
            ids: new() { ["parent_imdb"] = "tt0903747" }));

        var season = results.Should().ContainSingle().Which.Metadata;
        season.ExternalId.Should().Be("imdb:tt0903747/season:1");
        season.Title.Should().Be("Season 1");
        season.Year.Should().Be(2008);
        season.Rating.Should().BeNull("IMDb doesn't rate seasons; the computed score is kept separately");

        var score = Ext(season).GetProperty("ratings")[0];
        score.GetProperty("variant").GetString().Should().Be("episodes-avg");
        // (9.0 × 60000 + 8.6 × 40000) / 100000
        score.GetProperty("value").GetDouble().Should().Be(8.84);
        score.GetProperty("votes").GetInt64().Should().Be(100000);
        Ext(season).GetProperty("episodeCount").GetInt32().Should().Be(2);
        Ext(season).GetProperty("show").GetString().Should().Be("tt0903747");
        Ext(season).TryGetProperty("ids", out _).Should().BeFalse("a season has no IMDb id of its own");
    }

    [Fact]
    public async Task Search_Season_WithoutParentId_ReturnsNothing()
    {
        var results = await _provider.SearchAsync(Ctx("Season 1", type: "tv", level: 1, number: 1));
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_Episode_ByShowSeasonAndNumber()
    {
        var results = await _provider.SearchAsync(Ctx("Ozymandias", type: "tv", level: 2, number: 14,
            ids: new() { ["parent_imdb"] = "tt0903747/season:5" }));

        results.Should().ContainSingle();
        results[0].Metadata.ExternalId.Should().Be("imdb:tt2301451");
        results[0].ScoreReason.Should().Be("S05E14 match");
    }

    [Fact]
    public async Task Search_Episode_UnnumberedEpisodeMatchesByExactTitleOnly()
    {
        var byTitle = await _provider.SearchAsync(Ctx("Good Cop Bad Cop", 2009, type: "tv", level: 2, number: 1,
            ids: new() { ["parent_imdb"] = "tt0903747/season:0" }));
        byTitle.Should().ContainSingle().Which.Score.Should().Be(80);
        byTitle[0].Metadata.ExternalId.Should().Be("imdb:tt9999902");

        // Never assigned by position: an unknown number with a different title finds nothing.
        var byPosition = await _provider.SearchAsync(Ctx("Some Special", type: "tv", level: 2, number: 1,
            ids: new() { ["parent_imdb"] = "tt0903747/season:0" }));
        byPosition.Should().BeEmpty();
    }

    [Fact]
    public async Task GetById_Episode_CarriesShowSeasonAndNumber()
    {
        var m = await _provider.GetByIdAsync("imdb:tt2301451");

        m.Title.Should().Be("Ozymandias");
        m.Rating.Should().Be(10.0);
        m.Cast.Should().ContainSingle(c => c.Name == "Bryan Cranston" && c.Role == "Walter White");
        var ext = Ext(m);
        ext.GetProperty("show").GetString().Should().Be("tt0903747");
        ext.GetProperty("seasonNumber").GetInt32().Should().Be(5);
        ext.GetProperty("episodeNumber").GetInt32().Should().Be(14);
    }

    [Fact]
    public async Task GetById_Season()
    {
        var m = await _provider.GetByIdAsync("imdb:tt0903747/season:5");
        m.Title.Should().Be("Season 5");
        m.Year.Should().Be(2013);
    }

    [Fact]
    public async Task GetEpisodeList_ReturnsNumberedEpisodesOfTheSeason()
    {
        var list = await _provider.GetEpisodeListAsync("imdb:tt0903747", 1);
        list.Should().Equal(new ProviderEpisodeSummary(1, "Pilot"), new ProviderEpisodeSummary(2, "Cat's in the Bag..."));

        (await _provider.GetEpisodeListAsync("tv:1396", 1)).Should().BeEmpty("not an IMDb id");
        (await _provider.GetEpisodeListAsync("imdb:tt0903747", 9)).Should().BeEmpty();
    }

    // ── People ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("imdb:nm0000206")]   // as Chronicle stores a person's IMDb id
    [InlineData("nm0000206")]
    public async Task Search_Person_IsIdOnly(string storedId)
    {
        var results = await _provider.SearchAsync(Ctx("Keanu Reeves", type: "people", ids: new() { ["imdb"] = storedId }));

        var person = results.Should().ContainSingle().Which;
        person.Score.Should().Be(100);
        person.Metadata.ExternalId.Should().Be("imdb:nm0000206");
        person.Metadata.Title.Should().Be("Keanu Reeves");
        person.Metadata.Tags.Should().Equal("Actor", "Producer", "Soundtrack");
        var ext = Ext(person.Metadata);
        ext.GetProperty("birthYear").GetInt32().Should().Be(1964);
        ext.TryGetProperty("birthDate", out _).Should().BeFalse("a bare year isn't a date Chronicle can parse");
        ext.GetProperty("knownFor")[0].GetProperty("title").GetString().Should().Be("The Matrix");
    }

    [Fact]
    public async Task Search_Person_WithoutId_NeverSearchesByName()
    {
        var results = await _provider.SearchAsync(Ctx("Keanu Reeves", type: "people"));
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task PersonCredits_CoverPrincipalsAndCrew_EpisodesRolledUpToTheShow()
    {
        var cranston = await _provider.GetPersonCreditsAsync("imdb:nm0186505");
        cranston.Should().ContainSingle()
            .Which.Should().Be(new ProviderPersonCredit("imdb", "tt0903747", "tv", "Breaking Bad", 2008, null,
                "Actor", "Walter White"));

        var gilligan = await _provider.GetPersonCreditsAsync("imdb:nm0319213");
        gilligan.Select(c => (c.ExternalId, c.Role)).Should().BeEquivalentTo(
            [("tt0903747", "Writer"), ("tt0903747", "Director")]);

        var keanu = await _provider.GetPersonCreditsAsync("nm0000206");
        keanu.Should().Contain(c => c.ExternalId == "tt9999906" && c.MediaType == "game");
    }

    // ── Index lifecycle ───────────────────────────────────────────────────────

    [Fact]
    public async Task NoIndex_SearchThrowsProviderUnavailable_SoItemsStayPending()
    {
        var empty = Directory.CreateTempSubdirectory("imdb-empty-").FullName;
        try
        {
            var p = new ImdbMetadataProvider();
            p.ConfigureForTesting(empty);

            var search = () => p.SearchAsync(Ctx("The Matrix"));
            (await search.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().BeNull();
            (await p.HealthCheckAsync()).Should().BeFalse();
            (await p.GetEpisodeListAsync("imdb:tt0903747", 1)).Should().BeEmpty();
        }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    public async Task Healthy_WhenIndexExists() => (await _provider.HealthCheckAsync()).Should().BeTrue();

    [Fact]
    public void SettingsSchema_OpensWithTheDiskSpaceNotice()
    {
        var first = _provider.GetSettingsSchema().Settings[0];
        first.Type.Should().Be(SettingType.Notice);
        first.Description.Should().Contain("about 10 GB").And.Contain("Information courtesy of IMDb");
    }

    [Fact]
    public void MediaTypes_DeclareMusicVideosAndVideoGames()
    {
        var types = _provider.GetSupportedMediaTypes();
        types.Should().Contain(t => t.MediaTypeName == "music_videos" && t.DisplayName == "Music Videos");
        types.Should().Contain(t => t.MediaTypeName == "game" && t.DisplayName == "Video Games" && t.InteractionVerb == "played");
        types.Single(t => t.MediaTypeName == "people").DisplayName.Should().BeEmpty("IMDb contributes to people, it doesn't register the type");
    }
}
