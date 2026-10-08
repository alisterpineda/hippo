using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Hippo.Indexing;
using Hippo.Workspaces;
using Microsoft.Data.Sqlite;

namespace Hippo.Commands.Find;

internal static class FindCommand
{
    /// <summary>How many pages a query lists when no <c>--limit</c> is given. A listing without a query has no cap.</summary>
    internal const int QueryLimit = 20;

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
            Description = "Only markdown files whose frontmatter field meets the condition: field=value, field!=value, "
                + "field<value, field<=value, field>value, field>=value, field (present) or !field (missing); repeatable, and "
                + "every condition must hold. A field is a dotted path, and a part ending in [] means each element of that list, "
                + "as sources[].id=x. A value starting with @ names a field of the same page, as 'verified.at<@generated.at', "
                + "and @@ writes a literal @; a value without a leading @ is always a literal. Numbers compare as numbers, "
                + "anything else as text, so write dates as ISO 8601. Quote it for the shell, as --where 'as_of<2026-04-01'",
            HelpName = "condition",
        };
        var field = new Option<string[]>("--field")
        {
            Description = "Show this frontmatter field of each file, a dotted path such as verified.at, or several separated "
                + "by commas; repeatable. A part ending in [] means each element of that list, so sources[].resource shows the "
                + "list of values. A list or mapping shows whole; keys holding ., ,, [ or ] cannot be reached",
            HelpName = "path",
        };
        var errors = new Option<bool>("--errors") { Description = "Only files whose frontmatter failed to parse, with the error" };
        var noRefs = new Option<bool>("--no-refs") { Description = "Only files with no link to another indexed file" };
        var noBackrefs = new Option<bool>("--no-backrefs") { Description = "Only files no other file links to" };
        var from = new Option<string[]>("--from")
        {
            Description = "With --no-backrefs, count only links from files matching this workspace-relative glob, or with a "
                + "leading !, not matching it; repeatable",
            HelpName = "pattern",
        };
        var linkKind = WorkspaceSession.LinkKindOption("With --no-refs or --no-backrefs, count only links of this kind");
        var limit = new Option<int?>("--limit")
        {
            Description = $"At most this many results ({QueryLimit} by default with a query)",
            HelpName = "n",
        };
        limit.Validators.Add(result =>
        {
            // The token is read, not the value: a value that is not a number has its own error, and reading it throws.
            if (result.Tokens is [{ Value: var value }] && int.TryParse(value, CultureInfo.InvariantCulture, out var n) && n < 1)
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
            from,
            linkKind,
            limit,
            WorkspaceSession.JsonOption,
        };
        // An option with nothing to narrow would silently do nothing, so it is an error instead.
        command.Validators.Add(result =>
        {
            // Whether an option was given is read from its result, not its value, which throws when the value is invalid.
            if (result.GetResult(from) is not null && !result.GetValue(noBackrefs))
            {
                result.AddError("--from needs --no-backrefs");
            }
            if (result.GetResult(linkKind) is not null && !result.GetValue(noRefs) && !result.GetValue(noBackrefs))
            {
                result.AddError("--link-kind needs --no-refs or --no-backrefs");
            }
        });

        // The parse result as FindOptions: each value read once, and the --where and --field values parsed, which is
        // where a malformed one fails.
        FindOptions Bind(ParseResult result) => new()
        {
            Query = result.GetValue(query),
            Globs = result.GetValue(glob) ?? [],
            Kind = result.GetValue(kind),
            Where = (result.GetValue(where) ?? []).Select(FrontmatterFilter.Parse).ToList(),
            Fields = result.GetValue(field) is { Length: > 0 } paths ? FrontmatterFields.ParsePaths(paths) : null,
            ErrorsOnly = result.GetValue(errors),
            NoRefs = result.GetValue(noRefs),
            NoBackrefs = result.GetValue(noBackrefs),
            From = result.GetValue(from) ?? [],
            LinkKind = result.GetValue(linkKind),
            Limit = result.GetValue(limit),
        };

        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var options = Bind(result);
            // Only JSON carries the title, and reading one reads its page's whole search row.
            var found = Run(options, session.Workspace, session.Db, titles: session.Json);
            session.Emit(found, OutputJson.Default.FindListOutput, (writer, results) => WriteText(writer, results.Files, options, session.Styled));
            return ExitCode.Clean;
        }));
        return command;
    }

    /// <summary>
    /// What <c>find</c> lists for <paramref name="options"/>, as the records <c>--json</c> prints: without a query,
    /// every indexed file in path order, and with one, the pages holding it, best match first, each with its snippet.
    /// <paramref name="titles"/> says whether a listing carries each page's title, which costs reading its whole search
    /// row; a query's results always carry it. Throws a <see cref="HippoException"/> for a query with no word in it.
    /// </summary>
    internal static FindListOutput Run(FindOptions options, Workspace workspace, SqliteConnection db, bool titles)
    {
        // Only the frontmatter filters and fields read a file's frontmatter, so without them it is not read at all.
        var readFrontmatter = options.Where.Count > 0 || options.Fields is not null;

        if (options.Query is not { } text)
        {
            // One read transaction, so the listing and each link filter see the index as it was at one moment.
            using var listing = db.BeginTransaction(deferred: true);
            var files = Keep(FileQueries.List(db, titles, readFrontmatter, listing), options, workspace, db, listing);
            listing.Commit();
            return new FindListOutput(files.Take(options.Limit ?? int.MaxValue)
                .Select(f => new FindOutput(f.Path, f.Kind, f.Size, Format.Modified(f.Mtime), f.Title, f.ParseError, null,
                    Fields(options, f)))
                .ToList());
        }

        var search = SearchIndex.Query(text, workspace.Config.SearchTokenizer)
            ?? throw new HippoException("find needs at least one word to look for; leave the query out to list every file");

        // One read transaction, so the rows the matches name are still those rows when their snippets are read.
        using var transaction = db.BeginTransaction(deferred: true);
        var matches = Keep(SearchIndex.Matches(db, transaction, search, readFrontmatter), options, workspace, db, transaction);
        var found = matches.Take(options.Limit ?? QueryLimit).Select(m =>
        {
            var page = SearchIndex.Text(db, transaction, search, m.Id);
            return new FindOutput(m.Path, m.Kind, m.Size, Format.Modified(m.Mtime), page.Title, m.ParseError, page.Snippet,
                Fields(options, m));
        }).ToList();
        transaction.Commit();
        return new FindListOutput(found);
    }

    /// <summary>The rows of <paramref name="rows"/> that every filter in <paramref name="options"/> keeps. The filters
    /// keep the same rows whether they came from the listing or the search. A file the glob leaves out still has its
    /// links count toward <c>--no-refs</c> and <c>--no-backrefs</c>, since those are read from the whole index; only
    /// <c>--from</c> and <c>--link-kind</c> narrow which links count.</summary>
    private static List<T> Keep<T>(List<T> rows, FindOptions options, Workspace workspace, SqliteConnection db, SqliteTransaction transaction)
        where T : IFileRow
    {
        if (options.Globs.Count > 0)
        {
            var matched = workspace.Glob(options.Globs, rows.Select(row => row.Path));
            rows = rows.Where(row => matched.Contains(row.Path)).ToList();
        }
        if (options.Kind is { } only)
        {
            rows = rows.Where(row => row.Kind == only).ToList();
        }
        // Only a page has frontmatter, so every condition is a question about pages, negations included.
        if (options.Where.Count > 0)
        {
            rows = rows.Where(row => row.Kind == "markdown" && FrontmatterFilter.MatchesAll(options.Where, row.Frontmatter, row.ParseError))
                .ToList();
        }
        if (options.NoRefs)
        {
            var unlinked = LinkQueries.WithoutRefs(db, transaction, options.LinkKind);
            rows = rows.Where(row => unlinked.Contains(row.Path)).ToList();
        }
        if (options.NoBackrefs)
        {
            var sources = options.From.Count > 0 ? workspace.Glob(options.From, LinkQueries.Sources(db, transaction)) : null;
            var unlinked = LinkQueries.WithoutBackrefs(db, transaction, options.LinkKind, sources);
            rows = rows.Where(row => unlinked.Contains(row.Path)).ToList();
        }
        return options.ErrorsOnly ? rows.Where(row => row.ParseError is not null).ToList() : rows;
    }

    /// <summary>A result's <c>--field</c> values, or null without <c>--field</c>, so the JSON leaves them out.</summary>
    private static Dictionary<string, JsonElement>? Fields(FindOptions options, IFileRow row) =>
        options.Fields is null ? null : FrontmatterFields.Read(options.Fields, row.Frontmatter);

    /// <summary>The text form of <paramref name="results"/>. A record without a snippet is from the listing: one line,
    /// its path and under <c>--errors</c> its parse error. One with a snippet is a page the query matched: its path and
    /// title on a line and the snippet under it. The record decides, so the form is chosen once, in <see cref="Run"/>.</summary>
    private static void WriteText(TextWriter writer, List<FindOutput> results, FindOptions options, bool styled)
    {
        foreach (var result in results)
        {
            if (result.Marked is not { } marked)
            {
                writer.WriteLine((options.ErrorsOnly ? $"{Format.Safe(result.Path)}: {Format.Safe(result.ParseError!)}" : Format.Safe(result.Path))
                    + FieldText(result));
                continue;
            }
            writer.WriteLine((result.Title is null ? Format.Safe(result.Path) : $"{Format.Safe(result.Path)}  {Format.Safe(result.Title)}")
                + FieldText(result));
            writer.WriteLine($"  {Format.Snippet(marked, styled)}");
        }
    }

    /// <summary>The fields <c>--field</c> asked for, as they end a result's line: each <c>  key=value</c>, a string as
    /// written, and anything else as compact JSON.</summary>
    private static string FieldText(FindOutput result) =>
        string.Concat((result.Fields ?? []).Select(f =>
            $"  {Format.Safe(f.Key)}={Format.Safe(f.Value.ValueKind == JsonValueKind.String ? f.Value.GetString()! : Format.Compact(f.Value))}"));
}
