using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Unit;

/// <summary>
/// The native AOT binary links the SQLite pinned in src/Hippo/Sqlite.targets, while the tests load the one in the
/// SQLitePCLRaw package. Keeping them on one version means the tests exercise the SQLite the binary ships.
/// </summary>
public class SqliteVersionTests
{
    [Fact]
    public void The_linked_SQLite_is_the_version_the_package_loads()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        Assert.Equal(connection.ServerVersion, Pinned("HippoSqliteVersion"));
    }

    /// <summary>sqlite.org names the amalgamation for version X.Y.Z as XYYZZ00.</summary>
    [Fact]
    public void The_download_is_the_pinned_version()
    {
        var parts = Pinned("HippoSqliteVersion").Split('.').Select(int.Parse).ToArray();
        var name = $"sqlite-amalgamation-{parts[0]}{parts[1]:D2}{parts[2]:D2}00.zip";

        Assert.EndsWith("/" + name, Pinned("HippoSqliteUrl"));
    }

    private static string Pinned(string property)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "hippo.slnx")))
        {
            directory = directory.Parent ?? throw new InvalidOperationException("The repository root was not found.");
        }
        var targets = XDocument.Load(Path.Combine(directory.FullName, "src", "Hippo", "Sqlite.targets"));
        return targets.Descendants(property).Single().Value;
    }
}
