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
}
