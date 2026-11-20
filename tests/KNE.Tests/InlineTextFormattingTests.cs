using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Xunit;

namespace KNE.Tests;

public sealed class InlineTextFormattingTests
{
    [Fact]
    public void NestedStylesCombineAndClosingEachTagRestoresOnlyItsOwnOuterState()
    {
        var document = InlineText.ParseValues("А{b}Б{i}В{size=18.5}{font=Consolas}{color=red}Г{/color}Д{/font}Е{/size}Ж{/i}З{/b}И", null, name => name == "Consolas");
        Assert.Equal("АБВГДЕЖЗИ", document.Text); Assert.Empty(document.Pauses);
        Style(document, 0); Style(document, 1, bold: true); Style(document, 2, bold: true, italic: true);
        Style(document, 3, "#FF0000", true, true, 18.5, "Consolas"); Style(document, 4, bold: true, italic: true, size: 18.5, font: "Consolas");
        Style(document, 5, bold: true, italic: true, size: 18.5); Style(document, 6, bold: true, italic: true); Style(document, 7, bold: true); Style(document, 8);
    }

    [Fact]
    public void NestedRepeatedStylesRestoreOuterSizesAndKeepBoldAfterAnInnerBoldTagCloses()
    {
        var document = InlineText.Parse("{size=20}А{size=12}{b}Б{b}В{/b}Г{/b}{/size}Д{/size}Е");
        Assert.Equal("АБВГДЕ", document.Text); Style(document, 0, size: 20);
        for (int index = 1; index <= 3; index++) Style(document, index, bold: true, size: 12);
        Style(document, 4, size: 20); Style(document, 5);
    }

    [Theory]
    [InlineData("1", 1)] [InlineData("1.25", 1.25)] [InlineData("18.5", 18.5)] [InlineData("256", 256)]
    public void FontSizesAcceptBothExactBoundariesAndInvariantDecimalValuesEvenInARussianCulture(string literal, double expected)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var document = InlineText.Parse("{size=" + literal + "}ёж{/size}"); Assert.Equal("ёж", document.Text); Style(document, 0, size: expected);
            string encoded = InlineText.ToMarkup(document); Assert.Contains("{size=" + literal + "}", encoded);
            Assert.Equal(expected, Assert.Single(InlineText.Parse(encoded).Runs).Size);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData("0")] [InlineData("0.999")] [InlineData("-1")] [InlineData("256.001")]
    [InlineData("NaN")] [InlineData("Infinity")] [InlineData("18,5")] [InlineData("")]
    public void FontSizesRejectValuesOutsideTheRangeAndNonInvariantOrNonfiniteValues(string size) =>
        Assert.Throws<InvalidDataException>(() => InlineText.Parse("{size=" + size + "}x{/size}"));

    [Theory]
    [InlineData("{/b}")] [InlineData("{b}x")] [InlineData("{b}{i}x{/b}{/i}")]
    [InlineData("{i}x{/size}")] [InlineData("{font=}x{/font}")] [InlineData("{font=bad font}x{/font}")]
    public void MalformedOrMismatchedStyleTagsFailInsteadOfLeakingFormattingIntoFollowingText(string source) =>
        Assert.Throws<InvalidDataException>(() => InlineText.ParseValues(source, null, _ => true));

    [Fact]
    public void FontAliasesRequireTheirOwnConnectedNameAndAcceptTheEightyCharacterBoundary()
    {
        var queried = new List<string>(); string longest = new('a', 80);
        bool Connected(string name) { queried.Add(name); return name == "Consolas" || name == longest; }
        var document = InlineText.ParseValues("{font=Consolas}A{/font}{font=" + longest + "}B{/font}", null, Connected);
        Assert.Equal(new[] { "Consolas", longest }, queried); Style(document, 0, font: "Consolas"); Style(document, 1, font: longest);
        Assert.Throws<InvalidDataException>(() => InlineText.ParseValues("{font=Missing}x{/font}", null, Connected));
        Assert.Throws<InvalidDataException>(() => InlineText.ParseValues("{font=" + longest + "a}x{/font}", null, _ => true));
    }

    [Fact]
    public void SubstitutedTextStaysLiteralAndItsExplicitErrorColorCombinesWithTheAuthoredStyle()
    {
        int calls = 0;
        var document = InlineText.ParseValues("{i}({player}){/i}{w=0.5}", _ => { calls++; return new("{b}literal{/b}", "#808080"); });
        Assert.Equal(1, calls); Assert.Equal("({b}literal{/b})", document.Text);
        Style(document, 0, italic: true); Style(document, 1, color: "#808080", italic: true); Style(document, document.Text.Length - 1, italic: true);
        Assert.Equal(new InlineTextPause(document.Text.Length, .5), Assert.Single(document.Pauses));
        var encoded = InlineText.Parse(InlineText.ToMarkup(document)); Assert.Equal(document.Text, encoded.Text); Style(encoded, 1, "#808080", italic: true);
    }

    [Fact]
    public void ReencodingAnyVisiblePrefixBalancesStylesEscapesBracesAndOmitsLogicalPauses()
    {
        var document = InlineText.ParseValues("до {b}{i}{size=18.5}{font=Consolas}{color=blue}{{А}}Б{w}{/color}{/font}{/size}{/i}{/b} после", null, _ => true);
        Assert.Equal("до {А}Б после", document.Text); Assert.Single(document.Pauses);
        foreach (int requested in new[] { -1, 0, 1, 4, 7, document.Text.Length, 999 })
        {
            int count = Math.Clamp(requested, 0, document.Text.Length);
            string encoded = InlineText.ToMarkup(document, requested); var decoded = InlineText.ParseValues(encoded, null, _ => true);
            Assert.Equal(document.Text[..count], decoded.Text); Assert.Empty(decoded.Pauses);
            for (int index = 0; index < count; index++) AssertStyleEqual(At(document, index), At(decoded, index));
        }
    }

    [Theory]
    [InlineData(false, false, "Аá👩‍💻  BC\tЯ", 8, 12)]
    [InlineData(true, false, "Я  BC\tАá👩‍💻", 3, 7)]
    [InlineData(false, true, "👩‍💻áА  CB\tЯ", 8, 12)]
    [InlineData(true, true, "Я  CB\t👩‍💻áА", 3, 11)]
    public void ReverseModesPreserveWhitespaceGraphemeClustersStylesAndPauseGraphemeCounts(bool words, bool letters, string expected, int timedOffset, int clickOffset)
    {
        var original = InlineText.Parse("{b}Аá👩‍💻{/b}{w=0.5}  {color=red}BC{/color}{w}\t{size=18}Я{/size}");
        var reversed = InlineText.Reverse(original, words, letters);
        Assert.Equal(expected, reversed.Text); Assert.Equal(9, StringInfo.ParseCombiningCharacters(reversed.Text).Length);
        Assert.Equal(new[] { new InlineTextPause(timedOffset, .5), new InlineTextPause(clickOffset, null) }, reversed.Pauses);
        int emoji = reversed.Text.IndexOf("👩‍💻", StringComparison.Ordinal); for (int offset = 0; offset < "👩‍💻".Length; offset++) Style(reversed, emoji + offset, bold: true);
        Style(reversed, reversed.Text.IndexOf('А'), bold: true); Style(reversed, reversed.Text.IndexOf("á", StringComparison.Ordinal), bold: true);
        Style(reversed, reversed.Text.IndexOf('B'), "#FF0000"); Style(reversed, reversed.Text.IndexOf('C'), "#FF0000"); Style(reversed, reversed.Text.IndexOf('Я'), size: 18);
        foreach (int index in Enumerable.Range(0, reversed.Text.Length).Where(index => char.IsWhiteSpace(reversed.Text[index]))) Style(reversed, index);
        Assert.Equal("Аá👩‍💻  BC\tЯ", original.Text); Assert.Equal(8, original.Pauses[0].Offset);
    }

    [Theory]
    [InlineData("")] [InlineData("  \t\r\n")]
    public void ReversingEmptyOrWhitespaceOnlyDocumentsDoesNotLoseTextOrPauses(string text)
    {
        var document = InlineText.Parse("{w}" + text + "{w=0.5}");
        foreach (var mode in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var result = InlineText.Reverse(document, mode.Item1, mode.Item2); Assert.Equal(text, result.Text); Assert.Equal(document.Pauses, result.Pauses); Assert.Empty(result.Runs);
        }
    }

    [Fact]
    public Task PresentationAppliesCombinedStylesAndReturnsToInheritedFormattingOnTheNextPlainLine() => WorkspaceTabsTests.Sta(() =>
    {
        var target = new TextBlock { FontSize = 30, FontWeight = FontWeights.Light, FontFamily = new FontFamily("Segoe UI"), Foreground = Brushes.White };
        InlineTextPresentation.Apply(target, "plain {b}bold {i}{size=18.5}{color=red}both{/color}{/size}{/i}{/b} tail");
        Assert.Equal("plain bold both tail", target.Text);
        var plain = target.Inlines.OfType<Run>().Single(run => run.Text == "plain ");
        Assert.Equal(FontWeights.Light, plain.FontWeight); Assert.Equal(30, plain.FontSize); Assert.Same(Brushes.White, plain.Foreground);
        var bold = target.Inlines.OfType<Run>().Single(run => run.Text == "bold "); Assert.Equal(FontWeights.Bold, bold.FontWeight); Assert.Equal(FontStyles.Normal, bold.FontStyle); Assert.Equal(30, bold.FontSize);
        var both = target.Inlines.OfType<Run>().Single(run => run.Text == "both"); Assert.Equal(FontWeights.Bold, both.FontWeight); Assert.Equal(FontStyles.Italic, both.FontStyle); Assert.Equal(18.5, both.FontSize);
        Assert.Equal(Colors.Red, Assert.IsType<SolidColorBrush>(both.Foreground).Color);
        InlineTextPresentation.Apply(target, "unstyled"); Assert.Equal("unstyled", target.Text);
        var reset = Assert.Single(target.Inlines.OfType<Run>()); Assert.Equal(FontWeights.Light, reset.FontWeight); Assert.Equal(FontStyles.Normal, reset.FontStyle); Assert.Equal(30, reset.FontSize); Assert.Same(Brushes.White, reset.Foreground);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SuffixPresentationRevealsWholeGraphemesAndClipsTheirStyleRangesWithoutChangingTheUnderlyingMarkup() => WorkspaceTabsTests.Sta(() =>
    {
        const string source = "lead {b}á👩‍💻Z{/b}"; var target = new TextBlock();
        foreach (var (count, expected) in new[] { (0, ""), (1, "Z"), (2, "👩‍💻Z"), (3, "á👩‍💻Z"), (100, "lead á👩‍💻Z"), (2, "👩‍💻Z") })
        {
            InlineTextPresentation.Apply(target, source, visibleSuffix: count); Assert.Equal(expected, target.Text);
            if (count is >= 1 and <= 3) Assert.All(target.Inlines.OfType<Run>(), run => Assert.Equal(FontWeights.Bold, run.FontWeight));
        }
        Assert.Equal("lead á👩‍💻Z", InlineText.Parse(source).Text); return Task.CompletedTask;
    });

    [Fact]
    public Task ConnectedFontContextIsInheritedAndReconnectingAnAliasInvalidatesTheCachedPresentation() => WorkspaceTabsTests.Sta(() =>
    {
        using var fixture = new TempProject();
        Assert.True(new Typeface("Segoe UI").TryGetGlyphTypeface(out var installed));
        string first = fixture.Write("fonts/first.ttf", ""), second = fixture.Write("fonts/second.ttf", "");
        File.Copy(installed.FontUri.LocalPath, first, overwrite: true); File.Copy(installed.FontUri.LocalPath, second, overwrite: true);
        var parent = new Grid(); var target = new TextBlock(); parent.Children.Add(target);
        InlineTextPresentation.SetFonts(parent, fixture.ProjectPath, new Dictionary<string, string> { ["StoryFont"] = "fonts/first.ttf" });
        InlineTextPresentation.Apply(target, "{font=StoryFont}текст{/font}"); var old = Assert.Single(target.Inlines.OfType<Run>()).FontFamily;
        Assert.Contains("first.ttf#", old.Source);
        InlineTextPresentation.SetFonts(parent, fixture.ProjectPath, new Dictionary<string, string> { ["StoryFont"] = "fonts/second.ttf" });
        InlineTextPresentation.Apply(target, "{font=StoryFont}текст{/font}"); var current = Assert.Single(target.Inlines.OfType<Run>()).FontFamily;
        Assert.Contains("second.ttf#", current.Source); Assert.NotSame(old, current); Assert.Equal("текст", target.Text);
        var face = new Typeface(current, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Assert.True(face.TryGetGlyphTypeface(out var glyphs));
        Assert.Equal(second, glyphs.FontUri.LocalPath, ignoreCase: true);
        return Task.CompletedTask;
    });

    static InlineTextRun At(InlineTextDocument document, int index) => document.Runs.SingleOrDefault(run => run.Start <= index && index < run.Start + run.Length) ?? new(index, 1, null);
    static void Style(InlineTextDocument document, int index, string? color = null, bool bold = false, bool italic = false, double? size = null, string? font = null)
    {
        var run = At(document, index); Assert.Equal(color, run.Color); Assert.Equal(bold, run.Bold); Assert.Equal(italic, run.Italic); Assert.Equal(size, run.Size); Assert.Equal(font, run.Font);
    }
    static void AssertStyleEqual(InlineTextRun expected, InlineTextRun actual)
    {
        Assert.Equal(expected.Color, actual.Color); Assert.Equal(expected.Bold, actual.Bold); Assert.Equal(expected.Italic, actual.Italic); Assert.Equal(expected.Size, actual.Size); Assert.Equal(expected.Font, actual.Font);
    }
}
