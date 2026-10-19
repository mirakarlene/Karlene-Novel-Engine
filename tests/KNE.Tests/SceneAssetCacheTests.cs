using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class SceneAssetCacheTests
{
    [Fact]
    public async Task Preload_CurrentAndDirectNextSceneImagesAreFrozenCachedButTransitiveThirdSceneIsNotVisited()
    {
        using var fixture = new TempProject();
        string current = Image(fixture, "images/current.png", 2, 3, 255, 0, 0);
        string sprite = Image(fixture, "images/sprite.png", 3, 3, 0, 255, 0);
        string next = Image(fixture, "images/next.png", 3, 4, 0, 0, 255);
        string third = Image(fixture, "images/third.png", 7, 5, 255, 255, 0);
        string currentGraph = Graph(fixture, "current", [Asset("bg", ScriptNodeKind.Background, "images/current.png"), Asset("sprite", ScriptNodeKind.Character, "images/sprite.png"), Asset("duplicate", ScriptNodeKind.Background, "images/current.png"), Transition("next", "next")]);
        string nextGraph = Graph(fixture, "next", [Asset("next_bg", ScriptNodeKind.Background, "images/next.png"), Transition("third", "third")]);
        string thirdGraph = Graph(fixture, "third", [Asset("third_bg", ScriptNodeKind.Background, "images/third.png")]);
        var project = Project(fixture, currentGraph, nextGraph, thirdGraph); var cache = new SceneAssetCache();
        await cache.PreloadAsync(project, "current.knegraph", CancellationToken.None);
        Assert.Equal(new[] { current, sprite, next }.Order(), Keys(cache).Order());
        Assert.DoesNotContain(third, Keys(cache)); Assert.Equal((2 * 3 + 3 * 3 + 3 * 4) * 4, cache.CachedBytes);
        Assert.True(cache.Load(current).IsFrozen); Assert.True(cache.Load(sprite).IsFrozen); Assert.True(cache.Load(next).IsFrozen);
        var transitive = cache.Load(third); Assert.True(transitive.IsFrozen);
        Assert.Equal((2 * 3 + 3 * 3 + 3 * 4 + 7 * 5) * 4, cache.CachedBytes);
    }

    [Fact]
    public async Task Preload_MalformedGraphsCorruptMissingUnsupportedAndEscapingResourcesDoNotPreventValidImagesLoading()
    {
        using var fixture = new TempProject();
        string valid = Image(fixture, "images/good.png", 2, 2, 0, 255, 0);
        string truncated = Image(fixture, "images/truncated.png", 4, 4, 255, 0, 0);
        byte[] png = File.ReadAllBytes(truncated); File.WriteAllBytes(truncated, png[..24]);
        fixture.Write("images/not-image.png", "not an image");
        fixture.Write("images/document.txt", "not an image");
        string brokenGraph = fixture.Write("broken.knegraph", "{broken json");
        string graph = Graph(fixture, "current", [
            Asset("truncated", ScriptNodeKind.Background, "images/truncated.png"),
            Asset("garbage", ScriptNodeKind.Character, "images/not-image.png"),
            Asset("missing", ScriptNodeKind.Background, "images/missing.png"),
            Asset("unsupported", ScriptNodeKind.Background, "images/document.txt"),
            Asset("escape", ScriptNodeKind.Background, "../outside.png"),
            Asset("valid", ScriptNodeKind.Background, "images/good.png")
        ]);
        var cache = new SceneAssetCache();
        await cache.PreloadAsync(Project(fixture, brokenGraph, graph), "current.knegraph", CancellationToken.None);
        Assert.Equal(new[] { valid }, Keys(cache)); Assert.Equal(16, cache.CachedBytes);
        Assert.True(cache.Load(valid).IsFrozen);
    }

    [Fact]
    public async Task Preload_KrlUsesLiteralCurrentAndDirectTargetImagesIgnoringCommentsDynamicPathsAndTransitiveCallsWithoutExecutingCode()
    {
        using var fixture = new TempProject();
        string current = Image(fixture, "images/code-current.png", 2, 3, 255, 0, 0);
        string sprite = Image(fixture, "images/code-sprite.png", 3, 3, 0, 255, 0);
        string next = Image(fixture, "images/code-next.png", 3, 4, 0, 0, 255);
        string third = Image(fixture, "images/code-third.png", 7, 5, 255, 255, 0);
        string ignored = Image(fixture, "images/code-ignored.png", 9, 5, 255, 0, 255);
        string currentScript = fixture.Write("current.krl", """
            function current()
                state.preload_executed = true
                error("This code must never execute during speculative loading")
                background("images/code-current.png")
                show_character('images/code-sprite.png', "center")
                -- background("images/code-ignored.png")
                print("background('images/code-ignored.png')")
                background(dynamic_path)
                call(next_scene)
            end
            """);
        string nextScript = fixture.Write("next.krl", """
            function next_scene()
                background("images/code-next.png")
                call(third_scene)
            end
            """);
        string thirdScript = fixture.Write("third.krl", """
            function third_scene()
                background("images/code-third.png")
            end
            """);
        var project = Project(fixture, currentScript, nextScript, thirdScript); var cache = new SceneAssetCache();
        string state = project.Engine.InitialState.GetRawText(); byte[] source = File.ReadAllBytes(currentScript);
        await cache.PreloadAsync(project, "current.krl", CancellationToken.None);
        Assert.Equal(new[] { current, sprite, next }.Order(), Keys(cache).Order());
        Assert.DoesNotContain(third, Keys(cache)); Assert.DoesNotContain(ignored, Keys(cache));
        Assert.Equal((2 * 3 + 3 * 3 + 3 * 4) * 4, cache.CachedBytes);
        Assert.Equal(state, project.Engine.InitialState.GetRawText()); Assert.Equal(source, File.ReadAllBytes(currentScript));
    }

    [Fact]
    public async Task Preload_IsBoundedToTwentyFourImagesAndCurrentPlusThreeDirectTargets()
    {
        using var fixture = new TempProject(); var nodes = new List<ScriptNode>(); var expected = new List<string>();
        for (int index = 0; index < 30; index++)
        {
            string relative = $"images/bounded-{index}.png";
            string path = Image(fixture, relative, 1, 1, (byte)index, 0, 0);
            if (index < 24) expected.Add(path);
            nodes.Add(Asset("image_" + index, ScriptNodeKind.Background, relative));
        }
        string graph = Graph(fixture, "bounded", nodes); var cache = new SceneAssetCache();
        await cache.PreloadAsync(Project(fixture, graph), "bounded.knegraph", CancellationToken.None);
        Assert.Equal(expected.Order(), Keys(cache).Order()); Assert.Equal(24 * 4, cache.CachedBytes);

        var files = new List<string>(); var transitions = new List<ScriptNode>(); var targetAssets = new List<string>();
        for (int index = 0; index < 4; index++)
        {
            string target = "target_" + index; string relative = "images/" + target + ".png";
            targetAssets.Add(Image(fixture, relative, 2, 2, 0, (byte)(index + 1), 0));
            files.Add(Graph(fixture, target, [Asset("target_image", ScriptNodeKind.Background, relative)]));
            transitions.Add(Transition("transition_" + index, target));
        }
        string entry = Graph(fixture, "entry", transitions); files.Insert(0, entry); var targetCache = new SceneAssetCache();
        await targetCache.PreloadAsync(Project(fixture, files.ToArray()), "entry.knegraph", CancellationToken.None);
        Assert.Equal(targetAssets.Take(3).Order(), Keys(targetCache).Order()); Assert.DoesNotContain(targetAssets[3], Keys(targetCache));
    }

    [Fact]
    public async Task Preload_CancelledWorkDoesNotCacheAnythingOrExecuteCommandsAndNormalSpeculationDoesNotMutateProject()
    {
        using var fixture = new TempProject(); Image(fixture, "images/state.png", 2, 2, 0, 0, 255);
        string graph = Graph(fixture, "current", [
            new ScriptNode { Id = "mutate", Kind = ScriptNodeKind.SetVariable, Variable = "preload_executed", Value = "true", ValueKind = ScriptValueKind.Boolean },
            new ScriptNode { Id = "console", Kind = ScriptNodeKind.ConsoleWrite, Text = "This command must not run during preloading" },
            Asset("bg", ScriptNodeKind.Background, "images/state.png")
        ]);
        var project = Project(fixture, graph); string beforeState = project.Engine.InitialState.GetRawText();
        byte[] beforeGraph = File.ReadAllBytes(graph); string[] beforeFiles = Directory.GetFiles(fixture.ProjectPath, "*", SearchOption.AllDirectories).Order().ToArray();
        var cache = new SceneAssetCache(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.PreloadAsync(project, "current.knegraph", cancel.Token));
        Assert.Equal(0, cache.CachedBytes); Assert.Empty(Keys(cache));
        Assert.Equal(beforeState, project.Engine.InitialState.GetRawText()); Assert.Equal(beforeGraph, File.ReadAllBytes(graph));
        await cache.PreloadAsync(project, "current.knegraph", CancellationToken.None);
        Assert.Equal(16, cache.CachedBytes); Assert.Equal(beforeState, project.Engine.InitialState.GetRawText());
        Assert.Equal(beforeGraph, File.ReadAllBytes(graph));
        Assert.Equal(beforeFiles, Directory.GetFiles(fixture.ProjectPath, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task Load_FrozenImagesCanBeReusedAcrossThreadsAndInvalidateOnTimestampOrLengthChanges()
    {
        using var fixture = new TempProject(); string path = Image(fixture, "images/reuse.bmp", 2, 3, 255, 0, 0);
        DateTime timestamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(path, timestamp);
        var cache = new SceneAssetCache(); var first = await Task.Run(() => cache.Load(path));
        Assert.True(first.IsFrozen); Assert.Equal(2, first.PixelWidth); Assert.Equal(3, first.PixelHeight); Assert.Equal(24, cache.CachedBytes);
        Assert.Same(first, cache.Load(path)); Assert.Same(first, await Task.Run(() => cache.Load(path.ToUpperInvariant())));
        Pixel(first, 255, 0, 0);
        long length = new FileInfo(path).Length;
        Image(fixture, "images/reuse.bmp", 2, 3, 0, 0, 255); File.SetLastWriteTimeUtc(path, timestamp.AddMinutes(1));
        Assert.Equal(length, new FileInfo(path).Length);
        var changedTime = cache.Load(path); Assert.NotSame(first, changedTime); Pixel(changedTime, 0, 0, 255); Assert.True(changedTime.IsFrozen);
        Image(fixture, "images/reuse.bmp", 17, 11, 0, 255, 0); File.SetLastWriteTimeUtc(path, timestamp.AddMinutes(1));
        Assert.NotEqual(length, new FileInfo(path).Length);
        var changedLength = await Task.Run(() => cache.Load(path)); Assert.NotSame(changedTime, changedLength);
        Assert.Equal(17, changedLength.PixelWidth); Assert.Equal(11, changedLength.PixelHeight); Assert.Equal(17 * 11 * 4, cache.CachedBytes); Pixel(changedLength, 0, 255, 0);
    }

    [Fact]
    public void Load_EnforcesThirtyTwoImageLimitAndSixtyFourMebibyteBudgetIncludingExactBoundary()
    {
        using var fixture = new TempProject(); var countCache = new SceneAssetCache(); var small = new List<string>();
        for (int index = 0; index < 33; index++)
        {
            small.Add(Image(fixture, $"images/count-{index}.png", 1, 1, 0, 0, (byte)index));
            _ = countCache.Load(small[^1]);
        }
        Assert.Equal(32, Keys(countCache).Length); Assert.Equal(32 * 4, countCache.CachedBytes);
        Assert.DoesNotContain(small[0], Keys(countCache)); Assert.Contains(small[^1], Keys(countCache));
        var budgetCache = new SceneAssetCache(); var large = new List<string>();
        for (int index = 0; index < 4; index++)
        {
            large.Add(Image(fixture, $"images/budget-{index}.png", 2048, 2048, (byte)index, 0, 255));
            _ = budgetCache.Load(large[^1]);
        }
        Assert.Equal(64L * 1024 * 1024, budgetCache.CachedBytes); Assert.Equal(4, Keys(budgetCache).Length);
        _ = budgetCache.Load(small[0]);
        Assert.Equal(48L * 1024 * 1024 + 4, budgetCache.CachedBytes);
        Assert.DoesNotContain(large[0], Keys(budgetCache)); Assert.Contains(large[^1], Keys(budgetCache)); Assert.Contains(small[0], Keys(budgetCache));
    }

    private static ProjectInfo Project(TempProject fixture, params string[] graphs)
    {
        var project = new ProjectInfo { Path = fixture.ProjectPath, Configuration = new ProjectConfiguration(), Engine = new EngineSettings { InitialState = JsonSerializer.SerializeToElement(new { preload_executed = false, chapter = 1 }) } };
        foreach (string path in graphs) project.Files.Add(new ProjectFile { FilePath = path });
        return project;
    }

    private static ScriptNode Asset(string id, ScriptNodeKind kind, string path) => new() { Id = id, Kind = kind, Path = path };
    private static ScriptNode Transition(string id, string target) => new() { Id = id, Kind = ScriptNodeKind.CallScene, Path = target };
    private static string Graph(TempProject fixture, string name, IEnumerable<ScriptNode> nodes) => fixture.Write(name + ".knegraph", new VisualScript { FunctionName = name, Nodes = nodes.ToList() }.Serialize());
    private static string[] Keys(SceneAssetCache cache) => ((IDictionary)typeof(SceneAssetCache).GetField("images", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!).Keys.Cast<string>().ToArray();

    private static string Image(TempProject fixture, string relative, int width, int height, byte blue, byte green, byte red)
    {
        string path = PathSafety.ResolveRelative(fixture.ProjectPath, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4) { pixels[offset] = blue; pixels[offset + 1] = green; pixels[offset + 2] = red; pixels[offset + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze();
        BitmapEncoder encoder = Path.GetExtension(path) == ".bmp" ? new BmpBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(path); encoder.Save(output); return path;
    }

    private static void Pixel(BitmapSource image, byte blue, byte green, byte red)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; converted.CopyPixels(pixels, image.PixelWidth * 4, 0);
        Assert.Equal(new byte[] { blue, green, red, 255 }, pixels[..4]);
    }
}
