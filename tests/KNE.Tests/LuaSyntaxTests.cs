using Xunit;

namespace KNE.Tests;

public sealed class LuaSyntaxTests
{
    [Fact]
    public void Parse_KeywordsControlsKrlAndCustomCalls_HaveDistinctKindsAndExactOffsets()
    {
        const string source = "local x = true\nfunction main()\nif x then d(nil, 'Привет') end\ngoto(next_scene)\nend";
        Assert.Equal(new LuaToken[]
        {
            new(0, 5, LuaTokenKind.Keyword), new(6, 1, LuaTokenKind.Identifier), new(8, 1, LuaTokenKind.Operator),
            new(10, 4, LuaTokenKind.Keyword), new(15, 8, LuaTokenKind.Keyword), new(24, 4, LuaTokenKind.Function),
            new(28, 1, LuaTokenKind.Bracket, 0, 29), new(29, 1, LuaTokenKind.Bracket, 0, 28),
            new(31, 2, LuaTokenKind.Control), new(34, 1, LuaTokenKind.Identifier), new(36, 4, LuaTokenKind.Control),
            new(41, 1, LuaTokenKind.Function), new(42, 1, LuaTokenKind.Bracket, 0, 56), new(43, 3, LuaTokenKind.Keyword),
            new(46, 1, LuaTokenKind.Operator), new(48, 8, LuaTokenKind.String), new(56, 1, LuaTokenKind.Bracket, 0, 42),
            new(58, 3, LuaTokenKind.Control), new(62, 4, LuaTokenKind.Control), new(66, 1, LuaTokenKind.Bracket, 0, 77),
            new(67, 10, LuaTokenKind.Identifier), new(77, 1, LuaTokenKind.Bracket, 0, 66), new(79, 3, LuaTokenKind.Control)
        }, LuaSyntax.Parse(source));
    }

    [Fact]
    public void Parse_FunctionCallsSkipComments_AndIncompleteDeclarationsRemainColored()
    {
        const string source = "custom -- () ignored\n(1)\nfunction -- declaration\nunfinished";
        Assert.Equal(new LuaToken[]
        {
            new(0, 6, LuaTokenKind.Function), new(7, 13, LuaTokenKind.Comment), new(21, 1, LuaTokenKind.Bracket, 0, 23),
            new(22, 1, LuaTokenKind.Number), new(23, 1, LuaTokenKind.Bracket, 0, 21), new(25, 8, LuaTokenKind.Keyword),
            new(34, 14, LuaTokenKind.Comment), new(49, 10, LuaTokenKind.Function)
        }, LuaSyntax.Parse(source));
    }

    [Fact]
    public void Parse_QualifiedFunctionDeclaration_ColorsTheMemberNameAndPreservesTheTableIdentifier()
    {
        Assert.Equal(new LuaToken[]
        {
            new(0, 8, LuaTokenKind.Keyword), new(9, 6, LuaTokenKind.Identifier), new(15, 1, LuaTokenKind.Operator),
            new(16, 6, LuaTokenKind.Function), new(22, 1, LuaTokenKind.Bracket, 0, 23), new(23, 1, LuaTokenKind.Bracket, 0, 22)
        }, LuaSyntax.Parse("function object:method()"));
        Assert.Equal(new LuaToken[]
        {
            new(0, 8, LuaTokenKind.Keyword), new(9, 6, LuaTokenKind.Identifier), new(15, 1, LuaTokenKind.Operator), new(16, 6, LuaTokenKind.Function)
        }, LuaSyntax.Parse("function object.method"));
    }

    [Theory]
    [InlineData("d", LuaTokenKind.Function)]
    [InlineData("background", LuaTokenKind.Function)]
    [InlineData("show_character", LuaTokenKind.Function)]
    [InlineData("call", LuaTokenKind.Function)]
    [InlineData("init", LuaTokenKind.Function)]
    [InlineData("jump", LuaTokenKind.Function)]
    [InlineData("print_to_ui", LuaTokenKind.Function)]
    [InlineData("pcall", LuaTokenKind.Function)]
    [InlineData("false", LuaTokenKind.Keyword)]
    [InlineData("and", LuaTokenKind.Control)]
    [InlineData("in", LuaTokenKind.Control)]
    [InlineData("until", LuaTokenKind.Control)]
    [InlineData("returnValue", LuaTokenKind.Identifier)]
    [InlineData("D", LuaTokenKind.Identifier)]
    public void Parse_ReservedAndApiNames_AreCaseSensitiveWholeIdentifiers(string source, LuaTokenKind kind)
    {
        Assert.Equal(new[] { new LuaToken(0, source.Length, kind) }, LuaSyntax.Parse(source));
    }

    [Theory]
    [InlineData("-- line with )\r\nx", "-- line with )", 16)]
    [InlineData("--[==[ ) ]=] still comment ]==]\nx", "--[==[ ) ]=] still comment ]==]", 32)]
    [InlineData("--[[ nested [[ is plain text ]]\nx", "--[[ nested [[ is plain text ]]", 32)]
    [InlineData("--[=[ never closed ]==]", "--[=[ never closed ]==]", -1)]
    public void Parse_CommentsRequireExactLongDelimiter_AndNeverCreateBracketTokens(string source, string comment, int identifierOffset)
    {
        var expected = new List<LuaToken> { new(0, comment.Length, LuaTokenKind.Comment) };
        if (identifierOffset >= 0) expected.Add(new(identifierOffset, 1, LuaTokenKind.Identifier));
        Assert.Equal(expected, LuaSyntax.Parse(source));
    }

    [Theory]
    [InlineData("[=[text ]] ) ]==] \"x\" ]=]", "[=[text ]] ) ]==] \"x\" ]=]")]
    [InlineData("[===[Привет\n[=[not nested]=]\n]===]", "[===[Привет\n[=[not nested]=]\n]===]")]
    [InlineData("[=[unfinished ]==]", "[=[unfinished ]==]")]
    [InlineData("'quote \\' still string )'", "'quote \\' still string )'")]
    [InlineData("\"emoji 😀 escaped \\\" )\"", "\"emoji 😀 escaped \\\" )\"")]
    [InlineData("\"first\\\r\nsecond\"", "\"first\\\r\nsecond\"")]
    [InlineData("'first\\z \r\n\t second'", "'first\\z \r\n\t second'")]
    [InlineData("'unfinished\\", "'unfinished\\")]
    public void Parse_StringsHandleEscapesUnicodeAndLuaWhitespaceEscape_WithoutHighlightingContents(string source, string literal)
    {
        Assert.Equal(new[] { new LuaToken(0, literal.Length, LuaTokenKind.String) }, LuaSyntax.Parse(source));
    }

    [Fact]
    public void Parse_RawNewlineEndsBrokenQuotedString_AndResumesOnNextLine()
    {
        Assert.Equal(new LuaToken[] { new(0, 4, LuaTokenKind.String), new(5, 5, LuaTokenKind.Keyword) }, LuaSyntax.Parse("'bad\nlocal"));
    }

    [Theory]
    [InlineData(".5")]
    [InlineData("123.")]
    [InlineData("1.25e-4")]
    [InlineData("5E+12")]
    [InlineData("0xCAFE")]
    [InlineData("0X1.fp+3")]
    [InlineData("0x.8P-1")]
    public void Parse_LuaNumerals_IncludeFractionAndExponentAsOneToken(string source)
    {
        Assert.Equal(new[] { new LuaToken(0, source.Length, LuaTokenKind.Number) }, LuaSyntax.Parse(source));
    }

    [Fact]
    public void Parse_ConcatenationVarargsLabelsAndBitwiseOperators_AreNotAbsorbedIntoNumbers()
    {
        Assert.Equal(new LuaToken[]
        {
            new(0, 1, LuaTokenKind.Number), new(1, 2, LuaTokenKind.Operator), new(3, 1, LuaTokenKind.Number),
            new(5, 3, LuaTokenKind.Operator), new(9, 2, LuaTokenKind.Operator), new(12, 2, LuaTokenKind.Operator),
            new(15, 2, LuaTokenKind.Operator), new(18, 2, LuaTokenKind.Operator), new(21, 2, LuaTokenKind.Operator),
            new(24, 2, LuaTokenKind.Operator), new(27, 2, LuaTokenKind.Operator), new(30, 2, LuaTokenKind.Operator)
        }, LuaSyntax.Parse("1..2 ... :: // << >> ~= == <= >="));
    }

    [Fact]
    public void Parse_BracketsPairAcrossLinesWithDepth_IgnoringStringsAndComments()
    {
        const string source = "(\n[')'] -- }\n{})";
        Assert.Equal(new LuaToken[]
        {
            new(0, 1, LuaTokenKind.Bracket, 0, 15), new(2, 1, LuaTokenKind.Bracket, 1, 6), new(3, 3, LuaTokenKind.String),
            new(6, 1, LuaTokenKind.Bracket, 1, 2), new(8, 4, LuaTokenKind.Comment),
            new(13, 1, LuaTokenKind.Bracket, 1, 14), new(14, 1, LuaTokenKind.Bracket, 1, 13), new(15, 1, LuaTokenKind.Bracket, 0, 0)
        }, LuaSyntax.Parse(source));
    }

    [Theory]
    [InlineData("([)]")]
    [InlineData("({]}")]
    public void Parse_CrossedDelimiters_AreInvalidAndHaveNoPartners(string source)
    {
        Assert.Equal(new LuaToken[]
        {
            new(0, 1, LuaTokenKind.InvalidBracket), new(1, 1, LuaTokenKind.InvalidBracket, 1),
            new(2, 1, LuaTokenKind.InvalidBracket, 1), new(3, 1, LuaTokenKind.InvalidBracket)
        }, LuaSyntax.Parse(source));
    }

    [Fact]
    public void Parse_EditingClosingBracket_RecomputesPairsAndLeavesNoStalePartner()
    {
        Assert.Equal(new LuaToken[] { new(0, 1, LuaTokenKind.InvalidBracket), new(1, 1, LuaTokenKind.Identifier) }, LuaSyntax.Parse("(x"));
        Assert.Equal(new LuaToken[] { new(0, 1, LuaTokenKind.Bracket, 0, 2), new(1, 1, LuaTokenKind.Identifier), new(2, 1, LuaTokenKind.Bracket, 0, 0) }, LuaSyntax.Parse("(x)"));
        Assert.Equal(new LuaToken[] { new(0, 1, LuaTokenKind.InvalidBracket), new(1, 1, LuaTokenKind.Identifier), new(2, 1, LuaTokenKind.InvalidBracket) }, LuaSyntax.Parse("(x]"));
        Assert.Equal(new[] { new LuaToken(0, 1, LuaTokenKind.InvalidBracket) }, LuaSyntax.Parse(")"));
    }

    [Fact]
    public void Parse_DeepNestingUsesIterativePairs_AndReturnsReadOnlyOrderedSourceRanges()
    {
        const int count = 12000;
        string source = new string('(', count) + new string(')', count);
        var tokens = LuaSyntax.Parse(source);
        Assert.Equal(count * 2, tokens.Count);
        Assert.Equal(new LuaToken(0, 1, LuaTokenKind.Bracket, 0, count * 2 - 1), tokens[0]);
        Assert.Equal(new LuaToken(count - 1, 1, LuaTokenKind.Bracket, count - 1, count), tokens[count - 1]);
        Assert.Equal(new LuaToken(count, 1, LuaTokenKind.Bracket, count - 1, count - 1), tokens[count]);
        Assert.Equal(new LuaToken(count * 2 - 1, 1, LuaTokenKind.Bracket, 0, 0), tokens[^1]);
        Assert.All(tokens, token => Assert.Equal(source[token.Start] == '(' ? count * 2 - 1 - token.Start : count * 2 - token.End, token.Partner));
        Assert.Equal(Enumerable.Range(0, count * 2), tokens.Select(token => token.Start));
        Assert.Throws<NotSupportedException>(() => ((IList<LuaToken>)tokens)[0] = new(0, 1, LuaTokenKind.Operator));
        Assert.Equal(new string('(', count) + new string(')', count), source);
    }

    [Fact]
    public void Parse_EmptyWhitespaceAndMalformedLongOpener_RemainSourcePreserving()
    {
        Assert.Empty(LuaSyntax.Parse(string.Empty));
        Assert.Empty(LuaSyntax.Parse(" \t\r\n"));
        Assert.Equal(new LuaToken[]
        {
            new(0, 1, LuaTokenKind.InvalidBracket), new(1, 1, LuaTokenKind.Operator), new(2, 1, LuaTokenKind.Identifier)
        }, LuaSyntax.Parse("[=x"));
        Assert.Throws<ArgumentNullException>(() => LuaSyntax.Parse(null!));
    }
}
