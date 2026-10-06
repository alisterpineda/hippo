# Configuration

`.hippo/config.json` chooses which files are indexed and how links resolve. It is JSON that may hold `//` and `/* */` comments and trailing commas, and a key hippo does not know is an error:

```jsonc
{
  "files": {
    "include": ["**/*"],
    "exclude": [".git/**", ".obsidian/**", ".trash/**"],
    "gitignore": true                   // leave out the files git ignores, whatever include says
  },
  "bundles": ["wiki"],                  // a leading "/" in a link on a page in wiki resolves against wiki
  "links": {
    "frontmatter": [                    // an OKF bundle's path fields, such as sources[].resource, need no entry
      {
        "field": "related[]",           // dotted for nested mappings; [] for each element of a list
        "resolve": "page"               // page (the page's own folder, the default) or bundle (its bundle root)
      }
    ]
  },
  "lint": {
    "off": ["okf-footnote"],            // rules to leave unchecked; OKF MUST rules are always checked
    "exclude": ["archive/**"]           // files whose findings lint leaves out; they stay indexed and linkable
  },
  "search": {
    "tokenizer": "porter"               // porter (whole words and their forms, the default) or trigram (any substring)
  }
}
```

`gitignore`, on unless set to `false`, leaves out every untracked file git ignores, as `git ls-files --others --ignored --exclude-standard` lists them from the workspace root: each `.gitignore`, `.git/info/exclude` and your global excludes file count, including rules in a repository that holds the workspace in a subfolder; when that repository ignores the workspace folder itself, nothing is left out. A tracked file is indexed even when a rule matches it, as git itself does. hippo runs git only when the workspace root or a folder above it holds a `.git`; if git is not on `PATH` or fails, hippo warns and indexes the ignored files.

The workspace root's `.hippo` folder is never indexed, whatever `include` says: it is hippo's, not the workspace's, so a link into it is broken. A nested workspace's `.hippo` folder is indexed like any other.
