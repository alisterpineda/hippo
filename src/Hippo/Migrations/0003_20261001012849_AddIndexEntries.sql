CREATE TABLE "index_entries" (
    "id" INTEGER NOT NULL CONSTRAINT "PK_index_entries" PRIMARY KEY,
    "file_id" INTEGER NOT NULL,
    "line" INTEGER NOT NULL,
    "target" TEXT NOT NULL,
    "description" TEXT NULL,
    CONSTRAINT "FK_index_entries_files_file_id" FOREIGN KEY ("file_id") REFERENCES "files" ("id") ON DELETE CASCADE
);

CREATE INDEX "ix_index_entries_file_id" ON "index_entries" ("file_id");
