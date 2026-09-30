using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Hippo.Commands;

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
    public void Files() => AssertJson("""
        [
          {
            "path": "a.md",
            "kind": "markdown",
            "size": 4,
            "modified": "2026-09-01T12:30:00+00:00",
            "parseError": null
          }
        ]
        """, [new FileOutput("a.md", "markdown", 4, Time, null)], OutputJson.Default.ListFileOutput);

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
        [
          {
            "line": 3,
            "kind": "body",
            "type": "missing",
            "raw": "b.md",
            "target": "b.md"
          },
          {
            "line": 4,
            "kind": "body",
            "type": "url",
            "raw": "https://example.com",
            "target": null
          }
        ]
        """,
        [new RefOutput(3, "body", "missing", "b.md", "b.md"), new RefOutput(4, "body", "url", "https://example.com", null)],
        OutputJson.Default.ListRefOutput);

    [Fact]
    public void Backrefs() => AssertJson("""
        [
          {
            "source": "a.md",
            "line": 3,
            "kind": "frontmatter",
            "raw": "b.md"
          }
        ]
        """, [new BackrefOutput("a.md", 3, "frontmatter", "b.md")], OutputJson.Default.ListBackrefOutput);

    [Fact]
    public void Transitive_backrefs() => AssertJson("""
        [
          {
            "source": "a.md"
          }
        ]
        """, [new TransitiveBackrefOutput("a.md")], OutputJson.Default.ListTransitiveBackrefOutput);

    [Fact]
    public void Broken() => AssertJson("""
        [
          {
            "source": "a.md",
            "line": 3,
            "kind": "body",
            "raw": "../x.md",
            "target": null
          }
        ]
        """, [new BrokenOutput("a.md", 3, "body", "../x.md", null)], OutputJson.Default.ListBrokenOutput);

    [Fact]
    public void Orphans() => AssertJson("""
        [
          {
            "path": "a.md"
          }
        ]
        """, [new OrphanOutput("a.md")], OutputJson.Default.ListOrphanOutput);

    [Fact]
    public void Cache_indexes() => AssertJson("""
        [
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
        """,
        [new CacheIndexOutput("/c/ab/index.db", "/w", "orphaned", 4096), new CacheIndexOutput("/c/cd/index.db", null, "unknown", 0)],
        OutputJson.Default.ListCacheIndexOutput);
}
