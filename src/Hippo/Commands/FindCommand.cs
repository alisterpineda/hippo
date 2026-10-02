using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;
using Microsoft.Data.Sqlite;

namespace Hippo.Commands;

internal static class FindCommand
{
    /// <summary>How many pages a query lists when no <c>--limit</c> is given. A listing without a query has no cap.</summary>
    private const int QueryLimit = 20;

    public static Command Build(CliEnvironment environment)
    {
        var query = new Argument<string?>("query")
        {
            Description = "Words every page found must contain, and \"phrases\" each must contain as a whole; "
                + "the shell needs a phrase quoted again, as '\"two words\"' (Windows PowerShell 5.1: '\\\"two words\\\"', "
                + "cmd.exe: \"\\\"two words\\\"\"); without a query, every indexed file is listed",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var glob = new Option<string[]>("--glob")
        {
            Description = "Only paths matching this workspace-relative glob, or with a leading !, not matching it; repeatable",
            HelpName = "pattern",
        };
        var kind = new Option<string>("--kind") { Description = "Only files of this kind" };
        kind.AcceptOnlyFromAmong("markdown", "other");
        var where = new Option<string[]>("--where")
        {
            Description = "Only files whose frontmatter field meets the condition: field=value, field!=value, field<value, "
                + "field<=value, field>value, field>=value, field (present) or !field (missing); repeatable, and every condition "
                + "must hold. Numbers compare as numbers, anything else as text, so write dates as ISO 8601. Quote it for the "
                + "shell, as --where 'as_of<2026-04-01'",
            HelpName = "condition",
        };
        var field = new Option<string[]>("--field")
        {
            Description = "Show this frontmatter field of each file, a dotted path such as verified.at, or several separated "
                + "by commas; repeatable. A list or mapping shows whole; keys holding ., ,, [ or ] cannot be reached",
            HelpName = "path",
        };
        var errors = new Option<bool>("--errors") { Description = "Only files whose frontmatter failed to parse, with the error" };
        var noRefs = new Option<bool>("--no-refs") { Description = "Only files with no link to another indexed file" };
        var noBackrefs = new Option<bool>("--no-backrefs") { Description = "Only files no other file links to" };
        var limit = new Option<int?>("--limit")
        {
            Description = $"At most this many results ({QueryLimit} by default with a query)",
            HelpName = "n",
        };
        limit.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() < 1)
            {
                result.AddError("--limit must be at least 1");
            }
        });
        var command = new Command("find",
            "List indexed files in path order, or with a query, the pages holding every word and phrase of it, best match first, "
            + "with a snippet of each")
        {
            query,
            glob,
            kind,
            where,
            field,
            errors,
            noRefs,
            noBackrefs,
            limit,
            WorkspaceSession.JsonOption,
        };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var filters = (result.GetValue(where) ?? []).Select(FrontmatterFilter.Parse).ToList();
            var fields = result.GetValue(field) is { Length: > 0 } paths ? FrontmatterFields.ParsePaths(paths) : null;
            var errorsOnly = result.GetValue(errors);
            // Only the frontmatter filters and fields read a file's frontmatter, so without them it is not read at all.
            var readFrontmatter = filters.Count > 0 || fields is not null;

            // The filters keep the same rows whether they came from the listing or the search. A file the glob leaves out
            // still has its links count toward --no-refs and --no-backrefs, since those are read from the whole index.
            List<T> Keep<T>(List<T> rows, SqliteTransaction transaction) where T : IFileRow
            {
                if (result.GetValue(glob) is { Length: > 0 } patterns)
                {
                    var matched = session.Workspace.Glob(patterns, rows.Select(row => row.Path));
                    rows = rows.Where(row => matched.Contains(row.Path)).ToList();
                }
                if (result.GetValue(kind) is { } only)
                {
                    rows = rows.Where(row => row.Kind == only).ToList();
                }
                if (filters.Count > 0)
                {
                    rows = rows.Where(row => FrontmatterFilter.MatchesAll(filters, row.Frontmatter, row.ParseError)).ToList();
                }
                if (result.GetValue(noRefs))
                {
                    var unlinked = LinkQueries.WithoutRefs(session.Db, transaction);
                    rows = rows.Where(row => unlinked.Contains(row.Path)).ToList();
                }
                if (result.GetValue(noBackrefs))
                {
                    var unlinked = LinkQueries.WithoutBackrefs(session.Db, transaction);
                    rows = rows.Where(row => unlinked.Contains(row.Path)).ToList();
                }
                return errorsOnly ? rows.Where(row => row.ParseError is not null).ToList() : rows;
            }

            // Each result's --field values, or null without --field, so the JSON leaves them out.
            Dictionary<string, JsonElement>? Fields(IFileRow row) => fields is null ? null : FrontmatterFields.Read(fields, row.Frontmatter);

            if (result.GetValue(query) is not { } text)
            {
                // One read transaction, so the listing and each link filter see the index as it was at one moment.
                using var listing = session.Db.BeginTransaction(deferred: true);
                // Only JSON carries the title, and reading one reads its page's whole search row.
                var files = Keep(FileQueries.List(session.Db, titles: session.Json, frontmatter: readFrontmatter, transaction: listing), listing);
                listing.Commit();
                var listed = files.Take(result.GetValue(limit) ?? int.MaxValue)
                    .Select(f => new FindOutput(f.Path, f.Kind, f.Size, Format.Modified(f.Mtime), f.Title, f.ParseError, null,
                        Fields(f)))
                    .ToList();
                session.EmitList(listed, OutputJson.Default.ListFindOutput, file =>
                    (errorsOnly ? $"{Format.Safe(file.Path)}: {Format.Safe(file.ParseError!)}" : Format.Safe(file.Path)) + FieldText(file));
                return ExitCode.Clean;
            }

            var search = SearchIndex.Query(text, session.Workspace.Config.SearchTokenizer)
                ?? throw new HippoException("find needs at least one word to look for; leave the query out to list every file");

            // One read transaction, so the rows the matches name are still those rows when their snippets are read.
            using var transaction = session.Db.BeginTransaction(deferred: true);
            var matches = Keep(SearchIndex.Matches(session.Db, transaction, search, readFrontmatter), transaction);
            var found = matches.Take(result.GetValue(limit) ?? QueryLimit).Select(m =>
            {
                var page = SearchIndex.Text(session.Db, transaction, search, m.Id);
                return new FindOutput(m.Path, m.Kind, m.Size, Format.Modified(m.Mtime), page.Title, m.ParseError, page.Snippet, Fields(m));
            }).ToList();
            transaction.Commit();

            session.Emit(found, OutputJson.Default.ListFindOutput, (writer, results) =>
            {
                foreach (var page in results)
                {
                    writer.WriteLine((page.Title is null ? Format.Safe(page.Path) : $"{Format.Safe(page.Path)}  {Format.Safe(page.Title)}")
                        + FieldText(page));
                    writer.WriteLine($"  {Format.Safe(page.Snippet!)}");
                }
            });
            return ExitCode.Clean;
        }));
        return command;
    }

    /// <summary>The fields <c>--field</c> asked for, as they end a result's line: each <c>  key=value</c>, a string as
    /// written, and anything else as compact JSON.</summary>
    private static string FieldText(FindOutput result) =>
        string.Concat((result.Fields ?? []).Select(f =>
            $"  {Format.Safe(f.Key)}={Format.Safe(f.Value.ValueKind == JsonValueKind.String ? f.Value.GetString()! : Format.Compact(f.Value))}"));
}
