using System.CommandLine;
using System.CommandLine.Help;
using System.Diagnostics;
using Hippo.Cache;

namespace Hippo.Commands;

/// <summary>
/// <c>hippo cache list</c> and <c>hippo cache prune</c>: the indexes under the cache root, whichever workspaces they
/// belong to. Like <c>init</c>, they need no workspace, so they do not open a <see cref="WorkspaceSession"/>. Prune only
/// ever removes on request: an index costs a full reindex to rebuild, so none is removed on the way through another
/// command.
/// </summary>
internal static class CacheCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var command = new Command("cache", "List the indexes in the cache, or remove those whose workspace is gone");
        command.SetAction(result => new HelpAction().Invoke(result));
        command.Subcommands.Add(BuildList(environment));
        command.Subcommands.Add(BuildPrune(environment));
        return command;
    }

    private static Command BuildList(CliEnvironment environment)
    {
        var command = new Command("list", "List every index with its workspace root, state and size") { WorkspaceSession.JsonOption };
        command.SetAction(result => Guard.Run(result.InvocationConfiguration.Error, () =>
        {
            var indexes = CachedIndexes.List(CacheLocation.CacheRoot(environment.GetVariable), environment.GetMountPoints());
            WorkspaceSession.EmitList(
                result.InvocationConfiguration.Output,
                result.GetValue(WorkspaceSession.JsonOption),
                new CacheListOutput(Records(indexes)),
                OutputJson.Default.CacheListOutput,
                o => o.Indexes,
                index => $"{index.State,-11} {index.Size,12}  {Format.Safe(index.Root ?? index.Database)}");
            return ExitCode.Clean;
        }));
        return command;
    }

    private static Command BuildPrune(CliEnvironment environment)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "List what would be removed, and remove nothing" };
        var includeUnreachable = new Option<bool>("--include-unreachable")
        {
            Description = "Also remove indexes whose root cannot be reached, such as one on an unmounted drive",
        };
        var command = new Command("prune", "Remove the indexes of workspaces that were deleted, moved or renamed")
        {
            dryRun, includeUnreachable, WorkspaceSession.JsonOption,
        };
        command.SetAction(result => Guard.Run(result.InvocationConfiguration.Error, () =>
        {
            var error = result.InvocationConfiguration.Error;
            var cacheRoot = CacheLocation.CacheRoot(environment.GetVariable);
            var preview = result.GetValue(dryRun);
            var candidates = CachedIndexes.List(cacheRoot, environment.GetMountPoints())
                .Where(index => index.State == IndexState.Orphaned || result.GetValue(includeUnreachable) && index.State == IndexState.Unreachable)
                .ToList();

            var removed = new List<CachedIndex>();
            var failed = false;
            if (preview)
            {
                removed = candidates;
            }
            else
            {
                var warnings = CachedIndexes.RemoveLeftovers(cacheRoot);
                foreach (var index in candidates)
                {
                    try
                    {
                        if (CachedIndexes.Remove(index) is { } warning)
                        {
                            warnings.Add(warning);
                        }
                        removed.Add(index);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        error.WriteLine($"hippo: cannot remove the index of {Format.Safe(index.Root!)}: {Format.Safe(ex.Message)}");
                        failed = true;
                    }
                }
                WorkspaceSession.Warn(error, warnings);
            }

            var verb = preview ? "Would remove" : "Removed";
            var json = result.GetValue(WorkspaceSession.JsonOption);
            WorkspaceSession.EmitList(
                result.InvocationConfiguration.Output,
                json,
                new CachePruneOutput(preview, Records(removed)),
                OutputJson.Default.CachePruneOutput,
                o => o.Removed,
                index => $"{verb} {Format.Safe(index.Root!)} ({index.Size} bytes)");
            if (!json)
            {
                var output = result.InvocationConfiguration.Output;
                output.WriteLine(removed.Count == 0
                    ? "Nothing to remove."
                    : $"{verb} {removed.Count} {(removed.Count == 1 ? "index" : "indexes")}, {removed.Sum(i => i.Size)} bytes.");
            }
            return failed ? ExitCode.Error : ExitCode.Clean;
        }));
        return command;
    }

    /// <summary><paramref name="indexes"/> as the records <c>--json</c> prints.</summary>
    private static List<CacheIndexOutput> Records(List<CachedIndex> indexes) =>
        indexes.Select(i => new CacheIndexOutput(i.Database, i.Root, Name(i.State), i.Size)).ToList();

    private static string Name(IndexState state) => state switch
    {
        IndexState.Live => "live",
        IndexState.Orphaned => "orphaned",
        IndexState.Unreachable => "unreachable",
        IndexState.Unknown => "unknown",
        _ => throw new UnreachableException(state.ToString()),
    };
}
