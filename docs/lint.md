# Lint

```
hippo lint [--rule <name>…]  where the workspace breaks a lint rule: links whose target is not an indexed
                             file or folder, frontmatter that fails to parse, and where OKF bundles depart
                             from OKF v0.2; --rule lists only the rules named, even one lint.off turns off
```

`hippo lint` checks the workspace against the rules below, lists the findings by path and line, and exits 1 when there are any. Every rule is on unless `lint.off` turns it off, and `--rule` checks only the rules it names, even one `lint.off` turns off. The workspace rules check every indexed file. The [OKF rules](okf.md#okf-rules) check OKF bundles, and only when one of them is checked does `lint` note that no bundle declares `okf_version`, or that one declares a version other than 0.2.

`lint.exclude` takes globs in the same syntax as `files.exclude`, and `lint` leaves out every finding on a file one of them matches, whatever `--rule` names; the findings left out do not count toward the exit status. A glob matches the file the finding is on, not a link's target, so a broken link from another page into an excluded folder is still reported, while the findings on the files in that folder, its own bundle's OKF findings included, are not. An excluded file stays indexed and is still a link target, unlike one `files.exclude` leaves out, which suits frozen content such as an imported archive. The findings are left out when `lint` runs, so changing `lint.exclude` needs no reindex. Nor does the sweep warn about frontmatter or links that fail to parse in an excluded file; it still warns about one it cannot read at all.

## Workspace rules

| Rule | `lint.off` | Reports |
| --- | --- | --- |
| `broken-link` | Yes | A path link whose target is neither an indexed file nor a folder holding one, or that leaves the workspace |
| `frontmatter-syntax` | Yes | A markdown file whose frontmatter fails to parse |

A `broken-link` finding is on the line of the link, gives the link as written and the path it resolves to, and relates that path. It is worked out when `lint` runs, from the links the index holds, so a target that comes or goes changes it without its linking pages being read again.

A `frontmatter-syntax` finding is on the whole file and gives the parse error; it reports the files `hippo find --errors` lists, less those `lint.exclude` matches. It checks only that the frontmatter parses, not what it holds, and it reports a concept in an OKF bundle as well as `okf-type` does, so turning it off leaves the OKF finding in place.
