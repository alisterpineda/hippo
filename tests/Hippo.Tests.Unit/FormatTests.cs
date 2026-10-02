using System.Buffers;
using System.Text;
using System.Text.Json;
using Hippo.Commands;

namespace Hippo.Tests.Unit;

public class FormatTests
{
    [Fact]
    public void Control_characters_are_escaped_but_tab_and_text_are_kept()
    {
        Assert.Equal("a\\x1b]0;T\\x07\tb\\x9bé", Format.Safe("a\u001b]0;T\u0007\tb\u009bé"));
    }

    [Fact]
    public void Text_without_control_characters_is_returned_as_is()
    {
        const string text = "wiki/a b.md";

        Assert.Same(text, Format.Safe(text));
    }

    [Fact]
    public void Safe_lines_keeps_cr_and_lf_breaks_but_escapes_form_feed_and_nel()
    {
        var nl = Environment.NewLine;

        Assert.Equal($"a\\x0cb{nl}c\\x85d{nl}e{nl}f\\x1b", Format.SafeLines("a\u000cb\r\nc\u0085d\re\nf\u001b"));
    }

    [Fact]
    public void Indented_lays_out_json_as_the_serializer_does()
    {
        using var document = JsonDocument.Parse(
            """{"a":"x","b":[1,-2.5e3,true,false,null],"c":{},"d":[],"e":{"f":[{"g":"h"}]}}""");
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            document.RootElement.WriteTo(writer);
        }

        Assert.Equal(Encoding.UTF8.GetString(buffer.WrittenSpan), Format.Indented(document.RootElement));
    }

    [Fact]
    public void Compact_writes_json_on_one_line_with_no_spaces()
    {
        using var document = JsonDocument.Parse("""{ "a" : [1, "x y", {"b": null}], "c": {}, "d": [] }""");

        Assert.Equal("""{"a":[1,"x y",{"b":null}],"c":{},"d":[]}""", Format.Compact(document.RootElement));
    }

    [Fact]
    public void Compact_escapes_strings_as_indented_does()
    {
        using var document = JsonDocument.Parse("""["café \"q\" \n"]""");

        Assert.Equal("""["café \"q\" \n"]""", Format.Compact(document.RootElement));
    }

    [Fact]
    public void Indented_escapes_quotes_backslashes_and_control_characters_but_not_other_text()
    {
        using var document = JsonDocument.Parse(
            """{"k\"ey":"it's <a> & caf\u00e9 東京 🏠 \"q\" \\ \n\r\t \u001b]0;T\u0007 \u009b"}""");

        Assert.Equal(
            $$"""{{{Environment.NewLine}}  "k\"ey": "it's <a> & café 東京 🏠 \"q\" \\ \n\r\t \u001B]0;T\u0007 \u009B"{{Environment.NewLine}}}""",
            Format.Indented(document.RootElement));
    }
}
