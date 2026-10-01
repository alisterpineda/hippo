using System.Text;
using System.Text.RegularExpressions;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Hippo.Workspaces;

/// <summary>Markdown read as the text it shows, without its markup.</summary>
internal static partial class PlainText
{
    /// <summary>The text of <paramref name="inline"/>, such as a heading's, as it reads: literals and code as written,
    /// entities decoded, autolinks as their URL and each line break a space.</summary>
    public static string Of(ContainerInline? inline)
    {
        var text = new StringBuilder();
        foreach (var node in inline?.Descendants() ?? [])
        {
            switch (node)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case HtmlEntityInline entity:
                    text.Append(entity.Transcoded.AsSpan());
                    break;
                case AutolinkInline autolink:
                    text.Append(autolink.Url);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
            }
        }
        return text.ToString();
    }

    /// <summary>Collapses each run of whitespace to one space and trims the ends, so text that wraps across lines reads
    /// as one line.</summary>
    public static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
