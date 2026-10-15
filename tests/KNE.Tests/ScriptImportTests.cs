using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Xunit;

namespace KNE.Tests;

public sealed class ScriptImportTests
{
    [Fact]
    public async Task RenPy_LabelsDialogueMenuCallJumpAndReturn_RunAsIndependentNamespacedFunctions()
    {
        const string source = """
            label start:
                "Welcome"
                menu:
                    "Choose"
                    "A":
                        call helper
                    "B":
                        jump finish
                "After call"
                return
            label helper:
                e "Helper"
                return
            label finish:
                "Finish"
                return
            """;
        var result = ScriptImportConverter.FromRenPy(source, "my_import");
        Assert.Equal(source, result.Original);
        Assert.DoesNotContain("function main", result.Code);
        Assert.Contains("function my_import_start()", result.Code);
        Assert.Contains("call(my_import_helper)", result.Code);
        Assert.Contains("goto(my_import_finish)", result.Code);
        using var fixture = new TempProject();
        fixture.Write("game/scenes/imported.krl", result.Code);
        fixture.Write("game/scenes/main.krl", "function main() call(my_import_start) end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("Welcome", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.Equal("Choose", runtime.Story.Text); Assert.Equal(new[] { "A", "B" }, runtime.Story.Options);
        await runtime.ChooseAsync(1); Assert.Equal("Helper", runtime.Story.Text); Assert.Equal("e", runtime.Story.Speaker);
        await runtime.AdvanceAsync(); Assert.Equal("After call", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed);
        await using var alternate = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await alternate.StartAsync(); await alternate.AdvanceAsync(); await alternate.ChooseAsync(2);
        Assert.Equal("Finish", alternate.Story.Text);
        await alternate.AdvanceAsync(); Assert.True(alternate.Story.Completed);
    }

    [Fact]
    public async Task RenPy_UnsupportedConditionalAndPythonBodies_AreCommentsAndNeverExecuted()
    {
        const string source = """
            define e = Character("E")
            label start:
                if missing_condition:
                    "Must not run"
                    $ dangerous()
                python:
                    execute_anything()
                "Safe"
                return
            """;
        var result = ScriptImportConverter.FromRenPy(source);
        Assert.Contains(result.Warnings, w => w.Line == 3 && w.Message.Contains("if missing_condition"));
        Assert.Contains("-- [Ren'Py строка 4] \"Must not run\"", result.Code);
        Assert.DoesNotContain("d(nil, \"Must not run\")", result.Code);
        using var fixture = new TempProject();
        fixture.Write("game/scenes/imported.krl", result.Code);
        fixture.Write("game/scenes/main.krl", "function main() call(imported_start) end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("Safe", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed);
    }

    [Fact]
    public async Task RenPy_LiteralTextCannotInjectLua_AndEscapesRoundTrip()
    {
        const string text = "\"); error(\"INJECTED\"); --\nNext\tline";
        string source = "label start:\n    \"\\\"); error(\\\"INJECTED\\\"); --\\nNext\\tline\"\n    return";
        var result = ScriptImportConverter.FromRenPy(source);
        using var fixture = new TempProject();
        fixture.Write("game/scenes/imported.krl", result.Code);
        fixture.Write("game/scenes/main.krl", "function main() call(imported_start) end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal(text, runtime.Story.Text);
    }

    [Fact]
    public void RenPy_QuotedResourcesConvert_ButUnresolvedNamesAndDynamicTargetsAreExplicitWarnings()
    {
        var result = ScriptImportConverter.FromRenPy("label start:\n    scene \"game/backgrounds/bg.png\"\n    show 'game/characters/a.png'\n    play music \"game/audio/a.mp3\"\n    stop music\n    scene symbolic_bg\n    jump unknown\n    call expression attack()\n    \"[name] {b}hello{/b}\"\n    return");
        Assert.Contains("background(\"game/backgrounds/bg.png\")", result.Code);
        Assert.Contains("show_character(\"game/characters/a.png\", \"center\")", result.Code);
        Assert.Contains("play_music(\"game/audio/a.mp3\", true)", result.Code);
        Assert.Contains("stop_music()", result.Code);
        Assert.Contains(result.Warnings, w => w.Line == 6 && w.Message.Contains("symbolic_bg"));
        Assert.Contains(result.Warnings, w => w.Line == 7 && w.Message.Contains("unknown"));
        Assert.Contains(result.Warnings, w => w.Line == 8 && w.Message.Contains("attack()"));
        Assert.Contains(result.Warnings, w => w.Line == 9 && w.Message.Contains("буквально"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("label start:\n    pass\nlabel start:\n    pass")]
    [InlineData("label start(arg):\n    pass")]
    public void RenPy_EmptyUnsupportedOrDuplicateLabels_AreRejected(string source) =>
        Assert.Throws<InvalidDataException>(() => ScriptImportConverter.FromRenPy(source));

    [Theory]
    [InlineData("bad-prefix")]
    [InlineData("123name")]
    [InlineData("x); error('bad')")]
    public void Import_FunctionPrefixMustBeSafeIdentifier(string prefix) =>
        Assert.Throws<ArgumentException>(() => ScriptImportConverter.FromRenPy("label start:\n    pass", prefix));

    [Fact]
    public async Task Docx_RunsParagraphsAsLiteralNarration_IncludingTabsBreaksAndDeletedTextExclusion()
    {
        using var stream = Docx("""
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Глава</w:t></w:r></w:p>
            <w:p><w:r><w:t>Привет</w:t><w:tab/><w:t>мир</w:t><w:br/><w:t>&quot;); error('x') --</w:t></w:r><w:del><w:r><w:delText>Удалено</w:delText></w:r></w:del></w:p>
            <w:p/><w:p><w:r><w:drawing/></w:r></w:p></w:body></w:document>
            """);
        var result = ScriptImportConverter.FromDocx(stream, "word");
        Assert.Equal("Глава" + Environment.NewLine + "Привет\tмир\n\"); error('x') --" + Environment.NewLine + Environment.NewLine + Environment.NewLine, result.Original);
        Assert.Single(result.Warnings); Assert.Contains("изображения", result.Warnings[0].Message);
        Assert.DoesNotContain("Удалено", result.Code);
        using var fixture = new TempProject();
        fixture.Write("game/scenes/word.krl", result.Code);
        fixture.Write("game/scenes/main.krl", "function main() call(word_document) end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("Глава", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.Equal("Привет\tмир\n\"); error('x') --", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed);
    }

    [Fact]
    public void Docx_RejectsDtdMissingMainPartAndInvalidXml()
    {
        using var dtd = Docx("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/secret'>]><x>&e;</x>");
        Assert.Throws<XmlException>(() => ScriptImportConverter.FromDocx(dtd));
        using var invalid = Docx("<broken>");
        Assert.Throws<XmlException>(() => ScriptImportConverter.FromDocx(invalid));
        using var missing = new MemoryStream();
        using (var archive = new ZipArchive(missing, ZipArchiveMode.Create, true)) archive.CreateEntry("../document.xml");
        missing.Position = 0;
        Assert.Throws<InvalidDataException>(() => ScriptImportConverter.FromDocx(missing));
        using var oversized = Docx(new string('x', ScriptImportConverter.MaximumBytes + 1));
        Assert.Throws<InvalidDataException>(() => ScriptImportConverter.FromDocx(oversized));
    }

    [Fact]
    public async Task FromPath_ValidatesFormatSizeAndCancellation_WithoutChangingSource()
    {
        using var fixture = new TempProject();
        string path = fixture.Write("game/scenes/import.rpy", "label start:\n    \"Привет\"\n    return");
        byte[] before = await File.ReadAllBytesAsync(path);
        var result = await ScriptImportConverter.FromPathAsync(path, "chosen");
        Assert.Contains("function chosen_start()", result.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScriptImportConverter.FromPathAsync(path, cancellationToken: cancel.Token));
        string unsupported = fixture.Write("game/scenes/import.txt", "text");
        await Assert.ThrowsAsync<ArgumentException>(() => ScriptImportConverter.FromPathAsync(unsupported));
        string huge = fixture.Write("game/scenes/huge.rpy", new string('x', ScriptImportConverter.MaximumBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => ScriptImportConverter.FromPathAsync(huge));
    }

    [Fact]
    public void Docx_EmptyDocumentProducesAnEmptyRunnableFunction_AndDuplicateMainPartIsRejected()
    {
        using var empty = Docx("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>");
        var result = ScriptImportConverter.FromDocx(empty);
        Assert.Equal("", result.Original);
        Assert.Contains("function imported_document()" + Environment.NewLine + "end", result.Code);
        Assert.DoesNotContain("d(nil", result.Code);
        using var duplicate = new MemoryStream();
        using (var archive = new ZipArchive(duplicate, ZipArchiveMode.Create, true))
        {
            archive.CreateEntry("word/document.xml"); archive.CreateEntry("word/document.xml");
        }
        duplicate.Position = 0;
        Assert.Throws<InvalidDataException>(() => ScriptImportConverter.FromDocx(duplicate));
    }

    private static MemoryStream Docx(string xml)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false))) writer.Write(xml);
        stream.Position = 0;
        return stream;
    }
}
