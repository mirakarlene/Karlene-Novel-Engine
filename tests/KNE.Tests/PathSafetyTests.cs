using System.Diagnostics;
using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class PathSafetyTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("folder\\outside")]
    [InlineData("C:\\outside")]
    [InlineData("file:stream")]
    [InlineData("file.")]
    [InlineData(" file")]
    [InlineData("file ")]
    [InlineData("CON.txt")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("lpt9.txt")]
    public void ValidateFileName_UnsafeName_Rejects(string name) =>
        Assert.ThrowsAny<ArgumentException>(() => PathSafety.ValidateFileName(name));

    [Theory]
    [InlineData("Новелла с пробелами")]
    [InlineData("scene.v2.krl")]
    [InlineData("COM10.lua")]
    public void ValidateFileName_ValidName_Accepts(string name)
    {
        Assert.Null(Record.Exception(() => PathSafety.ValidateFileName(name)));
    }

    [Theory]
    [InlineData("../outside.lua")]
    [InlineData("../Новелла с пробелами_backup/scene.lua")]
    [InlineData(".")]
    public void ResolveRelative_OutsideOrRoot_Rejects(string relative)
    {
        using var fixture = new TempProject();
        Assert.Throws<ArgumentException>(() => PathSafety.ResolveRelative(fixture.ProjectPath, relative));
    }

    [Fact]
    public void ResolveRelative_NormalizedNestedPath_Accepts()
    {
        using var fixture = new TempProject();
        Assert.Equal(Path.Combine(fixture.ProjectPath, "game", "scenes", "main.krl"),
            PathSafety.ResolveRelative(fixture.ProjectPath + Path.DirectorySeparatorChar, "game/scenes/../scenes/main.krl"));
    }

    [Fact]
    public void ResolveRelative_AbsolutePath_Rejects()
    {
        using var fixture = new TempProject();
        Assert.Throws<ArgumentException>(() => PathSafety.ResolveRelative(fixture.ProjectPath, fixture.ProjectPath));
    }

    [Fact]
    public async Task LinkedDirectory_IsRejectedAndExcludedFromSnapshot()
    {
        using var fixture = new TempProject();
        string outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "keep");
        string link = Path.Combine(fixture.ProjectPath, "linked");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Throws<IOException>(() => PathSafety.ResolveRelative(fixture.ProjectPath, "linked/secret.txt"));
            var project = await fixture.LoadAsync();
            Assert.DoesNotContain(project.Files, f => f.FileName == "secret.txt");
            Assert.Single(project.Warnings);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "secret.txt")));
        }
        finally { Directory.Delete(link); }
    }
}
