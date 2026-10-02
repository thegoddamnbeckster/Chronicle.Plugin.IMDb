namespace Chronicle.Plugin.IMDb.Index;

/// <summary>
/// Layout of the local index (<c>{data_dir}/imdb-*.db</c>). Ids are stored as integers
/// (tt0133093 → 133093, nm0000206 → 206) and rebuilt as text on output. Bump
/// <see cref="Version"/> whenever the layout changes: an index stamped with another version is
/// treated as missing and the next sync rebuilds it (PLUGIN_IMDB.md §10.4).
/// </summary>
internal static class ImdbSchema
{
    public const int Version = 1;

    /// <summary>
    /// Tables only. Secondary indexes are created after the bulk load (<see cref="Indexes"/>),
    /// which is much faster than maintaining them row by row. The three largest tables are
    /// clustered on (title, ordering) WITHOUT ROWID: IMDb's files arrive sorted that way, so the
    /// load is an append, per-title reads are contiguous, and no separate title index is needed
    /// (measured on the full 2026-10-02 data: that index alone was over 100 million entries).
    /// </summary>
    public const string Tables = """
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE title_types (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
        CREATE TABLE categories (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
        CREATE TABLE titles (
            id INTEGER PRIMARY KEY,
            type INTEGER NOT NULL,
            primary_title TEXT NOT NULL,
            original_title TEXT,            -- NULL when identical to primary_title
            is_adult INTEGER NOT NULL,
            start_year INTEGER,
            end_year INTEGER,
            runtime INTEGER,
            genres TEXT                     -- IMDb's own names, comma-separated
        );
        CREATE TABLE episodes (
            id INTEGER PRIMARY KEY,         -- the episode's own title id
            parent INTEGER NOT NULL,
            season INTEGER,
            episode INTEGER
        );
        CREATE TABLE ratings (id INTEGER PRIMARY KEY, rating REAL NOT NULL, votes INTEGER NOT NULL);
        CREATE TABLE akas (
            title_id INTEGER NOT NULL,
            ordering INTEGER NOT NULL,
            title TEXT NOT NULL,
            region TEXT,
            language TEXT,
            types TEXT,
            attributes TEXT,
            is_original INTEGER NOT NULL,
            PRIMARY KEY (title_id, ordering)
        ) WITHOUT ROWID;
        CREATE TABLE crew (
            title_id INTEGER NOT NULL, role INTEGER NOT NULL, ordering INTEGER NOT NULL, person_id INTEGER NOT NULL,
            PRIMARY KEY (title_id, role, ordering)
        ) WITHOUT ROWID;
        CREATE TABLE principals (
            title_id INTEGER NOT NULL,
            ordering INTEGER NOT NULL,
            person_id INTEGER NOT NULL,
            category INTEGER NOT NULL,
            job TEXT,
            characters TEXT,                -- IMDb's JSON array text, e.g. ["Neo"]
            PRIMARY KEY (title_id, ordering)
        ) WITHOUT ROWID;
        CREATE TABLE names (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            birth_year INTEGER,
            death_year INTEGER,
            professions TEXT,
            known_for TEXT                  -- comma-separated title numbers
        );
        -- Every searchable spelling of every non-episode title: primary, original and akas,
        -- normalised with the same Normalize the scoring uses (PLUGIN_IMDB.md §10.10).
        CREATE TABLE search_names (norm TEXT NOT NULL, raw TEXT NOT NULL, title_id INTEGER NOT NULL);
        """;

    public const string Indexes = """
        CREATE INDEX ix_episodes_parent ON episodes (parent, season, episode);
        CREATE INDEX ix_crew_person ON crew (person_id);
        CREATE INDEX ix_principals_person ON principals (person_id);
        CREATE INDEX ix_search_norm ON search_names (norm);
        CREATE VIRTUAL TABLE search_fts USING fts5 (
            norm, content='search_names', content_rowid='rowid',
            tokenize='unicode61 remove_diacritics 2');
        INSERT INTO search_fts (search_fts) VALUES ('rebuild');
        """;

    /// <summary>crew.role values.</summary>
    public const int RoleDirector = 0, RoleWriter = 1;
}
