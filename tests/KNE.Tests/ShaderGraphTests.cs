using System.IO;
using System.Windows;
using Veldrid;
using Veldrid.SPIRV;
using Xunit;

namespace KNE.Tests;

public sealed class ShaderGraphTests
{
    public static IEnumerable<object[]> Operations() => ShaderLibrary.Definitions.SelectMany(definition =>
        new[] { new object[] { definition.Kind, false }, new object[] { definition.Kind, true } });

    [Theory]
    [MemberData(nameof(Operations))]
    public void Catalogue_AllShaderOperationsCompileToSpirvAndHlsl(ScriptNodeKind kind, bool connectInputs)
    {
        using var fixture = new TempProject();
        var graph = Operation(kind);
        var operation = graph.Nodes.Single(node => node.Kind == kind);
        if (kind == ScriptNodeKind.ShaderContainer)
        {
            operation.Path = "child.kneshader";
            fixture.Write(operation.Path, ShaderGraphCompiler.Create().Serialize());
        }
        if (connectInputs)
        {
            var value = Node(ScriptNodeKind.ShaderColor, "value");
            graph.Nodes.Add(value);
            foreach (string input in ShaderLibrary.Get(kind).Inputs) operation.Shader!.Inputs[input] = new(value.Id);
        }

        var result = ShaderGraphCompiler.Compile(graph, fixture.ProjectPath);

        AssertPortable(result);
        Assert.Equal("scene", result.Target);
    }

    [Fact]
    public void Catalogue_DefaultsRoundTripWithDeclaredPortsAndCompleteShaderInventory()
    {
        var expected = Enum.GetValues<ScriptNodeKind>().Where(kind => kind.ToString().StartsWith("Shader", StringComparison.Ordinal)).ToArray();
        Assert.Equal(expected, ShaderLibrary.Definitions.Select(definition => definition.Kind));
        foreach (var definition in ShaderLibrary.Definitions)
        {
            Assert.True(ShaderLibrary.IsShader(definition.Kind));
            var node = BlockLibrary.Create(definition.Kind, "shaders");
            var copy = VisualScript.Parse(new VisualScript { IsShader = true, Nodes = [node] }.Serialize()).Nodes.Single();
            Assert.Equal(definition.Outputs, copy.Outputs().Select(port => port.Label));
            Assert.Equal("scene", copy.Shader!.Target);
            Assert.Empty(copy.Shader.Inputs);
            Assert.Equal(definition.Inputs.Length, definition.Inputs.Distinct().Count());
            foreach (var parameter in ShaderLibrary.Parameters(definition.Kind))
            {
                Assert.InRange(parameter.Default, parameter.Min, parameter.Max);
                Assert.True(double.IsFinite(parameter.Default));
            }
        }
        Assert.Equal(new[] { "R", "G", "B", "A" }, ShaderLibrary.Get(ScriptNodeKind.ShaderSplit).Outputs);
        Assert.Empty(ShaderLibrary.Get(ScriptNodeKind.ShaderOutput).Outputs);
        Assert.False(ShaderLibrary.IsShader(ScriptNodeKind.Dialogue));
    }

    [Fact]
    public void Create_ProducesIndependentConnectedTextureOutputWithoutLuaEntryPoint()
    {
        using var fixture = new TempProject();
        var graph = ShaderGraphCompiler.Create();
        Assert.True(graph.IsShader);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal(new ShaderConnection("texture"), graph.Nodes.Single(node => node.Kind == ScriptNodeKind.ShaderOutput).Shader!.Inputs["Color"]);
        Assert.False(ShaderGraphCompiler.Compile(graph, fixture.ProjectPath).Animated);
        Assert.Throws<InvalidDataException>(() => graph.Compile());
        graph.Nodes[0].Shader!.Target = "background";
        Assert.Equal("scene", ShaderGraphCompiler.Create().Nodes[0].Shader!.Target);
        Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(VisualScript.Create(), fixture.ProjectPath));
    }

    [Theory]
    [InlineData("missing-output")]
    [InlineData("duplicate-output")]
    [InlineData("missing-node")]
    [InlineData("invalid-output")]
    [InlineData("output-as-source")]
    [InlineData("cycle")]
    [InlineData("disconnected-cycle")]
    [InlineData("duplicate-id")]
    [InlineData("gameplay-node")]
    public void Compile_RejectsMalformedGraph(string fault)
    {
        using var fixture = new TempProject();
        var graph = ShaderGraphCompiler.Create();
        var output = graph.Nodes[1];
        switch (fault)
        {
            case "missing-output": graph.Nodes.Remove(output); break;
            case "duplicate-output": graph.Nodes.Add(Node(ScriptNodeKind.ShaderOutput, "second")); break;
            case "missing-node": output.Shader!.Inputs["Color"] = new("absent"); break;
            case "invalid-output": output.Shader!.Inputs["Color"] = new("texture", 1); break;
            case "output-as-source": graph.Nodes[0].Shader!.Inputs["UV"] = new("output"); break;
            case "cycle": graph.Nodes[0].Shader!.Inputs["UV"] = new("texture"); break;
            case "disconnected-cycle":
                var a = Node(ScriptNodeKind.ShaderInvert, "a");
                var b = Node(ScriptNodeKind.ShaderInvert, "b");
                a.Shader!.Inputs["Color"] = new("b"); b.Shader!.Inputs["Color"] = new("a");
                graph.Nodes.AddRange([a, b]); break;
            case "duplicate-id": graph.Nodes[0].Id = "output"; break;
            case "gameplay-node": graph.Nodes.Add(BlockLibrary.Create(ScriptNodeKind.Dialogue)); break;
        }
        Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
    }

    [Theory]
    [InlineData("unknown-input")]
    [InlineData("negative-output")]
    [InlineData("unknown-value")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("below-range")]
    [InlineData("above-range")]
    [InlineData("invalid-target")]
    [InlineData("missing-settings")]
    [InlineData("equal-slider-bounds")]
    [InlineData("reversed-slider-bounds")]
    public void Compile_RejectsInvalidSettings(string fault)
    {
        using var fixture = new TempProject();
        var graph = Operation(ScriptNodeKind.ShaderFloat);
        var node = graph.Nodes[0];
        switch (fault)
        {
            case "unknown-input": node.Shader!.Inputs["Typo"] = new("output"); break;
            case "negative-output": graph.Nodes[1].Shader!.Inputs["Color"] = new(node.Id, -1); break;
            case "unknown-value": node.Shader!.Values["Typo"] = 1; break;
            case "nan": node.Shader!.Values["x"] = double.NaN; break;
            case "infinity": node.Shader!.Values["x"] = double.PositiveInfinity; break;
            case "below-range": node.Shader!.Values["x"] = -10001; break;
            case "above-range": node.Shader!.Values["x"] = 10001; break;
            case "invalid-target": node.Shader!.Target = "other"; break;
            case "missing-settings": node.Shader = null; break;
            case "equal-slider-bounds": node.Shader!.Values["min"] = 1; node.Shader.Values["max"] = 1; break;
            case "reversed-slider-bounds": node.Shader!.Values["min"] = 2; node.Shader.Values["max"] = 1; break;
        }
        Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
    }

    [Theory]
    [InlineData("scene", "")]
    [InlineData("background", "")]
    [InlineData("sprite", "")]
    [InlineData("ui", "")]
    [InlineData("screen", "inventory")]
    public void Compile_TextureTargetAndNamedScreenArePreserved(string target, string screen)
    {
        using var fixture = new TempProject();
        var graph = ShaderGraphCompiler.Create();
        graph.Nodes[0].Shader!.Target = target; graph.Nodes[0].Shader!.Screen = screen;
        var compiled = ShaderGraphCompiler.Compile(graph, fixture.ProjectPath);
        Assert.Equal(target, compiled.Target); Assert.Equal(screen, compiled.Screen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compile_RejectsConflictingTargetsOrUnnamedScreen(bool conflict)
    {
        using var fixture = new TempProject();
        var graph = ShaderGraphCompiler.Create();
        graph.Nodes[0].Shader!.Target = "screen";
        if (conflict)
        {
            graph.Nodes[0].Shader!.Screen = "inventory";
            graph.Nodes.Add(Node(ScriptNodeKind.ShaderTexture, "other"));
        }
        Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
    }

    [Theory]
    [InlineData(ScriptNodeKind.ShaderTime, true)]
    [InlineData(ScriptNodeKind.ShaderFlipbook, true)]
    [InlineData(ScriptNodeKind.ShaderScanlines, true)]
    [InlineData(ScriptNodeKind.ShaderUv, false)]
    public void Compile_TimeDependentOperationsDeclareAnimation(ScriptNodeKind kind, bool animated)
    {
        using var fixture = new TempProject();
        Assert.Equal(animated, ShaderGraphCompiler.Compile(Operation(kind), fixture.ProjectPath).Animated);
    }

    [Fact]
    public void Compile_FlipbookWithConstantTimeIsStaticAndSplitOutputsCompileIndependently()
    {
        using var fixture = new TempProject();
        var graph = Operation(ScriptNodeKind.ShaderFlipbook);
        graph.Nodes.Add(Node(ScriptNodeKind.ShaderFloat, "clock"));
        graph.Nodes[0].Shader!.Inputs["Time"] = new("clock");
        Assert.False(ShaderGraphCompiler.Compile(graph, fixture.ProjectPath).Animated);
        var split = Node(ScriptNodeKind.ShaderSplit, "split");
        var combine = Node(ScriptNodeKind.ShaderCombine, "combine");
        foreach (var (input, index) in new[] { ("R", 3), ("G", 2), ("B", 1), ("A", 0) }) combine.Shader!.Inputs[input] = new("split", index);
        graph.Nodes = [split, combine, Node(ScriptNodeKind.ShaderOutput, "output")];
        graph.Nodes[2].Shader!.Inputs["Color"] = new("combine");
        AssertPortable(ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
    }

    [Theory]
    [InlineData(ScriptNodeKind.ShaderFloat)]
    [InlineData(ScriptNodeKind.ShaderVector)]
    [InlineData(ScriptNodeKind.ShaderColor)]
    public void Compile_ExposedOverridesUseStableIdentityNameAndInclusiveBounds(ScriptNodeKind kind)
    {
        using var fixture = new TempProject();
        var graph = Operation(kind);
        graph.Nodes[0].Shader!.Name = "Палитра 🌸";
        string sourcePath = Path.Combine(fixture.ProjectPath, "shaders", "effect.kneshader");
        var defaults = ShaderGraphCompiler.Compile(graph, fixture.ProjectPath, sourcePath);
        var original = Assert.Single(defaults.Parameters);
        Assert.Equal("shaders/effect.kneshader::operation", original.Id);
        Assert.Equal("Палитра 🌸", original.Name);
        Assert.Equal(kind, original.Kind);
        Assert.Equal(kind == ScriptNodeKind.ShaderFloat ? 1 : 4, original.Values.Length);
        double[] expectedValues = kind switch
        {
            ScriptNodeKind.ShaderFloat => [.5],
            ScriptNodeKind.ShaderVector => [1.0, 1.0, 0.0, 0.0],
            _ => [1.0, .5, 1.0, 1.0]
        };
        Assert.Equal(expectedValues, original.Values);
        Assert.Equal(kind == ScriptNodeKind.ShaderVector ? -10 : 0, original.Minimum);
        Assert.Equal(kind == ScriptNodeKind.ShaderVector ? 10 : 1, original.Maximum);
        foreach (double boundary in new[] { original.Minimum, original.Maximum })
        {
            double[] replacement = Enumerable.Repeat(boundary, original.Values.Length).ToArray();
            var changed = ShaderGraphCompiler.Compile(graph, fixture.ProjectPath, sourcePath, new Dictionary<string, double[]> { [original.Id] = replacement });
            Assert.Equal(replacement, Assert.Single(changed.Parameters).Values);
            AssertPortable(changed);
            Assert.NotEqual(defaults.Glsl, changed.Glsl);
        }
        Assert.Empty(graph.Nodes[0].Shader!.Values);
    }

    [Theory]
    [InlineData(ScriptNodeKind.ShaderFloat)]
    [InlineData(ScriptNodeKind.ShaderVector)]
    [InlineData(ScriptNodeKind.ShaderColor)]
    public void Compile_ExposedOverridesRejectWrongArityNonFiniteAndOutOfRange(ScriptNodeKind kind)
    {
        using var fixture = new TempProject();
        var graph = Operation(kind);
        var parameter = Assert.Single(ShaderGraphCompiler.Compile(graph, fixture.ProjectPath).Parameters);
        foreach (double[] invalid in new[]
        {
            Array.Empty<double>(), new double[parameter.Values.Length + 1],
            Enumerable.Repeat(double.NaN, parameter.Values.Length).ToArray(),
            Enumerable.Repeat(double.PositiveInfinity, parameter.Values.Length).ToArray(),
            Enumerable.Repeat(parameter.Minimum - .001, parameter.Values.Length).ToArray(),
            Enumerable.Repeat(parameter.Maximum + .001, parameter.Values.Length).ToArray()
        })
            Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath, exposed: new Dictionary<string, double[]> { [parameter.Id] = invalid }));
    }

    [Fact]
    public void Compile_ContainersAreReusablePureFunctionsWithDistinctParameterIdentities()
    {
        using var fixture = new TempProject();
        var child = Operation(ScriptNodeKind.ShaderColor);
        fixture.Write("shaders/first.kneshader", child.Serialize());
        fixture.Write("shaders/second.kneshader", child.Serialize());
        var graph = Operation(ScriptNodeKind.ShaderContainer);
        graph.Nodes[0].Path = "shaders/first.kneshader";
        var second = Node(ScriptNodeKind.ShaderContainer, "second"); second.Path = "shaders/second.kneshader";
        var reuse = Node(ScriptNodeKind.ShaderContainer, "reuse"); reuse.Path = "shaders/first.kneshader";
        graph.Nodes.AddRange([second, reuse]);
        var result = ShaderGraphCompiler.Compile(graph, fixture.ProjectPath);
        Assert.Equal(3, result.Parameters.Count);
        Assert.Contains(result.Parameters, parameter => parameter.Id == "shaders/first.kneshader::operation");
        Assert.Contains(result.Parameters, parameter => parameter.Id == "shaders/second.kneshader::operation");
        AssertPortable(result);

        child = ShaderGraphCompiler.Create(); child.Nodes[0].Shader!.Target = "screen"; child.Nodes[0].Shader!.Screen = "child-only";
        fixture.Write("shaders/first.kneshader", child.Serialize());
        var parent = ShaderGraphCompiler.Create();
        parent.Nodes[0].Shader!.Target = "background";
        parent.Nodes.Add(graph.Nodes[0]); parent.Nodes[1].Shader!.Inputs["Color"] = new("operation");
        Assert.Equal("background", ShaderGraphCompiler.Compile(parent, fixture.ProjectPath).Target);
    }

    [Theory]
    [InlineData("../outside.kneshader")]
    [InlineData("child.knegraph")]
    [InlineData("child.krl")]
    public void Compile_ContainerPathsStayInsideProjectAndRequireShaderExtension(string path)
    {
        using var fixture = new TempProject();
        var graph = Operation(ScriptNodeKind.ShaderContainer); graph.Nodes[0].Path = path;
        fixture.Write("child.knegraph", VisualScript.Create().Serialize());
        fixture.Write("child.krl", "function main() end");
        if (path.StartsWith("..", StringComparison.Ordinal))
            Assert.Throws<ArgumentException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
        else Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compile_RejectsDirectAndIndirectRecursiveContainers(bool indirect)
    {
        using var fixture = new TempProject();
        var graph = Operation(ScriptNodeKind.ShaderContainer);
        graph.Nodes[0].Path = indirect ? "child.kneshader" : "root.kneshader";
        string sourcePath = fixture.Write("root.kneshader", graph.Serialize());
        var child = Operation(ScriptNodeKind.ShaderContainer); child.Nodes[0].Path = "root.kneshader";
        fixture.Write("child.kneshader", child.Serialize());
        Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath, sourcePath));
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(13, false)]
    public void Compile_ContainerDepthAllowsTwelveAndRejectsThirteen(int depth, bool accepted)
    {
        using var fixture = new TempProject();
        fixture.Write($"level{depth}.kneshader", ShaderGraphCompiler.Create().Serialize());
        for (int index = depth - 1; index >= 1; index--)
        {
            var child = Operation(ScriptNodeKind.ShaderContainer); child.Nodes[0].Path = $"level{index + 1}.kneshader";
            fixture.Write($"level{index}.kneshader", child.Serialize());
        }
        var graph = Operation(ScriptNodeKind.ShaderContainer); graph.Nodes[0].Path = "level1.kneshader";
        if (accepted) AssertPortable(ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
        else Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
    }

    [Fact]
    public void Compile_ContainerRejectsWrongDocumentTypeAndOversize()
    {
        using var fixture = new TempProject();
        var graph = Operation(ScriptNodeKind.ShaderContainer); graph.Nodes[0].Path = "child.kneshader";
        fixture.Write("child.kneshader", VisualScript.Create().Serialize());
        Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
        fixture.Write("child.kneshader", new string(' ', 2 * 1024 * 1024 + 1));
        Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
    }

    [Fact]
    public void Compile_ChainedBlurAccountsForMultiplicativeTextureSampleCost()
    {
        using var fixture = new TempProject();
        var graph = Operation(ScriptNodeKind.ShaderGaussianBlur);
        var inner = Node(ScriptNodeKind.ShaderGaussianBlur, "inner");
        graph.Nodes.Add(inner); graph.Nodes[0].Shader!.Inputs["Color"] = new(inner.Id);
        // Each default blur uses 25 taps; nesting evaluates the inner blur 25 times.
        Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
    }

    [Theory]
    [InlineData(498, true)]
    [InlineData(499, false)]
    public void Compile_AggregateBlockLimitIncludesContainersAtFiveHundred(int childBlocks, bool accepted)
    {
        using var fixture = new TempProject();
        var child = ShaderGraphCompiler.Create();
        for (int index = child.Nodes.Count; index < childBlocks; index++) child.Nodes.Add(Node(ScriptNodeKind.ShaderUv, "extra" + index));
        fixture.Write("child.kneshader", child.Serialize());
        var graph = Operation(ScriptNodeKind.ShaderContainer); graph.Nodes[0].Path = "child.kneshader";
        if (accepted) AssertPortable(ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
        else Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
    }

    [Fact]
    public void Compile_NodeIdentityCannotInjectShaderSource()
    {
        using var fixture = new TempProject();
        var graph = Operation(ScriptNodeKind.ShaderColor);
        graph.Nodes[0].Id = "tint\n#error injected\r\n";
        graph.Nodes[1].Shader!.Inputs["Color"] = new(graph.Nodes[0].Id);
        AssertPortable(ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
    }

    [Theory]
    [InlineData("effect.kneshader", true, false)]
    [InlineData("effect.KNESHADER", true, false)]
    [InlineData("effect.knegraph", false, true)]
    [InlineData("effect.krl", false, false)]
    public void Document_ExtensionsKeepShadersSeparateFromGameplay(string path, bool shader, bool gameplay)
    {
        Assert.Equal(shader, ScriptDocument.IsShaderPath(path));
        Assert.Equal(gameplay, ScriptDocument.IsVisualPath(path));
        Assert.Equal(shader || gameplay, ScriptDocument.IsGraphPath(path));
    }

    [Fact]
    public async Task Document_CreateSaveReopenPreservesShaderPortsParametersLayoutAndUniqueMain()
    {
        using var fixture = new TempProject();
        string path = Path.Combine(fixture.ProjectPath, "effect.kneshader");
        var document = await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, path);
        Assert.True(document.IsVisual); Assert.True(document.Graph!.IsShader); Assert.False(document.IsDirty);
        var color = Node(ScriptNodeKind.ShaderColor, "tint");
        color.X = 421; color.Y = 183; color.Shader!.Name = "Тон 🌸"; color.Shader.Values["x"] = .25;
        document.Graph.Nodes.Add(color); document.Graph.Nodes[1].Shader!.Inputs["Color"] = new("tint");
        Assert.True(document.IsDirty);
        await document.SaveAsync(); Assert.False(document.IsDirty);
        var reopened = await ScriptDocument.OpenAsync(fixture.ProjectPath, path);
        var saved = reopened.Graph!.Nodes.Single(node => node.Id == "tint");
        Assert.Equal(421, saved.X); Assert.Equal(183, saved.Y); Assert.Equal("Тон 🌸", saved.Shader!.Name); Assert.Equal(.25, saved.Shader.Values["x"]);
        Assert.Equal(new ShaderConnection("tint"), reopened.Graph.Nodes.Single(node => node.Kind == ScriptNodeKind.ShaderOutput).Shader!.Inputs["Color"]);
        Assert.Equal("main", GameScripts.Load(await fixture.LoadAsync()).Main.Name);
        AssertPortable(ShaderGraphCompiler.Compile(reopened.Graph, fixture.ProjectPath, path));
        await Assert.ThrowsAsync<IOException>(() => ScriptDocument.CreateVisualAsync(fixture.ProjectPath, path));
        Assert.Empty(Directory.GetFiles(fixture.ProjectPath, "*.tmp"));
    }

    [Fact]
    public async Task Document_RejectsProjectEscapesAndMismatchedGraphFlags()
    {
        using var fixture = new TempProject();
        string outside = Path.Combine(fixture.Root, "escape.kneshader");
        await Assert.ThrowsAsync<ArgumentException>(() => ScriptDocument.CreateVisualAsync(fixture.ProjectPath, outside));
        await Assert.ThrowsAsync<ArgumentException>(() => ScriptDocument.OpenAsync(fixture.ProjectPath, outside));
        Assert.False(File.Exists(outside));
        string gameplay = fixture.Write("wrong.kneshader", VisualScript.Create().Serialize());
        string shader = fixture.Write("wrong.knegraph", ShaderGraphCompiler.Create().Serialize());
        await Assert.ThrowsAsync<InvalidDataException>(() => ScriptDocument.OpenAsync(fixture.ProjectPath, gameplay));
        await Assert.ThrowsAsync<InvalidDataException>(() => ScriptDocument.OpenAsync(fixture.ProjectPath, shader));
    }

    [GpuTheory]
    [InlineData("vulkan")]
    [InlineData("opengl")]
    public void NativeGpu_ShaderInvertsTwoByTwoTextureAndPreservesPremultipliedAlpha(string api)
    {
        using var fixture = new TempProject();
        using var renderer = NativeGraphicsRenderer.Create(api, false);
        var texture = new GraphicsTexture(3001, 2, 2, [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 64, 32, 16, 128]);
        var shader = ShaderGraphCompiler.Compile(Operation(ScriptNodeKind.ShaderInvert), fixture.ProjectPath);
        using var image = renderer.Render(new GraphicsFrame(2, 2, [new(texture, new Rect(0, 0, 2, 2))]), 0, shader);
        Pixel(image, 0, 0, 255, 255, 0, 255); Pixel(image, 1, 0, 255, 0, 255, 255);
        Pixel(image, 0, 1, 0, 255, 255, 255); Pixel(image, 1, 1, 64, 96, 112, 128);
        Assert.Equal((byte)128, image.Pixels[15]);
    }

    [GpuTheory]
    [InlineData("vulkan")]
    [InlineData("opengl")]
    public void NativeGpu_ShaderUvTimeAndPipelineResetChangeActualPixels(string api)
    {
        using var fixture = new TempProject();
        using var renderer = NativeGraphicsRenderer.Create(api, false);
        var texture = new GraphicsTexture(3002, 1, 1, [255, 255, 255, 255]);
        var frame = new GraphicsFrame(2, 2, [new(texture, new Rect(0, 0, 2, 2))]);
        var uv = ShaderGraphCompiler.Compile(Operation(ScriptNodeKind.ShaderUv), fixture.ProjectPath);
        using (var image = renderer.Render(frame, 0, uv))
        {
            Pixel(image, 0, 0, 0, 64, 64, 255); Pixel(image, 1, 1, 0, 191, 191, 255);
        }
        var graph = Operation(ScriptNodeKind.ShaderCombine);
        graph.Nodes.Add(Node(ScriptNodeKind.ShaderTime, "time")); graph.Nodes[0].Shader!.Inputs["R"] = new("time");
        var time = ShaderGraphCompiler.Compile(graph, fixture.ProjectPath);
        using (var first = renderer.Render(frame, 0, time, .25f)) Pixel(first, 0, 0, 0, 0, 64, 255);
        using (var later = renderer.Render(frame, 0, time, .75f)) Pixel(later, 0, 0, 0, 0, 191, 255);
        using var restored = renderer.Render(frame, 0);
        Pixel(restored, 0, 0, 255, 255, 255, 255);
    }

    [GpuTheory]
    [InlineData("vulkan")]
    [InlineData("opengl")]
    public void NativeGpu_GrayscaleAndPixelateApplyTheirNumericalOperations(string api)
    {
        using var fixture = new TempProject();
        using var renderer = NativeGraphicsRenderer.Create(api, false);
        var texture = new GraphicsTexture(3003, 2, 2, [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255]);
        var grayscale = ShaderGraphCompiler.Compile(Operation(ScriptNodeKind.ShaderGrayscale), fixture.ProjectPath);
        using (var image = renderer.Render(new GraphicsFrame(2, 2, [new(texture, new Rect(0, 0, 2, 2))]), 0, grayscale))
        {
            Pixel(image, 0, 0, 54, 54, 54, 255); Pixel(image, 1, 0, 182, 182, 182, 255);
            Pixel(image, 0, 1, 18, 18, 18, 255); Pixel(image, 1, 1, 255, 255, 255, 255);
        }
        var graph = Operation(ScriptNodeKind.ShaderPixelate); graph.Nodes[0].Shader!.Values["amount"] = 2;
        var pixelate = ShaderGraphCompiler.Compile(graph, fixture.ProjectPath);
        using var enlarged = renderer.Render(new GraphicsFrame(4, 4, [new(texture, new Rect(0, 0, 4, 4))]), 0, pixelate);
        // These interior pixels would blend adjacent texels without UV quantization.
        Pixel(enlarged, 1, 1, 0, 0, 255, 255); Pixel(enlarged, 2, 1, 0, 255, 0, 255);
        Pixel(enlarged, 1, 2, 255, 0, 0, 255); Pixel(enlarged, 2, 2, 255, 255, 255, 255);
    }

    private static VisualScript Operation(ScriptNodeKind kind)
    {
        if (kind == ScriptNodeKind.ShaderOutput) return ShaderGraphCompiler.Create();
        var operation = Node(kind, "operation");
        var output = Node(ScriptNodeKind.ShaderOutput, "output"); output.Shader!.Inputs["Color"] = new(operation.Id);
        return new VisualScript { IsShader = true, FunctionName = "shader", Nodes = [operation, output] };
    }

    private static ScriptNode Node(ScriptNodeKind kind, string id)
    {
        var node = BlockLibrary.Create(kind); node.Id = id; return node;
    }

    private static void AssertPortable(CompiledShaderGraph shader)
    {
        var binary = SpirvCompilation.CompileGlslToSpirv(shader.Glsl, "shader.frag", ShaderStages.Fragment, GlslCompileOptions.Default).SpirvBytes;
        Assert.True(binary.Length > 20);
        Assert.Equal(0x07230203u, BitConverter.ToUInt32(binary));
        Assert.Contains("SV_Target", shader.ToHlsl());
    }

    private static void Pixel(GraphicsPixels image, int x, int y, int blue, int green, int red, int alpha)
    {
        int offset = (y * image.Width + x) * 4;
        int[] expected = [blue, green, red, alpha];
        for (int channel = 0; channel < 4; channel++) Assert.InRange((int)image.Pixels[offset + channel], Math.Max(0, expected[channel] - 2), Math.Min(255, expected[channel] + 2));
    }
}
