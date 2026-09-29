-- links is derived: WorkspaceSession forces a full reindex after any script runs, which fills it.
-- type is path, url or anchor; whether a path link's target is a file or missing is read from files when asked, so it
-- stays right when the target comes or goes without the linking page changing.
CREATE TABLE links (
    id INTEGER NOT NULL PRIMARY KEY,
    source_id INTEGER NOT NULL REFERENCES files (id) ON DELETE CASCADE,
    line INTEGER NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('body', 'frontmatter')),
    type TEXT NOT NULL CHECK (type IN ('path', 'url', 'anchor')),
    raw TEXT NOT NULL,
    -- The workspace key a path link resolves to; null for a path that leaves the workspace, and for every other type.
    target TEXT NULL CHECK (type = 'path' OR target IS NULL)
);

CREATE INDEX ix_links_source_id ON links (source_id);
CREATE INDEX ix_links_target ON links (target);

-- Facts about how the index was built, such as the link settings its links were extracted under.
CREATE TABLE meta (
    key TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
);
