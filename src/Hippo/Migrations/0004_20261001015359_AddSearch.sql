CREATE VIRTUAL TABLE search USING fts5(title, description, path, body, tokenize = 'porter unicode61');
