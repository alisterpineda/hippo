using System.CommandLine;
using Hippo.Indexing;
using Hippo.Notebooks;
using Microsoft.Data.Sqlite;

namespace Hippo.Commands;

/// <summary>
/// What every notebook command starts from: the notebook found from the working directory, its migrated index, and
/// a sweep that has just brought the index in line with the files on disk.
/// </summary>
internal sealed record NotebookSession(Notebook Notebook, string DatabasePath, SqliteConnection Db, SweepResult Sweep, TextWriter Output)
{
    public static readonly Option<bool> JsonOption = new("--json") { Description = "Print JSON instead of text" };

    /// <summary>Opens the session and runs <paramref name="command"/>. A <see cref="HippoException"/> or any other
    /// failure prints to stderr and exits 2.</summary>
    public static int Run(ParseResult result, bool rebuild, Func<NotebookSession, int> command)
    {
        var error = result.InvocationConfiguration.Error;
        try
        {
            var notebook = Notebook.Open(Directory.GetCurrentDirectory());
            Warn(error, notebook.Warnings);

            var databasePath = CacheLocation.DatabasePath(notebook.Root, Environment.GetEnvironmentVariable);
            using var db = IndexDatabase.Open(databasePath, out var scriptsApplied);
            // A schema change may alter what the sweep stores for unchanged files, so it re-reads them all.
            var sweep = Sweeper.Run(notebook, db, rebuild || scriptsApplied > 0, TimeProvider.System);
            Warn(error, sweep.Warnings);

            return command(new NotebookSession(notebook, databasePath, db, sweep, result.InvocationConfiguration.Output));
        }
        catch (HippoException ex)
        {
            error.WriteLine($"hippo: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            error.WriteLine($"hippo: unexpected error: {ex}");
            return 2;
        }
    }

    private static void Warn(TextWriter error, IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
        {
            error.WriteLine($"hippo: warning: {Format.Safe(warning)}");
        }
    }
}
