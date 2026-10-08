# Scripts and agents

`hippo init` and `hippo cache` aside, every command runs from anywhere inside a workspace: hippo walks up to the first folder with a `.hippo/config.json`. Each prints text, or JSON with `--json` on every command but `init`.

## Exit status

Every command exits 0 when clean, 1 when `lint` reports findings, and 2 on error. Finding nothing is not an error, so `find` and `backrefs` exit 0 with an empty list.

## JSON

The JSON is always an object, and a command that lists things holds them under a key: `files` for `find` and `backrefs --transitive`, `links` for `refs` and `backrefs`, `findings` for `lint`, `indexes` for `cache list`, and `removed` for `cache prune`.

```console
$ hippo backrefs reading-list.md --json
{
  "links": [
    {
      "source": "index.md",
      "line": 4,
      "kind": "body",
      "raw": "reading-list.md",
      "text": "Reading list"
    },
    {
      "source": "projects/garden.md",
      "line": 7,
      "kind": "body",
      "raw": "../reading-list.md",
      "text": "the reading list"
    }
  ]
}
```

[Find](find.md) describes the fields of each file `find` lists, and `truncated`, which says when `--limit` left files out.
