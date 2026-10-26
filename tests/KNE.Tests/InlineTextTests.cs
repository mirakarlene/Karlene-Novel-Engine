using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class InlineTextTests
{
    [Theory]
    [InlineData("red", "#FF0000")]
    [InlineData("blue", "#0000FF")]
    [InlineData("green", "#008000")]
    [InlineData("yellow", "#FFFF00")]
    [InlineData("white", "#FFFFFF")]
    [InlineData("black", "#000000")]
    [InlineData("gray", "#808080")]
    [InlineData("orange", "#FFA500")]
    [InlineData("purple", "#800080")]
    [InlineData("pink", "#FFC0CB")]
    [InlineData("cyan", "#00FFFF")]
    [InlineData("magenta", "#FF00FF")]
    [InlineData("#ff0000", "#FF0000")]
    [InlineData("rgb(255,0,0)", "#FF0000")]
    [InlineData("rgb(0, 128, 255)", "#0080FF")]
    public void Colors_StripTagsAndApplyOnlyToSelectedText(string color, string expected)
    {
        var document = InlineText.Parse("До {color=" + color + "}цвет{/color} после");
        Assert.Equal("До цвет после", document.Text);
        Assert.Equal(new InlineTextRun(3, 4, expected), Assert.Single(document.Runs));
        Assert.Empty(document.Pauses);
    }

    [Fact]
    public void NestedColors_RestoreOuterColor_AndPreserveUnicodeAndNewlines()
    {
        var document = InlineText.Parse("{color=red}А{color=blue}🙂{/color}\nБ{/color}В");
        Assert.Equal("А🙂\nБВ", document.Text);
        Assert.Equal(new[] { new InlineTextRun(0, 1, "#FF0000"), new(1, 2, "#0000FF"), new(3, 2, "#FF0000") }, document.Runs);
    }

    [Fact]
    public void Pauses_UseDisplayedOffsets_AndDistinguishClickFromZeroAndFractionalDurations()
    {
        var document = InlineText.Parse("{w}А{color=red}🙂{w=0.5}Б{/color}{w=0}{w=3600}");
        Assert.Equal("А🙂Б", document.Text);
        Assert.Equal(new[] { new InlineTextPause(0, null), new(3, .5), new(4, 0), new(4, 3600) }, document.Pauses);
        Assert.Equal(new InlineTextRun(1, 3, "#FF0000"), Assert.Single(document.Runs));
    }

    [Fact]
    public void Variables_AreResolvedOnce_AsLiteralTextInsideCurrentColor()
    {
        var keys = new List<string>();
        var document = InlineText.Parse("{color=red}Привет, {player}!{/color}{w=0.5}{score}", key =>
        {
            keys.Add(key); return key == "player" ? "{w}{color=blue}🙂" : "42";
        });
        Assert.Equal(new[] { "player", "score" }, keys);
        Assert.Equal("Привет, {w}{color=blue}🙂!42", document.Text);
        Assert.Equal(new InlineTextRun(0, document.Text.Length - 2, "#FF0000"), Assert.Single(document.Runs));
        Assert.Equal(new InlineTextPause(document.Text.Length - 2, .5), Assert.Single(document.Pauses));
    }

    [Fact]
    public void OrdinaryBracesAndUnresolvedTemplates_ArePreservedWhenNoResolverIsProvided()
    {
        const string text = "{player} JSON: {\"key\": 3} и {unknown=tag}, } {";
        var document = InlineText.Parse(text);
        Assert.Equal(text, document.Text); Assert.Empty(document.Runs); Assert.Empty(document.Pauses);
    }

    [Theory]
    [InlineData("{color=ultraviolet}x{/color}")]
    [InlineData("{color=#GG0000}x{/color}")]
    [InlineData("{color=rgb(256,0,0)}x{/color}")]
    [InlineData("{color=rgb(1,2)}x{/color}")]
    [InlineData("{color=red}x")]
    [InlineData("x{/color}")]
    [InlineData("{w=-1}")]
    [InlineData("{w=NaN}")]
    [InlineData("{w=Infinity}")]
    [InlineData("{w=3600.1}")]
    [InlineData("{w=0,5}")]
    public void MalformedReservedTags_ReportSourceErrors(string source) => Assert.Throws<InvalidDataException>(() => InlineText.Parse(source));
}
