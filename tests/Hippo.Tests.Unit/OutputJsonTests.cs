using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Hippo.Commands;
using Hippo.Indexing;

namespace Hippo.Tests.Unit;

/// <summary>
/// Pins the <c>--json</c> shapes, which scripts depend on: a renamed, added or removed field, or a value printed another
/// way, fails here. Change the expected JSON only when the contract is meant to change.
/// </summary>
public class OutputJsonTests
{
    private static readonly DateTimeOffset Time = new(2026, 9, 1, 12, 30, 0, TimeSpan.Zero);

    private static void AssertJson<T>(string expected, T value, JsonTypeInfo<T> type) =>
        Assert.Equal(expected.ReplaceLineEndings("\n"), JsonSerializer.Serialize(value, type).ReplaceLineEndings("\n"));

    [Fact]
    public void Index() => AssertJson("""
        {
          "files": 3,
          "added": 1,
          "updated": 1,
          "removed": 1,
          "hashed": 2,
          "rebuilt": false,
          "elapsedMs": 12
        }
        """, new IndexOutput(3, 1, 1, 1, 2, false, 12), OutputJson.Default.IndexOutput);

    [Fact]
    public void Status() => AssertJson("""
        {
          "root": "/w",
          "database": "/c/index.db",
          "files": {
            "total": 3,
            "markdown": 2,
            "other": 1,
            "parseErrors": 0
          },
          "lastSweep": {
            "finishedAt": "2026-09-01T12:30:00+00:00",
            "elapsedMs": 12,
            "added": 1,
            "updated": 0,
            "removed": 0
          }
        }
        """,
        new StatusOutput("/w", "/c/index.db", new CountsOutput(3, 2, 1, 0), new SweepOutput(Time, 12, 1, 0, 0)),
        OutputJson.Default.StatusOutput);

    [Fact]
    public void Find() => AssertJson("""
        {
          "files": [
            {
              "path": "wiki/heron.md",
              "kind": "markdown",
              "size": 40,
              "modified": "2026-09-01T12:30:00+00:00",
              "title": "Herons",
              "parseError": null,
              "snippet": "The grey heron waits..."
            },
            {
              "path": "raw/day.md",
              "kind": "markdown",
              "size": 4,
              "modified": "2026-09-01T12:30:00+00:00",
              "title": null,
              "parseError": "line 1: bad",
              "snippet": null
            }
          ],
          "truncated": true
        }
        """,
        new FindListOutput([
            new FindOutput("wiki/heron.md", "markdown", 40, Time, "Herons", null, new SearchSnippet("The grey heron waits...", [])),
            new FindOutput("raw/day.md", "markdown", 4, Time, null, "line 1: bad", null),
        ], Truncated: true),
        OutputJson.Default.FindListOutput);

    [Fact]
    public void Find_with_fields() => AssertJson("""
        {
          "files": [
            {
              "path": "wiki/x.md",
              "kind": "markdown",
              "size": 40,
              "modified": "2026-09-01T12:30:00+00:00",
              "title": null,
              "parseError": null,
              "snippet": null,
              "fields": {
                "as_of": "2026-04-01",
                "verified.at": null,
                "tags": [
                  "a"
                ]
              }
            },
            {
              "path": "raw/day.md",
              "kind": "markdown",
              "size": 4,
              "modified": "2026-09-01T12:30:00+00:00",
              "title": null,
              "parseError": "line 1: bad",
              "snippet": null,
              "fields": {}
            }
          ],
          "truncated": false
        }
        """,
        new FindListOutput([
            new FindOutput("wiki/x.md", "markdown", 40, Time, null, null, null, new()
            {
                ["as_of"] = JsonDocument.Parse("\"2026-04-01\"").RootElement,
                ["verified.at"] = JsonDocument.Parse("null").RootElement,
                ["tags"] = JsonDocument.Parse("""["a"]""").RootElement,
            }),
            new FindOutput("raw/day.md", "markdown", 4, Time, null, "line 1: bad", null, []),
        ], Truncated: false),
        OutputJson.Default.FindListOutput);

    [Fact]
    public void Show() => AssertJson("""
        {
          "path": "a.md",
          "kind": "markdown",
          "size": 4,
          "modified": "2026-09-01T12:30:00+00:00",
          "hash": "ab",
          "frontmatter": {
            "type": "Topic"
          },
          "parseError": null
        }
        """,
        new ShowOutput("a.md", "markdown", 4, Time, "ab", JsonDocument.Parse("""{"type":"Topic"}""").RootElement, null),
        OutputJson.Default.ShowOutput);

    [Fact]
    public void Refs() => AssertJson("""
        {
          "links": [
            {
              "line": 3,
              "kind": "body",
              "type": "missing",
              "raw": "b.md",
              "target": "b.md",
              "text": "B"
            },
            {
              "line": 4,
              "kind": "frontmatter",
              "type": "url",
              "raw": "https://example.com",
              "target": null,
              "text": null
            }
          ]
        }
        """,
        new RefsOutput([
            new RefOutput(3, "body", "missing", "b.md", "b.md", "B"),
            new RefOutput(4, "frontmatter", "url", "https://example.com", null, null),
        ]),
        OutputJson.Default.RefsOutput);

    [Fact]
    public void Backrefs() => AssertJson("""
        {
          "links": [
            {
              "source": "a.md",
              "line": 3,
              "kind": "body",
              "raw": "b.md",
              "text": ""
            },
            {
              "source": "a.md",
              "line": 4,
              "kind": "frontmatter",
              "raw": "b.md",
              "text": null
            }
          ]
        }
        """,
        new BackrefsOutput([new BackrefOutput("a.md", 3, "body", "b.md", ""), new BackrefOutput("a.md", 4, "frontmatter", "b.md", null)]),
        OutputJson.Default.BackrefsOutput);

    [Fact]
    public void Transitive_backrefs() => AssertJson("""
        {
          "files": [
            {
              "path": "a.md"
            }
          ]
        }
        """, new TransitiveBackrefsOutput([new TransitiveBackrefOutput("a.md")]), OutputJson.Default.TransitiveBackrefsOutput);

    [Fact]
    public void Lint() => AssertJson("""
        {
          "findings": [
            {
              "rule": "okf-type",
              "path": "kb/a.md",
              "line": null,
              "message": "it has no frontmatter",
              "related": []
            },
            {
              "rule": "okf-status",
              "path": "kb/b.md",
              "line": 4,
              "message": "status \u0027Stable\u0027 is not draft, stable or deprecated",
              "related": [
                "kb/c.md"
              ]
            }
          ]
        }
        """,
        new LintOutput([
            new FindingOutput("okf-type", "kb/a.md", null, "it has no frontmatter", []),
            new FindingOutput("okf-status", "kb/b.md", 4, "status 'Stable' is not draft, stable or deprecated", ["kb/c.md"]),
        ]),
        OutputJson.Default.LintOutput);

    [Fact]
    public void Cache_indexes() => AssertJson("""
        {
          "indexes": [
            {
              "database": "/c/ab/index.db",
              "root": "/w",
              "state": "orphaned",
              "size": 4096
            },
            {
              "database": "/c/cd/index.db",
              "root": null,
              "state": "unknown",
              "size": 0
            }
          ]
        }
        """,
        new CacheListOutput([new CacheIndexOutput("/c/ab/index.db", "/w", "orphaned", 4096), new CacheIndexOutput("/c/cd/index.db", null, "unknown", 0)]),
        OutputJson.Default.CacheListOutput);

    [Fact]
    public void Cache_prune() => AssertJson("""
        {
          "dryRun": true,
          "removed": [
            {
              "database": "/c/ab/index.db",
              "root": "/w",
              "state": "orphaned",
              "size": 4096
            }
          ]
        }
        """, new CachePruneOutput(true, [new CacheIndexOutput("/c/ab/index.db", "/w", "orphaned", 4096)]), OutputJson.Default.CachePruneOutput);

    /// <summary>An empty list is written as one, so a script reading the key never meets <c>null</c> or a missing
    /// field.</summary>
    [Fact]
    public void An_empty_list_is_written() => AssertJson("""
        {
          "findings": []
        }
        """, new LintOutput([]), OutputJson.Default.LintOutput);
}
