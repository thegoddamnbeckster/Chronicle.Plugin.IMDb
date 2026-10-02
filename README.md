# Chronicle.Plugin.IMDb

Metadata provider for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle), built from
[IMDb's non-commercial datasets](https://developer.imdb.com/non-commercial-datasets/).

Information courtesy of IMDb (https://www.imdb.com). Used with permission.

## What it provides

| Media type | What IMDb supplies |
|------------|--------------------|
| Movies, Anime Movies | Title, original and alternate titles, year, runtime, genres, cast with characters, crew, IMDb rating and votes, format (movie / TV movie / short / special / direct-to-video), adult flag |
| TV, Anime | The same for shows, plus end year and episode count; seasons (episode count, year span, a vote-weighted season score Chronicle computes); episodes (title, number, year, runtime, rating, cast and crew) |
| Music Videos *(new type)* | The same as movies, plus the performing artist |
| Video Games *(new type, shared with the game plugins)* | Title, year, genres, voice cast, crew, rating |
| People | Birth and death years, professions, known-for titles, full filmography |

IMDb's datasets have no plot summaries, images, exact release dates, certificates or
companies. Other providers (TMDB, TheTVDB, Fanart.tv) supply those; Chronicle's Metadata
Assignment order decides which provider wins each field.

Full design: [docs/plugins/PLUGIN_IMDB.md](https://github.com/thegoddamnbeckster/Chronicle/blob/main/docs/plugins/PLUGIN_IMDB.md).

## How it works

IMDb's terms forbid scraping imdb.com, and third-party "IMDb APIs" scrape it, so this plugin uses
only the dataset files IMDb publishes for personal, non-commercial use. It downloads them and
builds a local SQLite index in the plugin's data folder; every lookup is then a local read with no
network calls, API keys or rate limits.

**Disk space:** about 2 GB of downloads (deleted after each build unless *Keep downloaded files*
is on) and an index of about 10 GB with everything kept (measured October 2026). A sync builds the
new index beside the current one, so it needs about 12 GB free on top of that. The scope settings
(adult titles, title types, alternate-title regions and languages, episode credits) make it
smaller.

## Setup

1. Install the plugin and open **Settings → Plugins → IMDb**.
2. Adjust the scope settings if you want a smaller index.
3. Run the **Sync IMDb Datasets** task once (about 10-15 minutes: roughly 4 to download, 8 to
   build). Until it has run, IMDb matches nothing and items stay queued.

### Tasks

| Task | Default | What it does |
|------|---------|--------------|
| Sync IMDb Datasets | Mondays 03:00, off until enabled | Downloads the datasets and rebuilds the index when IMDb has published new files or the scope settings changed. The current index stays in use until the new one is finished |
| Refresh IMDb Ratings | Daily 04:00 | Updates scores and votes in the index from IMDb's ~9 MB daily ratings file |
| Fetch Missing Metadata | Daily 05:00 | Matches items that have no IMDb data yet |
| Re-sync All Metadata | Off | Re-reads IMDb data for every matched item (local reads only) |

Progress, sizes and timings are written to `imdb-sync.log` in the plugin's data folder.

### Fix Match

Paste any IMDb link or id: title pages and their sub-pages, episode pages, mobile
(`m.imdb.com`) and IMDbPro links, person pages, or a bare `tt…` / `nm…` id. Lists, user pages,
companies, events and searches are rejected because they don't identify one title or person.

## Building

```powershell
dotnet build
dotnet test tests
```

Chronicle's `scripts/RunTestEnvironment.ps1` builds and deploys it with the other plugins.

## License

MIT. IMDb's data is subject to IMDb's own
[non-commercial licence](https://developer.imdb.com/non-commercial-datasets/).
