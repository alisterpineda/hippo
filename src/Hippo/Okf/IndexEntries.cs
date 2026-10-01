using System.Text.RegularExpressions;
using Hippo.Workspaces;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Hippo.Okf;

/// <summary>
/// Reads the entries of an OKF <c>index.md</c> (§8): each list item that opens with a link, written
/// <c>* [Title](url) - description</c>. Any other link in an index, such as one in a paragraph, is not an entry.
/// </summary>
internal static partial class IndexEntries
{
    public static bool IsIndex(string path) => path.EndsWith("/" + OkfBundle.IndexName, StringComparison.Ordinal);

    /// <summary>The entries in <paramref name="document"/>, parsed from <paramref name="body"/>, which starts on file line
    /// <paramref name="bodyLine"/>. Each gives its link's line and destination, and the text after the link as written,
    /// in <see cref="Normalize"/>d form and without its separator, or null when there is none.</summary>
    public static IEnumerable<(int Line, string Url, string? Description)> Read(MarkdownDocument document, string body, int bodyLine)
    {
        foreach (var item in document.Descendants<ListItemBlock>())
        {
            if (item.Count == 0 || item[0] is not ParagraphBlock { Inline.FirstChild: LinkInline { IsImage: false } link } paragraph)
            {
                continue;
            }
            // As written, not as rendered, so it compares with a description an index generator copied in verbatim.
            var text = Separator().Replace(Normalize(body[(link.Span.End + 1)..(paragraph.Span.End + 1)]), "").TrimStart();
            yield return (link.Line + bodyLine, link.Url ?? "", text.Length == 0 ? null : text);
        }
    }

    /// <summary>Collapses each run of whitespace to one space and trims the ends, so an entry that wraps across lines
    /// reads as one line.</summary>
    public static string Normalize(string text) => PlainText.Collapse(text);

    /// <summary>The separator between an entry's link and its description: a hyphen as §8 writes it, or a dash or
    /// colon.</summary>
    [GeneratedRegex("^[-–—:]")]
    private static partial Regex Separator();
}
