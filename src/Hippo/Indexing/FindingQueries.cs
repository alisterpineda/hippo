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
}
