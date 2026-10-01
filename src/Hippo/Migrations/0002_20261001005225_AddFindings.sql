CREATE TABLE "findings" (
    "id" INTEGER NOT NULL CONSTRAINT "PK_findings" PRIMARY KEY,
    "file_id" INTEGER NOT NULL,
    "rule" TEXT NOT NULL,
    "line" INTEGER NULL,
    "message" TEXT NOT NULL,
    "related" TEXT NOT NULL,
    CONSTRAINT "FK_findings_files_file_id" FOREIGN KEY ("file_id") REFERENCES "files" ("id") ON DELETE CASCADE
);

CREATE INDEX "ix_findings_file_id" ON "findings" ("file_id");
