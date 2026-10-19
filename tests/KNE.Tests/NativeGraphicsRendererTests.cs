using System.Windows;
using System.Numerics;
using Xunit;

namespace KNE.Tests;

/// <summary>These tests require real GPU drivers. Opt in with KNE_GPU_TEST=1; disabled runs report skips.</summary>
public sealed class NativeGraphicsRendererTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("directx")]
    [InlineData("Vulkan")]
    public void NativeRenderer_UnknownApiIsRejectedBeforeNativeDeviceCreation(string? api)
    {
        var error = Assert.Throws<ArgumentException>(() => NativeGraphicsRenderer.Create(api!, false));
        Assert.Equal("api", error.ParamName);
    }

    [GpuTheory]
    [InlineData("vulkan", 0)]
    [InlineData("vulkan", 2)]
    [InlineData("vulkan", 4)]
    [InlineData("opengl", 0)]
    [InlineData("opengl", 2)]
    [InlineData("opengl", 4)]
    public void NativeGpu_RenderReadback_KeepsBgraOriginPremultiplicationClippingAndRequestedSamples(string api, int samples)
    {
        using var renderer = NativeGraphicsRenderer.Create(api, false);
        // Four quadrants in one texture distinguish UV inversion from frame origin and BGRA/RGBA swaps.
        var quadrants = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                byte[] color = y < 4 ? x < 4 ? [0, 0, 255, 255] : [0, 255, 0, 255] : x < 4 ? [255, 0, 0, 255] : [0, 255, 255, 255];
                color.CopyTo(quadrants, (y * 8 + x) * 4);
            }
        var blue = Solid(2, 255, 0, 0, 255);
        var halfRed = Solid(3, 0, 0, 128, 128);
        var green = Solid(4, 0, 255, 0, 255);
        GraphicsSprite[] sprites =
        [
            new(new GraphicsTexture(1, 8, 8, quadrants), new Rect(0, 0, 32, 32)),
            new(blue, new Rect(32, 0, 16, 16)),
            new(halfRed, new Rect(32, 0, 16, 16)),
            new(halfRed, new Rect(32, 16, 16, 16)),
            new(green, new Rect(-8, 40, 16, 16))
        ];
        using var image = renderer.Render(new GraphicsFrame(64, 48, sprites), samples);
        Assert.Equal(api, image.Backend);
        Assert.Equal(samples, image.MsaaSamples);
        Assert.Equal(64, image.Width); Assert.Equal(48, image.Height);
        Assert.True(image.Pixels.Length >= 64 * 48 * 4);
        Assert.Equal(5, image.DrawCalls);
        Assert.True(image.TrackedGpuBytes >= 64 * 48 * 4);
        Assert.True(double.IsFinite(image.Milliseconds) && image.Milliseconds >= 0);
        Pixel(image, 4, 4, 0, 0, 255, 255);
        Pixel(image, 28, 4, 0, 255, 0, 255);
        Pixel(image, 4, 28, 255, 0, 0, 255);
        Pixel(image, 28, 28, 0, 255, 255, 255);
        Pixel(image, 40, 8, 127, 0, 128, 255);
        Pixel(image, 40, 24, 0, 0, 128, 128);
        Pixel(image, 2, 44, 0, 255, 0, 255);
        Pixel(image, 12, 44, 0, 0, 0, 0);
        Pixel(image, 63, 47, 0, 0, 0, 0);
    }

    [GpuTheory]
    [InlineData("vulkan")]
    [InlineData("opengl")]
    public void NativeGpu_MsaaActuallyResolvesPartialTriangleCoverage_AndChangingSamplesRecreatesTarget(string api)
    {
        using var renderer = NativeGraphicsRenderer.Create(api, false);
        // Non-axis-aligned triangle: solid interior, transparent exterior, and partially covered edge samples.
        GraphicsVertex[] triangle =
        [
            new(new Vector2(2.2f, 2.3f), Vector2.Zero, Vector4.One),
            new(new Vector2(27.1f, 6.7f), Vector2.Zero, Vector4.One),
            new(new Vector2(6.4f, 28.2f), Vector2.Zero, Vector4.One)
        ];
        foreach (int samples in new[] { 0, 2, 4, 0 })
        {
            using var image = renderer.Render(new GraphicsFrame(32, 32, [], triangle), samples);
            Assert.Equal(api, image.Backend); Assert.Equal(samples, image.MsaaSamples); Assert.Equal(1, image.DrawCalls);
            Pixel(image, 8, 8, 255, 255, 255, 255); Pixel(image, 31, 31, 0, 0, 0, 0);
            var alpha = Enumerable.Range(0, 32 * 32).Select(index => image.Pixels[index * 4 + 3]).ToArray();
            if (samples == 0) Assert.All(alpha, value => Assert.True(value is 0 or 255));
            else Assert.Contains(alpha, value => value > 0 && value < 255);
            for (int index = 0; index < 32 * 32; index++)
            {
                int offset = index * 4;
                Assert.Equal(image.Pixels[offset + 3], image.Pixels[offset]);
                Assert.Equal(image.Pixels[offset + 3], image.Pixels[offset + 1]);
                Assert.Equal(image.Pixels[offset + 3], image.Pixels[offset + 2]);
            }
        }
    }

    [GpuTheory]
    [InlineData("vulkan")]
    [InlineData("opengl")]
    public void NativeGpu_ResizeAndEmptyFrame_ClearTargetsAndReportSubmittedDrawCalls(string api)
    {
        using var renderer = NativeGraphicsRenderer.Create(api, true);
        var red = Solid(100, 0, 0, 255, 255);
        using (var first = renderer.Render(new GraphicsFrame(17, 11, [new(red, new Rect(0, 0, 17, 11))]), 0))
        {
            Assert.Equal(1, first.DrawCalls); Pixel(first, 16, 10, 0, 0, 255, 255);
        }
        // Odd widths catch implementations that copy staging memory without respecting row pitch.
        foreach (var (width, height) in new[] { (35, 19), (3, 7), (1, 1), (8192, 1), (17, 11) })
        {
            using var empty = renderer.Render(new GraphicsFrame(width, height, []), 0);
            Assert.Equal(width, empty.Width); Assert.Equal(height, empty.Height);
            Assert.Equal(api, empty.Backend); Assert.Equal(0, empty.DrawCalls);
            Assert.All(empty.Pixels.Take(width * height * 4), value => Assert.Equal((byte)0, value));
            using var filled = renderer.Render(new GraphicsFrame(width, height, [new(red, new Rect(0, 0, width, height))]), 0);
            Assert.Equal(1, filled.DrawCalls);
            Pixel(filled, 0, 0, 0, 0, 255, 255); Pixel(filled, width - 1, height - 1, 0, 0, 255, 255);
        }
    }

    [GpuTheory]
    [InlineData("vulkan")]
    [InlineData("opengl")]
    public void NativeGpu_TextureIdentityOpacityAndRepeatedDeviceLifetime_DoNotReuseStaleResources(string api)
    {
        for (int lifetime = 0; lifetime < 3; lifetime++)
        {
            using var renderer = NativeGraphicsRenderer.Create(api, false);
            var red = Solid(200, 0, 0, 255, 255);
            var green = Solid(201, 0, 255, 0, 255);
            foreach (var (texture, b, g, r) in new[] { (red, 0, 0, 255), (red, 0, 0, 255), (green, 0, 255, 0), (red, 0, 0, 255) })
            {
                using var image = renderer.Render(new GraphicsFrame(9, 5, [new(texture, new Rect(0, 0, 9, 5))]), 0);
                Pixel(image, 4, 2, b, g, r, 255);
                Assert.Equal(1, image.DrawCalls);
            }
            using (var faded = renderer.Render(new GraphicsFrame(9, 5, [new(red, new Rect(0, 0, 9, 5), .5f)]), 0))
                Pixel(faded, 4, 2, 0, 0, 128, 128);
            renderer.Dispose(); renderer.Dispose();
            Assert.Throws<ObjectDisposedException>(() => renderer.Render(new GraphicsFrame(9, 5, []), 0));
        }
    }

    [GpuTheory]
    [InlineData("vulkan")]
    [InlineData("opengl")]
    public void NativeGpu_InvalidFramesAndUnsupportedSamples_FailExplicitly(string api)
    {
        using var renderer = NativeGraphicsRenderer.Create(api, false);
        foreach (var (width, height) in new[] { (0, 10), (10, 0), (-1, 10), (8193, 1), (1, 8193), (8192, 8192) })
            Assert.ThrowsAny<ArgumentException>(() => renderer.Render(new GraphicsFrame(width, height, []), 0));
        foreach (int samples in new[] { -1, 1, 3, 8 })
            Assert.ThrowsAny<ArgumentException>(() => renderer.Render(new GraphicsFrame(4, 4, []), samples));
        var shortTexture = new GraphicsTexture(400, 2, 2, new byte[15]);
        Assert.ThrowsAny<ArgumentException>(() => renderer.Render(new GraphicsFrame(4, 4, [new(shortTexture, new Rect(0, 0, 4, 4))]), 0));
        var validTexture = Solid(402, 0, 255, 0, 255);
        foreach (float opacity in new[] { -.01f, 1.01f, float.NaN, float.PositiveInfinity })
            Assert.ThrowsAny<ArgumentException>(() => renderer.Render(new GraphicsFrame(4, 4, [new(validTexture, new Rect(0, 0, 4, 4), opacity)]), 0));
        Assert.ThrowsAny<ArgumentException>(() => renderer.Render(new GraphicsFrame(4, 4, [new(validTexture, Rect.Empty)]), 0));
        Assert.ThrowsAny<ArgumentException>(() => renderer.Render(new GraphicsFrame(4, 4, Enumerable.Repeat(new GraphicsSprite(validTexture, new Rect(0, 0, 4, 4)), 501).ToArray()), 0));
        Assert.ThrowsAny<ArgumentException>(() => renderer.Render(new GraphicsFrame(4, 4, [], [new(Vector2.Zero, Vector2.Zero, Vector4.One)]), 0));
        // A rejected frame must not poison command-list/device state.
        using var recovery = renderer.Render(new GraphicsFrame(4, 4, [new(Solid(401, 0, 255, 0, 255), new Rect(0, 0, 4, 4))]), 0);
        Pixel(recovery, 2, 2, 0, 255, 0, 255);
    }

    private static GraphicsTexture Solid(long id, byte blue, byte green, byte red, byte alpha) => new(id, 1, 1, [blue, green, red, alpha]);

    private static void Pixel(GraphicsPixels image, int x, int y, int blue, int green, int red, int alpha)
    {
        int offset = (y * image.Width + x) * 4;
        int[] expected = [blue, green, red, alpha];
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange((int)image.Pixels[offset + channel], Math.Max(0, expected[channel] - 2), Math.Min(255, expected[channel] + 2));
    }
}

public sealed class GpuTheoryAttribute : TheoryAttribute
{
    public GpuTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("KNE_GPU_TEST") != "1")
            Skip = "Real GPU verification is opt-in: set KNE_GPU_TEST=1 (both Vulkan and OpenGL drivers required).";
    }
}
