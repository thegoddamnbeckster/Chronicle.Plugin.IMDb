using System.IO.Compression;
using System.Text;
using Chronicle.Plugin.IMDb.Index;

namespace Chronicle.Plugin.IMDb.Tests;

/// <summary>
/// Small slices of IMDb's dataset files, in IMDb's exact format (tabs, <c>\N</c> nulls, 0x02
/// between multiple aka types), covering the cases the design calls out: a movie with a
/// same-named series and game, a show with numbered and unnumbered episodes, an adult title, a
/// music video next to a same-named film, multi-character credits and alternate titles.
/// </summary>
internal static class ImdbFixture
{
    static ImdbFixture() => SQLitePCL.Batteries_V2.Init();

    public const string Basics = """
        tconst	titleType	primaryTitle	originalTitle	isAdult	startYear	endYear	runtimeMinutes	genres
        tt0133093	movie	The Matrix	The Matrix	0	1999	\N	136	Action,Sci-Fi
        tt0234215	movie	The Matrix Reloaded	The Matrix Reloaded	0	2003	\N	138	Action,Sci-Fi
        tt9999901	tvSeries	The Matrix	The Matrix	0	2020	2021	30	Drama
        tt9999906	videoGame	The Matrix: Path of Neo	The Matrix: Path of Neo	0	2005	\N	\N	Action,Adventure
        tt0903747	tvSeries	Breaking Bad	Breaking Bad	0	2008	2013	49	Crime,Drama,Thriller
        tt0959621	tvEpisode	Pilot	Pilot	0	2008	\N	58	Crime,Drama,Thriller
        tt1054724	tvEpisode	Cat's in the Bag...	Cat's in the Bag...	0	2008	\N	48	Crime,Drama,Thriller
        tt2301451	tvEpisode	Ozymandias	Ozymandias	0	2013	\N	47	Crime,Drama,Thriller
        tt9999902	tvEpisode	Good Cop Bad Cop	Good Cop Bad Cop	0	2009	\N	3	Comedy
        tt0211915	movie	Amélie	Le fabuleux destin d'Amélie Poulain	0	2001	\N	122	Comedy,Romance
        tt9999903	movie	Adult Film X	Adult Film X	1	2010	\N	90	Adult
        tt9999904	video	Bohemian Rhapsody	Bohemian Rhapsody	0	1975	\N	6	Music
        tt9999905	movie	Bohemian Rhapsody	Bohemian Rhapsody	0	2018	\N	134	Biography,Drama,Music
        tt9999907	movie	"Quoted" Title	"Quoted" Title	0	2015	\N	90	Drama
        tt1602620	movie	Amour	Amour	0	2012	\N	127	Drama,Romance
        tt3774694	movie	Love	Love	0	2015	\N	135	Drama,Romance
        broken-row-with-too-few-columns
        """;

    public const string Akas = """
        titleId	ordering	title	region	language	types	attributes	isOriginalTitle
        tt0133093	1	Matrix	DE	\N	imdbDisplay	\N	0
        tt0133093	2	The Matrix	\N	\N	original	\N	1
        tt0133093	3	Матрица	RU	\N	imdbDisplay	\N	0
        tt0211915	1	Amelie	US	en	imdbDisplay\u0002working	literal title	0
        tt0211915	2	Le fabuleux destin d'Amélie Poulain	\N	\N	original	\N	1
        tt0959621	1	Piloto	ES	\N	\N	\N	0
        tt1602620	1	Love	XWW	en	imdbDisplay	\N	0
        """;

    public const string Episode = """
        tconst	parentTconst	seasonNumber	episodeNumber
        tt0959621	tt0903747	1	1
        tt1054724	tt0903747	1	2
        tt2301451	tt0903747	5	14
        tt9999902	tt0903747	\N	\N
        """;

    public const string Ratings = """
        tconst	averageRating	numVotes
        tt0133093	8.7	2282340
        tt0234215	7.2	650000
        tt9999901	5.0	100
        tt0903747	9.5	2683831
        tt0959621	9.0	60000
        tt1054724	8.6	40000
        tt2301451	10.0	509006
        tt9999904	9.0	1000
        tt9999905	7.9	600000
        tt9999903	4.0	50
        tt1602620	7.9	111414
        tt3774694	6.0	74965
        """;

    public const string Crew = """
        tconst	directors	writers
        tt0133093	nm0905154,nm0905152	nm0905152,nm0905154
        tt0903747	\N	nm0319213
        tt0959621	nm0319213	nm0319213
        """;

    public const string Principals = """
        tconst	ordering	nconst	category	job	characters
        tt0133093	1	nm0000206	actor	\N	["Neo"]
        tt0133093	2	nm0000401	actor	\N	["Morpheus"]
        tt0133093	3	nm0905154	director	\N	\N
        tt0133093	4	nm0005428	producer	executive producer	\N
        tt0959621	1	nm0186505	actor	\N	["Walter White"]
        tt2301451	1	nm0186505	actor	\N	["Walter White"]
        tt9999904	1	nm9999990	self	\N	["Self"]
        tt9999906	1	nm0000206	actor	\N	["Neo","Thomas Anderson"]
        tt9999903	1	nm9999991	actor	\N	["Lead"]
        """;

    public const string Names = """
        nconst	primaryName	birthYear	deathYear	primaryProfession	knownForTitles
        nm0000206	Keanu Reeves	1964	\N	actor,producer,soundtrack	tt0133093,tt0234215
        nm0000401	Laurence Fishburne	1961	\N	actor,producer	tt0133093
        nm0905154	Lana Wachowski	1965	\N	writer,director,producer	tt0133093
        nm0905152	Lilly Wachowski	1967	\N	writer,director,producer	tt0133093
        nm0005428	Joel Silver	1952	\N	producer	tt0133093
        nm0186505	Bryan Cranston	1956	\N	actor,producer,director	tt0903747
        nm0319213	Vince Gilligan	1967	\N	writer,producer,director	tt0903747
        nm9999990	Queen	\N	\N	soundtrack	tt9999904
        nm9999991	Adult Actor	1980	\N	actor	tt9999903
        nm9999992	Nobody Credited	1990	\N	actor	\N
        """;

    public static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>
    {
        [ImdbDatasetSource.Basics] = Basics, [ImdbDatasetSource.Akas] = Akas,
        [ImdbDatasetSource.Episode] = Episode, [ImdbDatasetSource.Ratings] = Ratings,
        [ImdbDatasetSource.Crew] = Crew, [ImdbDatasetSource.Principals] = Principals,
        [ImdbDatasetSource.Names] = Names,
    };

    /// <summary>Gzipped bytes of a fixture, with the literal "\u0002" turned into the real
    /// control character IMDb uses.</summary>
    public static byte[] Gzip(string tsv)
    {
        var text = tsv.Replace("\\u0002", "\u0002").Replace("\r\n", "\n") + "\n";
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(text));
        return ms.ToArray();
    }

    public static DatasetFiles WriteFiles(string dir, IReadOnlyDictionary<string, string>? overrides = null)
    {
        Directory.CreateDirectory(dir);
        string Write(string name)
        {
            var path = Path.Combine(dir, name + ".tsv.gz");
            File.WriteAllBytes(path, Gzip(overrides?.GetValueOrDefault(name) ?? Files[name]));
            return path;
        }
        return new DatasetFiles(
            Write(ImdbDatasetSource.Basics), Write(ImdbDatasetSource.Akas), Write(ImdbDatasetSource.Crew),
            Write(ImdbDatasetSource.Episode), Write(ImdbDatasetSource.Principals),
            Write(ImdbDatasetSource.Ratings), Write(ImdbDatasetSource.Names));
    }

    /// <summary>Builds an index from the fixtures into <paramref name="dataDir"/> and makes it live.</summary>
    public static ImdbIndexStore BuildIndex(string dataDir, ImdbScope? scope = null)
    {
        var store = new ImdbIndexStore(dataDir);
        var files = WriteFiles(Path.Combine(dataDir, "fixture-src"));
        var output = store.NewBuildPath();
        new ImdbIndexBuilder(scope ?? ImdbScope.Everything, _ => { })
            .Build(files, output, new Dictionary<string, string>(), CancellationToken.None);
        store.Activate(output);
        return store;
    }
}
