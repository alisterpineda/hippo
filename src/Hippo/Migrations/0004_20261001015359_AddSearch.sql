CREATE VIRTUAL TABLE search USING fts5(title, path, body, tokenize = 'porter unicode61');
