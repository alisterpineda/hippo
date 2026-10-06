# Lint

```
hippo lint [--rule <name>…]  where the workspace breaks a lint rule: links whose target is not an indexed
                             file or folder, frontmatter that fails to parse, and where OKF bundles depart
                             from OKF v0.2; --rule lists only the rules named, even one lint.off turns off,
                             and okf-* names every OKF rule
```

`hippo lint` checks the workspace against the rules below, lists the findings by path and line, and exits 1 when there are any. Every rule is on unless `lint.off` turns it off, and `--rule` checks only the rules it names. A rule name in `lint.off` or `--rule` may hold `*`, which matches any run of characters, so `okf-*` names every OKF rule; one that names no rule is an error. The workspace rules check every indexed file. The [OKF rules](okf.md#okf-rules) check OKF bundles, and only when one of them is checked does `lint` note, when `bundles` lists any folder, that no bundle declares `okf_version`.

## Turning rules off

Each entry in `lint.off` is a rule name, which turns that rule off everywhere, or an object whose `paths` turn rules off on some files only: its `rules` say which, and an object without `rules` turns off every rule.

```jsonc
"lint": {
  "off": [
    "okf-footnote",
    { "rules": ["okf-index"], "paths": ["wiki/drafts/**"] },
    { "paths": ["archive/**"] }
  ]
}
```

`paths` takes globs in the same syntax as `files.exclude`, so one that matches a folder covers everything under it. A glob matches the file a finding is on, not a link's target, so a broken link from another page into `archive` is still reported, while the findings on the files in it, its own bundle's OKF findings included, are not. The findings left out do not count toward the exit status. A file left out stays indexed and is still a link target, unlike one `files.exclude` leaves out, which suits frozen content such as an imported archive. The findings are left out when `lint` runs, so changing `lint.off` needs no reindex.

`--rule` checks the rules it names even where an entry that names them turns them off, `"*"` included, but not on the files an entry without `rules` leaves out: those are not for linting, whatever is asked.

Nor does the sweep warn about frontmatter that fails to parse in a file where `frontmatter-syntax` is off, or about links that fail to parse in one where `broken-link` is off; it still warns about a file it cannot read at all.

## Workspace rules

| Rule | Reports |
| --- | --- |
| `broken-link` | A path link whose target is neither an indexed file nor a folder holding one, or that leaves the workspace |
| `frontmatter-syntax` | A markdown file whose frontmatter fails to parse |

A `broken-link` finding is on the line of the link, gives the link as written and the path it resolves to, and relates that path. It is worked out when `lint` runs, from the links the index holds, so a target that comes or goes changes it without its linking pages being read again.

A `frontmatter-syntax` finding is on the whole file and gives the parse error; it reports the files `hippo find --errors` lists, less those `lint.off` leaves out. It checks only that the frontmatter parses, not what it holds. A concept in an OKF bundle whose frontmatter fails to parse is reported by `okf-type` as well, since its type cannot be read, so turning `frontmatter-syntax` off leaves that finding in place.
