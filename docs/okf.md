# OKF bundles

A bundle is an [OKF v0.2](https://github.com/GoogleCloudPlatform/open-knowledge-format/blob/main/SPEC.md) bundle when its root `index.md` declares `okf_version` in its frontmatter. hippo reads every OKF bundle as 0.2; `lint` notes one that declares another version. A bundle without the declaration is a plain bundle, and a folder not listed in `bundles` is never an OKF bundle, whatever its `index.md` says. A page belongs to the deepest bundle holding it.

In an OKF bundle, OKF's path fields are frontmatter links with no `links.frontmatter` entry: `resource`, `sources[].resource`, `computation`, `executor.resource` and `attester.resource`. A relative value resolves against the bundle root, as the spec's examples do; a `links.frontmatter` entry for the same field resolves it as the entry says instead. A path may leave its bundle. Every `sources[].resource` that is not a URL is read as a path, so a scope descriptor such as `all queries in BigQuery project X` is a broken link. Body links resolve as they do anywhere else.

## OKF rules

The OKF rules check every markdown file in every OKF bundle. `index.md` and `log.md` are reserved at every level and checked for their own structure; every other markdown file is a concept.

A rule marked *No* is a MUST rule in OKF v0.2: a bundle that breaks it does not conform, so `lint` always checks it.

| Rule | `lint.off` | Reports |
| --- | --- | --- |
| `okf-type` | No | A concept with no frontmatter, frontmatter that fails to parse, or no non-empty `type` |
| `okf-index-frontmatter` | No | Frontmatter in an `index.md` below the bundle root, or a key other than `okf_version` in the root one |
| `okf-log-date` | No | A level-2 heading in a `log.md` that is not a `YYYY-MM-DD` date |
| `okf-source-resource` | Yes | A `sources` entry with no `resource` |
| `okf-footnote` | Yes | A footnote whose label matches no `sources[].id` on its page, explanatory footnotes included. A definition nothing references is not checked |
| `okf-timestamp` | Yes | A value in `generated.at`, `verified[].at`, `stale_after`, `sources[].last_modified` or a `usage_window` that is not an ISO 8601 datetime with an explicit UTC offset |
| `okf-actor` | Yes | A `generated` with no `by`, or a `generated.by` or `verified[].by` not shaped `<producer>/<version>`, `human:<id>` or `process:<id>`. A bare `verified` mapping counts as a one-element list |
| `okf-status` | Yes | A `status` other than `draft`, `stable` or `deprecated` |
| `okf-index` | Yes | An `index.md` entry whose page is missing or whose description is not the page's `description`, or a page that no `index.md` above it links to |

Every OKF rule is on for every OKF bundle. `lint.off` turns off the rules marked *Yes*; naming one marked *No* there is a config error, so a run with no findings from those means the bundle is conformant. A concept whose frontmatter fails to parse is reported by `okf-type` alone among the OKF rules that read frontmatter, since the rest cannot be checked without it; `okf-index` still checks that an index lists it. Findings are made when a page is indexed and kept in the index, so `lint` costs no more than a query.

`okf-index` reads an entry as a list item that opens with a link, `* [Title](url) - description`. The description is the text after the link and its separator (a hyphen, dash or colon) as written, with each run of whitespace read as one space, and it must equal the page's `description`, read the same way. An entry for a folder, an `index.md` or `log.md`, a file that is not markdown, or a page whose frontmatter fails to parse has no description to compare. An `index.md` covers the pages in its own folder and every folder below it, up to its bundle's root, and a page is listed when any `index.md` covering it links to it, in an entry or anywhere else. Every OKF bundle has a root `index.md`, so every page in it must be linked from one; a bundle that keeps no listing can turn the rule off. Each `okf-index` finding concerns an index and a page that can change apart, so it is worked out when `lint` runs, from what the index holds.
