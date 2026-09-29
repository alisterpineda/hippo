CREATE TABLE "files" (
    "id" INTEGER NOT NULL CONSTRAINT "PK_files" PRIMARY KEY AUTOINCREMENT,
    "path" TEXT NOT NULL,
    "mtime" INTEGER NOT NULL,
    "size" INTEGER NOT NULL,
    "hash" TEXT NOT NULL,
    "hashed_at" INTEGER NOT NULL,
    "kind" TEXT NOT NULL,
    "frontmatter" TEXT NULL,
    "parse_error" TEXT NULL,
    CONSTRAINT "ck_files_kind" CHECK (kind IN ('markdown', 'plain'))
);

CREATE TABLE "meta" (
    "key" TEXT NOT NULL CONSTRAINT "PK_meta" PRIMARY KEY,
    "value" TEXT NOT NULL
);

CREATE TABLE "links" (
    "id" INTEGER NOT NULL CONSTRAINT "PK_links" PRIMARY KEY,
    "source_id" INTEGER NOT NULL,
    "line" INTEGER NOT NULL,
    "kind" TEXT NOT NULL,
    "type" TEXT NOT NULL,
    "raw" TEXT NOT NULL,
    "target" TEXT NULL,
    CONSTRAINT "ck_links_kind" CHECK (kind IN ('body', 'frontmatter')),
    CONSTRAINT "ck_links_target" CHECK (type = 'path' OR target IS NULL),
    CONSTRAINT "ck_links_type" CHECK (type IN ('path', 'url', 'anchor')),
    CONSTRAINT "FK_links_files_source_id" FOREIGN KEY ("source_id") REFERENCES "files" ("id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "ix_files_path" ON "files" ("path");

CREATE INDEX "ix_links_source_id" ON "links" ("source_id");

CREATE INDEX "ix_links_target" ON "links" ("target");
