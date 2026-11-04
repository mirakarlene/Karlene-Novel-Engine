using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace KNE.Tests;

public sealed class ScriptProtectionTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(65536)]
    public void AuthenticatedScriptEnvelopeRoundTripsEmptyAndBinarySourcesWithFreshNonces(int length)
    {
        using var fixture = new TempProject();
        byte[] key = RandomNumberGenerator.GetBytes(32), source = RandomNumberGenerator.GetBytes(length);
        byte[] encrypted = ScriptProtection.Encrypt(source, key), second = ScriptProtection.Encrypt(source, key);
        Assert.False(encrypted.AsSpan().SequenceEqual(second)); Assert.Equal(length + 36, encrypted.Length);
        string path = Path.Combine(fixture.ProjectPath, "protected.krl"); File.WriteAllBytes(path, encrypted);
        Assert.Equal(source, ScriptProtection.ReadBytes(path, key)); Assert.Equal(encrypted, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(8)] [InlineData(20)] [InlineData(36)]
    public void ModifiedNonceTagOrCiphertextIsRejectedBeforeReturningAnyScript(int index)
    {
        using var fixture = new TempProject(); byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] encrypted = ScriptProtection.Encrypt(Encoding.UTF8.GetBytes("function main() d(nil,'secret') end"), key); encrypted[index] ^= 1;
        string path = Path.Combine(fixture.ProjectPath, "protected.krl"); File.WriteAllBytes(path, encrypted);
        Assert.Throws<InvalidDataException>(() => ScriptProtection.ReadBytes(path, key)); Assert.Equal(encrypted, File.ReadAllBytes(path));
    }

    [Fact]
    public void WrongMissingAndTruncatedKeysOrEnvelopesFailWithoutChangingTheCiphertext()
    {
        using var fixture = new TempProject(); byte[] encrypted = ScriptProtection.Encrypt(Encoding.UTF8.GetBytes("secret source"), RandomNumberGenerator.GetBytes(32));
        string path = Path.Combine(fixture.ProjectPath, "protected.krl"); File.WriteAllBytes(path, encrypted);
        Assert.Throws<InvalidDataException>(() => ScriptProtection.ReadBytes(path, RandomNumberGenerator.GetBytes(32)));
        Assert.Throws<InvalidDataException>(() => ScriptProtection.ReadBytes(path)); Assert.Equal(encrypted, File.ReadAllBytes(path));
        File.WriteAllBytes(path, encrypted[..12]); Assert.Throws<InvalidDataException>(() => ScriptProtection.ReadBytes(path, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public async Task ReleaseStagingEncryptsOnlyScriptFormatsAndPreservesOriginalProjectAndAssets()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil,'release secret') end");
        fixture.Write("game/plugins/plain.lua", "state.plugin_secret='Lua secret'"); fixture.Write("game/plugins/plain.cs", "// C# secret");
        var graph = VisualScript.Create(); graph.FunctionName = "protected_graph"; fixture.Write("graph.knegraph", graph.Serialize());
        string asset = fixture.Write("assets/image.bin", "asset bytes"), notes = fixture.Write("README.txt", "public notes");
        var original = Directory.EnumerateFiles(fixture.ProjectPath, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        string stage = Path.Combine(fixture.Root, "release-stage"); Directory.CreateDirectory(stage);
        await GameBuildExporter.CopyProjectAsync(await fixture.LoadAsync(), stage, CancellationToken.None);
        byte[] key = RandomNumberGenerator.GetBytes(32); await ScriptProtection.EncryptProjectAsync(stage, key, CancellationToken.None);
        foreach (string source in original.Keys.Where(ScriptProtection.IsScript))
        {
            string relative = Path.GetRelativePath(fixture.ProjectPath, source), target = Path.Combine(stage, relative);
            Assert.True(File.Exists(target)); Assert.NotEqual(original[source], File.ReadAllBytes(target)); Assert.Equal(original[source], ScriptProtection.ReadBytes(target, key));
        }
        Assert.Equal(File.ReadAllBytes(asset), File.ReadAllBytes(Path.Combine(stage, Path.GetRelativePath(fixture.ProjectPath, asset))));
        Assert.Equal(File.ReadAllBytes(notes), File.ReadAllBytes(Path.Combine(stage, "README.txt")));
        foreach (var file in original) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.DoesNotContain("release secret", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(stage, "game/scenes/main.krl"))));
    }

    [Fact]
    public async Task CancelledStagingLeavesAllSourceFilesUnencrypted()
    {
        using var fixture = new TempProject(); var before = Directory.EnumerateFiles(fixture.ProjectPath, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScriptProtection.EncryptProjectAsync(fixture.ProjectPath, RandomNumberGenerator.GetBytes(32), cancellation.Token));
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PlainTextSourcesContinueToReadWithBomAndWithoutAnyEmbeddedKey(bool utf16)
    {
        using var fixture = new TempProject(); const string source = "function main() d(nil,'Привет') end";
        string path = Path.Combine(fixture.ProjectPath, "plain.krl"); File.WriteAllText(path, source, utf16 ? Encoding.Unicode : new UTF8Encoding(true));
        byte[] before = File.ReadAllBytes(path); Assert.Equal(source, ScriptProtection.ReadText(path)); Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(-1, false)] [InlineData(0, false)] [InlineData(1, false)]
    [InlineData(-1, true)] [InlineData(0, true)] [InlineData(1, true)]
    public void SourceLimitAppliesToPlaintextLengthRatherThanAuthenticatedEnvelopeOverhead(int offset, bool encrypted)
    {
        using var fixture = new TempProject();
        byte[] source = new byte[8 * 1024 * 1024 + offset], key = RandomNumberGenerator.GetBytes(32);
        string path = Path.Combine(fixture.ProjectPath, "boundary.krl"); byte[] contents = encrypted ? ScriptProtection.Encrypt(source, key) : source;
        File.WriteAllBytes(path, contents);
        if (offset > 0) Assert.Throws<InvalidDataException>(() => ScriptProtection.ReadBytes(path, key));
        else Assert.Equal(source, ScriptProtection.ReadBytes(path, key));
        Assert.Equal(contents, File.ReadAllBytes(path));
    }
}
