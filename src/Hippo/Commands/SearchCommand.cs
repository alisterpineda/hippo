using System.CommandLine;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class SearchCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var query = new Argument<string>("query") { Description = "Words every page found must contain" };
        var glob = new Option<string>("--glob") { Description = "Only paths matching this workspace-relative glob", HelpName = "pattern" };
        var where = new Option<string>("--where") { Description = "Only pages whose frontmatter field equals value", HelpName = "field=value" };
        var limit = new Option<int>("--limit") { Description = "At most this many results", HelpName = "n", DefaultValueFactory = _ => 20 };
        limit.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 1)
            {
                result.AddError("--limit must be at least 1");
            }
        });
        var command = new Command("search", "Find pages by what they say, best match first, with a snippet of each")
        {
            query,
            glob,
            where,
            limit,
            WorkspaceSession.JsonOption,
        };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var text = result.GetValue(query)!;
            var search = SearchIndex.Query(text, session.Workspace.Config.SearchTokenizer)
                ?? throw new HippoException("search needs at least one word to look for");
            var filter = result.GetValue(where) is { } condition ? FrontmatterFilter.Parse(condition) : null;

            // One read transaction, so the rows the matches name are still those rows when their snippets are read.
            using var transaction = session.Db.BeginTransaction(deferred: true);
            var matches = SearchIndex.Matches(session.Db, transaction, search, filter);
            if (result.GetValue(glob) is { } pattern)
            {
                var matched = session.Workspace.Glob([pattern], matches.Select(m => m.Path));
                matches = matches.Where(m => matched.Contains(m.Path)).ToList();
            }
            var output = matches.Take(result.GetValue(limit)).Select(m =>
            {
                var found = SearchIndex.Text(session.Db, transaction, search, m.Id);
                return new SearchOutput(m.Path, found.Title.Length == 0 ? null : found.Title, found.Snippet);
            }).ToList();
            transaction.Commit();

            session.Emit(output, OutputJson.Default.ListSearchOutput, (writer, results) =>
            {
                foreach (var found in results)
                {
                    writer.WriteLine(found.Title is null ? Format.Safe(found.Path) : $"{Format.Safe(found.Path)}  {Format.Safe(found.Title)}");
                    writer.WriteLine($"  {Format.Safe(found.Snippet)}");
                }
            });
            return ExitCode.Clean;
        }));
        return command;
    }
}
