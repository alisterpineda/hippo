# Find

```
hippo find ["<query>"] [--glob <pattern>…] [--kind markdown|other] [--where <condition>…]
           [--field <path>[,<path>…]…] [--errors] [--no-refs] [--no-backrefs] [--from <pattern>…]
           [--link-kind body|frontmatter] [--limit n]
                             every indexed file in path order, or with a query, the pages holding every
                             word of it, best match first, each with its title and a snippet; --glob,
                             --kind and --where filter by path, kind or frontmatter field, and a --glob
                             starting with ! leaves out what it matches; --where takes field=value,
                             field!=value, field<value, field<=value, field>value, field>=value, field or
                             !field, quoted for the shell ('as_of<2026-04-01'), keeping only pages, and
                             a value @field names another field of the page; a field path's part
                             ending in [] means each element of that list; --field shows those
                             frontmatter fields of each file; --errors keeps only files whose
                             frontmatter failed to parse, with the error; --no-refs keeps files with no
                             link to another file, and --no-backrefs those no other file links to;
                             --from counts only links from files it matches toward --no-backrefs, and
                             --link-kind only links of that kind toward either; --limit caps the
                             results (20 by default with a query, none without)
```

Without a query, `hippo find` lists every indexed file in path order, one path per line, and under `--errors` each path followed by its frontmatter error. With `--json`, each file has its path, kind, size, modified time, title (null for a file that is not a page, or a page with none), parse error and snippet (null without a query), and under `--field`, its fields. The filters keep only the files they match, and `--limit` counts what is left; without a query there is no limit unless one is given.

`--glob` takes a glob relative to the workspace root, and can be given more than once: a file is kept when any glob matches it, less those a glob starting with `!` matches. When every glob starts with `!`, they leave out what they match from every file, so `--glob '!archive/**'` keeps everything outside `archive`. `--kind` keeps only `markdown` files, those whose names end in `.md`, or only `other` files. `--no-backrefs` keeps the files, markdown or not, that no other file links to, and `--no-refs` those with no link to another indexed file; a file's links to itself, and links to folders, URLs, anchors and missing targets, count for neither. Together, `--no-refs --no-backrefs` list the files with no link in or out. The link filters read every link in the index, so a file `--glob` leaves out still has its links count.

`--from` makes `--no-backrefs` count only the links whose source file matches it, and `--link-kind body` or `--link-kind frontmatter` makes `--no-refs` and `--no-backrefs` count only links of that kind. `--from` takes globs as `--glob` does: relative to the workspace root, repeatable, and a leading `!` leaves files out. A file's links to itself still never count. Each needs a filter to narrow, so `--from` without `--no-backrefs`, or `--link-kind` without `--no-refs` or `--no-backrefs`, is an error. To list the artifacts no wiki page cites in its frontmatter, however many day entries link them:

```sh
hippo find --glob 'raw/artifacts/**' --no-backrefs --from 'wiki/**' --link-kind frontmatter
```

`--where` keeps the pages whose frontmatter meets a condition, and can be given more than once: a page must meet every one. Only markdown files have frontmatter, so no condition matches any other file, not even `!=` or `!field`. A field is a dotted path into nested mappings, such as `verified.at`, written as a `links.frontmatter` field is in the [config](config.md): a part ending in `[]` means each element of that list, so `--where 'sources[].id=j-2026-09-16'` keeps the pages where any element of `sources` has that `id`. A part without `[]` does not step into a list, so `sources.id` reaches nothing when `sources` is a list, and `[]` on a value that is not a list reaches nothing either. A field cannot hold `=`, `<`, `>` or `!`, since the first of them starts the operator, and a key holding `.`, `[` or `]` cannot be reached.

| Condition | Keeps the pages where |
|---|---|
| `field=value` | the field equals the value |
| `field!=value` | it does not: `tags!=draft` keeps the pages with no `draft` tag, including those with no `tags` |
| `field<value`, `field<=value`, `field>value`, `field>=value` | the field is below or above the value |
| `field` | the field is there and not null; `as_of:` with no value counts as missing |
| `!field` | the field is missing or null |

A stored number is compared as a number when the value is one too, and anything else as text, character by character, so write dates as ISO 8601 (`2026-04-01`), which sort as text in date order. YAML dates are stored as text, so `as_of: 2026-04-01` is the string `"2026-04-01"`. A list matches when any element does. A range never matches a missing field, a boolean, a null or a mapping. A page whose frontmatter failed to parse meets no condition, not even `!=` or `!field`, since nothing is known of it; `--errors` lists those. `<` and `>` redirect in every shell, so quote each condition: `--where 'as_of<2026-04-01'`.

A value starting with `@` names a field of the same page, in the same path grammar, in place of a literal: `--where 'verified.at<@generated.at'` keeps the pages verified before they were generated. `@@` writes a literal `@`, so `author=@@alice` matches `@alice`, and a value without a leading `@` is always a literal. The two fields compare as numbers when both are numbers, and as text otherwise, and if either is a list, the condition holds when any pair of elements meets it. A missing right-hand field fails `=`, `<`, `<=`, `>` and `>=`, and meets `!=`, which stays the exact negation of `=`.

`--field` shows the frontmatter fields it names, each a dotted path as `--where` takes, and can be given more than once, or with several paths separated by commas: `--field as_of,tracking --field verified.at`. A path naming a list or a mapping shows it whole, a path through `[]` shows the list of the values it reaches, as `--field 'sources[].resource'` does, and one that reaches nothing is left out. A key holding `.`, `,`, `[` or `]` cannot be reached. With `--json`, each file gets a `fields` map keyed by the path as given, leaving out a field the file does not have, giving a field set to YAML null as `null`, and empty for a file whose frontmatter failed to parse. In text, each field ends the file's line as `key=value`, a list or mapping as compact JSON, and with a query on the title line, above the snippet:

```
wiki/x.md  as_of=2026-04-01  tracking=open
```

So a check over a bundle's frontmatter takes one call:

```sh
hippo find --glob 'wiki/**' --where tracking=open --where 'as_of<2026-04-01' --json
hippo find --glob 'wiki/**' --where tracking --where '!as_of' --json
hippo find --glob 'wiki/**' --field as_of,tracking,verified.at --json
```

With a query, `hippo find` looks through every markdown file's title, description, path and body: the body is the text after the frontmatter, the title is the frontmatter `title`, or the first level-1 heading when there is none, and the description is the frontmatter `description`, in every workspace, not only in OKF bundles. In the query, the text between a pair of `"` is a phrase, and every other word stands alone; a page matches when it holds every phrase and every word, so `'"integration test" ranking'` needs both the phrase and the word. Every `"` is syntax: an odd number of them is an error, an empty phrase `""` is ignored, and a `"` cannot be searched for. Each word and phrase is matched as text, so punctuation in it is never query syntax: `foo-bar` finds the words `foo` and `bar` side by side, and `*`, `:`, `-`, `AND`, `OR` and `NEAR` mean nothing special. Results rank by BM25, with a match in the title counting for more than one in the description, one in the description for more than one in the path, and one in the path for more than one in the body. Each result shows a snippet of the body around the match, or of the description when only the description matched, on one line, as written, so the page's own markdown such as `**` shows unchanged. The body and description are searched and shown with each run of whitespace read as one space and the control characters U+0001 to U+0003, which hippo uses to mark snippets, read as `�`. On a terminal the matched words are in bold, unless `NO_COLOR` is set to anything but the empty string; on Windows, hippo turns on the console's ANSI support, and a console that cannot take it shows no bold; piped output and the JSON `snippet` mark nothing. A page that matches only in its title or path shows the start of its body. The filters keep only the pages they match, as without a query, and `--limit` counts what is left, 20 unless another is given. Finding nothing is not an error, so `find` exits 0 either way.

The `porter` tokenizer, the default, matches whole words and other forms of them, so `running` finds `runs`. The `trigram` tokenizer matches any run of three or more characters, inside words too, so `dex` finds `index`, but a word shorter than three characters finds nothing. Changing `search.tokenizer` rebuilds the search index on the next command, without reading the files again.

A phrase means what the tokenizer makes of it, and it matches within the title, the path or the body, never across them:

| | `porter` (default) | `trigram` |
|---|---|---|
| A phrase means | The words next to each other and in order, in any of their forms, ignoring punctuation and case | The text as a substring, punctuation included, ignoring case |
| `"integration test"` finds "test integration" | No | No |
| `"(per human, unfiled)"` finds "per humans, unfilled" | Yes | No |

Under `trigram`, a phrase of three or more characters can match although its words are shorter, so `"is a"` finds "this is a test".

The shell removes the outer quotes, so a phrase needs a second layer:

| Shell | Phrase |
|---|---|
| bash, zsh, fish, PowerShell 7.3+ | `hippo find '"integration test"'` |
| Windows PowerShell 5.1 | `hippo find '\"integration test\"'` |
| cmd.exe | `hippo find "\"integration test\""` |
