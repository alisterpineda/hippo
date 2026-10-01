using Dapper;
using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

/// <summary>A finding on the file at <see cref="Path"/>. <see cref="Related"/> is a JSON array of the other paths it
/// concerns.</summary>
internal sealed record StoredFinding(string Rule, string Path, long? Line, string Message, string Related);

internal static class FindingQueries
{
    /// <summary>Every finding, by path and line; a finding about a whole file comes before those on its lines.</summary>
    public static List<StoredFinding> List(SqliteConnection db) =>
        db.Query<StoredFinding>("""
            SELECT fi.rule, f.path, fi.line, fi.message, fi.related
            FROM findings fi
            JOIN files f ON f.id = fi.file_id
            ORDER BY f.path, fi.line, fi.id
            """).ToList();

    /// <summary>Merges <paramref name="worked"/>, findings worked out rather than stored, into <paramref name="stored"/>,
    /// in <see cref="List"/>'s order, which this must keep in step with. The sort is stable, so ties keep the stored
    /// findings' own order, then the worked ones'.</summary>
    public static List<T> Merge<T>(IEnumerable<T> stored, IEnumerable<T> worked, Func<T, string> path, Func<T, long?> line) =>
        stored.Concat(worked).OrderBy(path, StringComparer.Ordinal).ThenBy(f => line(f) ?? 0).ToList();
}
