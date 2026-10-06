# Links

```
hippo refs <path>            the links out of a file: line, kind, file, directory, missing, url or anchor,
                             and the link text
hippo backrefs <path> [--link-kind body|frontmatter] [--from <pattern>…] [--transitive]
                             the links into a path, with their text; --link-kind and --from keep only
                             links of that kind and from the files a glob matches; --transitive lists
                             every file that reaches it, along only such links
```

A bundle is a folder listed in `bundles` whose pages treat it as their root: a leading `/` in a link resolves against the deepest bundle holding the page, or against the workspace root for a page in none. Other body links resolve from the page's folder; `[[x]]` is plain text, not a link.

Body links are CommonMark links, images, reference links and autolinks; their destinations are URLs, so percent-encoding is decoded and a query or fragment dropped. Frontmatter values are literal paths: nothing is decoded and a `?` is part of the path, but a `#` still starts a fragment that is dropped, so a frontmatter value cannot name a file with `#` in its name. A link with a scheme is a URL, one starting with `#` is anchor-only, and any other is a file when it resolves to an indexed file, a directory when it resolves to a folder holding an indexed file, and missing otherwise. Paths are compared under Unicode canonical equivalence (NFC), on every OS: a link, or a path or glob given to a command, that spells `é` as `e` and a combining accent reaches a file whose name has the precomposed `é`, and the other way round. A file is always shown under its name on disk and a link as written. Where a filesystem keeps two such names apart as two files, a link to either reaches both. A body link's text is what a reader sees as the link, as plain text on one line, read as a page's title is: `[**Okf** bundles](x.md)` reads `Okf bundles`, an image gives its alt text, a reference link its label and an autolink its URL. `refs` and `backrefs` end each line with it in brackets, and a frontmatter link has none. A trailing `/` makes no difference, and the workspace root is a directory. A folder holding no indexed file, because it is empty or everything in it is excluded, is missing. `backrefs` of a folder lists the links to the folder itself, and `backrefs .` from the root lists those to the root. With `--transitive`, `--link-kind` and `--from` apply at every hop: a chain follows only links of that kind and passes only through files `--from` matches, so with `--from 'wiki/**'`, a wiki page that reaches a file only through a day entry is not listed.
