# Cache

```
hippo cache list             every index in the cache: its state, size and workspace root
hippo cache prune [--dry-run] [--include-unreachable]
                             remove the indexes of workspaces that were deleted, moved or renamed
```

The index lives in `<user cache>/hippo/<hash of workspace root>/index.db`; set `HIPPO_CACHE_DIR` to an absolute path to replace `<user cache>/hippo`. The hash is the SHA-256 of the root's path. On Windows that is the path the OS resolves the folder to, so a different letter case, an 8.3 short name, a junction or a `subst` drive still finds the same index. Each index records its root, which `hippo cache list` checks: `live` when the root still has a `.hippo/config.json`; `orphaned` when it does not but the root or the folder above it exists, as after the workspace was deleted, moved or renamed; `unreachable` when the drive the root was on is not mounted, or when neither the root nor the folder above it exists or they cannot be checked, as for an offline share; and `unknown` when the index records no root or cannot be read. `hippo cache prune` removes the orphaned indexes, and with `--include-unreachable` the unreachable ones too; nothing else removes an index. A moved workspace is indexed afresh at its new path, and its old index stays until pruned.
