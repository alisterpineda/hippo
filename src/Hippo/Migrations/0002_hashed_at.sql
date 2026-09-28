-- files is derived: NotebookSession forces a full reindex after any script runs, so its rows need not survive.
DROP TABLE files;

CREATE TABLE files (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    path TEXT NOT NULL,
    mtime INTEGER NOT NULL,
    size INTEGER NOT NULL,
    hash TEXT NOT NULL,
    hashed_at INTEGER NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('markdown', 'plain')),
    frontmatter TEXT NULL,
    parse_error TEXT NULL
);

CREATE UNIQUE INDEX ix_files_path ON files (path);

-- Indexes built before 0001 lost EF's bookkeeping still have this table; nothing reads it.
DROP TABLE IF EXISTS "__EFMigrationsHistory";
