CREATE TEMP TABLE search_copy AS SELECT rowid AS id, title, path, body FROM search;

DROP TABLE search;

CREATE VIRTUAL TABLE search USING fts5(title, description, path, body, tokenize = 'porter unicode61');

INSERT INTO search (rowid, title, description, path, body) SELECT id, title, '', path, body FROM temp.search_copy;

DROP TABLE temp.search_copy;
