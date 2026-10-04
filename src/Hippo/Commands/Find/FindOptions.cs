namespace Hippo.Commands.Find;

/// <summary>
/// What <c>find</c> was asked for, bound from the command line and parsed as far as parsing can go without the
/// index: each <c>--where</c> is a <see cref="FrontmatterFilter"/>, and <see cref="Fields"/> are the paths of every
/// <c>--field</c>, or null without one. <see cref="FindCommand.Run"/> takes it in place of the command line, so a test
/// can ask what <c>find</c> would list without parsing arguments or reading its output. Every member defaults to the
/// command line's own default, so a test sets only what it is about.
/// </summary>
internal sealed record FindOptions
{
    /// <summary>The words and phrases every page listed must hold, or null to list every file.</summary>
    public string? Query { get; init; }

    /// <summary>The <c>--glob</c> patterns, a leading <c>!</c> leaving out what it matches.</summary>
    public IReadOnlyList<string> Globs { get; init; } = [];

    /// <summary>The <c>--kind</c>, <c>markdown</c> or <c>other</c>, or null for both.</summary>
    public string? Kind { get; init; }

    /// <summary>The <c>--where</c> conditions, every one of which a file must meet.</summary>
    public IReadOnlyList<FrontmatterFilter> Where { get; init; } = [];

    /// <summary>The paths of every <c>--field</c>, or null without one, which leaves the fields out of the output.</summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>Only files whose frontmatter failed to parse.</summary>
    public bool ErrorsOnly { get; init; }

    /// <summary>Only files with no link to another indexed file.</summary>
    public bool NoRefs { get; init; }

    /// <summary>Only files no other file links to.</summary>
    public bool NoBackrefs { get; init; }

    /// <summary>With <see cref="NoBackrefs"/>, the <c>--from</c> patterns of the files whose links count.</summary>
    public IReadOnlyList<string> From { get; init; } = [];

    /// <summary>With <see cref="NoRefs"/> or <see cref="NoBackrefs"/>, the one kind of link that counts.</summary>
    public string? LinkKind { get; init; }

    /// <summary>At most this many results, or null for the default: none without a query, and with one,
    /// <see cref="FindCommand.QueryLimit"/>.</summary>
    public int? Limit { get; init; }
}
