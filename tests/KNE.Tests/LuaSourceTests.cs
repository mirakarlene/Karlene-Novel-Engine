using Xunit;

namespace KNE.Tests;

public sealed class LuaSourceTests
{
    [Fact]
    public void Functions_IgnoreCommentsStringsLocalsAndMethods_AndKeepGlobalLocations()
    {
        string source = """
            -- function main() end
            --[=[ function main() end ]=]
            text = "function main() goto(main)"
            long = [==[function main() end]==]
            do local function main() end end
            do local main = function() end end
            function object.main() end
            function object:main() end
            function main() end
            main = function() end
            function _G.main() end
            """;
        Assert.Equal(new[] { new LuaFunctionDeclaration("main", 9), new("main", 10), new("main", 11) }, LuaSource.Functions(source));
    }

    [Fact]
    public void Prepare_RewritesOnlyGameplayGoto_WithWhitespaceCommentsAndNativeLabels()
    {
        string source = """
            -- goto(fake)
            text = "goto(fake)\""
            long = [=[goto(fake)]=]
            goto -- comment
                (lmao)
            goto again
            ::again::
            goto('lmao')
            """;
        Assert.Equal("""
            -- goto(fake)
            text = "goto(fake)\""
            long = [=[goto(fake)]=]
            goto_scene -- comment
                (lmao)
            goto again
            ::again::
            goto_scene('lmao')
            """, LuaSource.Prepare(source));
    }

    [Fact]
    public void Functions_RespectLocalScopesParametersAndTableFields()
    {
        string source = """
            local table = { main = function() end }
            do local a, main; main = function() end; function main() end end
            function helper(main) main = function() end end
            if true then local main; function main() end else function main() end end
            local callbacks = { callback = function() _G.extra = function() end end }
            """;
        Assert.Equal(new[] { new LuaFunctionDeclaration("helper", 3), new("main", 4), new("extra", 5) }, LuaSource.Functions(source));
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("lmao_42", true)]
    [InlineData("_chapter", true)]
    [InlineData("", false)]
    [InlineData("42chapter", false)]
    [InlineData("goto", false)]
    [InlineData("game/scene.lua", false)]
    [InlineData("chapter); error('injected')", false)]
    public void Identifier_RejectsKeywordsPathsAndCode(string name, bool valid) => Assert.Equal(valid, LuaSource.IsIdentifier(name));
}
