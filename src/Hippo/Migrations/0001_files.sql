CREATE TABLE "files" (
    "id" INTEGER NOT NULL CONSTRAINT "PK_files" PRIMARY KEY AUTOINCREMENT,
    "path" TEXT NOT NULL,
    "mtime" INTEGER NOT NULL,
    "size" INTEGER NOT NULL,
    "hash" TEXT NOT NULL,
    "kind" TEXT NOT NULL,
    "frontmatter" TEXT NULL,
    "parse_error" TEXT NULL,
    CONSTRAINT "ck_files_kind" CHECK (kind IN ('markdown', 'plain'))
);

CREATE UNIQUE INDEX "ix_files_path" ON "files" ("path");
