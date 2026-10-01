using System.CommandLine;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class FindCommand
{
    /// <summary>How many pages a query lists when no <c>--limit</c> is given. A listing without a query has no cap.</summary>
    private const int QueryLimit = 20;

    public static Command Build(CliEnvironment environment)
    {
        var query = new Argument<string?>("query")
        {
            Description = "Words every page found must contain; without them, every indexed file is listed",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var glob = new Option<string>("--glob") { Description = "Only paths matching this workspace-relative glob", HelpName = "pattern" };
        var where = new Option<string>("--where") { Description = "Only files whose frontmatter field equals value", HelpName = "field=value" };
        var errors = new Option<bool>("--errors") { Description = "Only files whose frontmatter failed to parse, with the error" };
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
            "List indexed files in path order, or with a query, the pages holding every word of it, best match first, "
            + "with a snippet of each")
        {
            query,
            glob,
            where,
            errors,
            limit,
            WorkspaceSession.JsonOption,
        };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var filter = result.GetValue(where) is { } condition ? FrontmatterFilter.Parse(condition) : null;
            var errorsOnly = result.GetValue(errors);

            // --glob and --errors keep the same rows whether they came from the listing or the search.
            List<T> Keep<T>(List<T> rows, Func<T, string> path, Func<T, string?> parseError)
            {
                if (result.GetValue(glob) is { } pattern)
                {
                    var matched = session.Workspace.Glob([pattern], rows.Select(path));
                    rows = rows.Where(row => matched.Contains(path(row))).ToList();
                }
                return errorsOnly ? rows.Where(row => parseError(row) is not null).ToList() : rows;
            }

            if (result.GetValue(query) is not { } text)
            {
                // Only JSON carries the title, and reading one reads its page's whole search row.
                var files = Keep(FileQueries.List(session.Db, filter, titles: session.Json), f => f.Path, f => f.ParseError);
                var listed = files.Take(result.GetValue(limit) ?? int.MaxValue)
                    .Select(f => new FindOutput(f.Path, f.Kind, f.Size, Format.Modified(f.Mtime), f.Title, f.ParseError, null))
                    .ToList();
                session.EmitList(listed, OutputJson.Default.ListFindOutput, file =>
                    errorsOnly ? $"{Format.Safe(file.Path)}: {Format.Safe(file.ParseError!)}" : Format.Safe(file.Path));
                return ExitCode.Clean;
            }

            var search = SearchIndex.Query(text, session.Workspace.Config.SearchTokenizer)
                ?? throw new HippoException("find needs at least one word to look for; leave the query out to list every file");

            // One read transaction, so the rows the matches name are still those rows when their snippets are read.
            using var transaction = session.Db.BeginTransaction(deferred: true);
            var matches = Keep(SearchIndex.Matches(session.Db, transaction, search, filter), m => m.Path, m => m.ParseError);
            var found = matches.Take(result.GetValue(limit) ?? QueryLimit).Select(m =>
            {
                var page = SearchIndex.Text(session.Db, transaction, search, m.Id);
                return new FindOutput(m.Path, m.Kind, m.Size, Format.Modified(m.Mtime), page.Title, m.ParseError, page.Snippet);
            }).ToList();
            transaction.Commit();

            session.Emit(found, OutputJson.Default.ListFindOutput, (writer, results) =>
            {
                foreach (var page in results)
                {
                    writer.WriteLine(page.Title is null ? Format.Safe(page.Path) : $"{Format.Safe(page.Path)}  {Format.Safe(page.Title)}");
                    writer.WriteLine($"  {Format.Safe(page.Snippet!)}");
                }
            });
            return ExitCode.Clean;
        }));
        return command;
    }
}
