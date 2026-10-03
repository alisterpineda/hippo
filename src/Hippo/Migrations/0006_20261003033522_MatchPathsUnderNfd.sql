DROP INDEX "ix_links_target";

ALTER TABLE "links" ADD "target_nfd" TEXT NULL;

ALTER TABLE "index_entries" ADD "target_nfd" TEXT NOT NULL DEFAULT '';

ALTER TABLE "files" ADD "path_nfd" TEXT NOT NULL DEFAULT '';

UPDATE files SET path_nfd = path;

UPDATE links SET target_nfd = target;

UPDATE index_entries SET target_nfd = target;

CREATE INDEX "ix_links_target_nfd" ON "links" ("target_nfd");

CREATE INDEX "ix_files_path_nfd" ON "files" ("path_nfd");
