using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendOptionsTests
{
    [Theory]
    [InlineData("--project", false)]
    [InlineData("--inspect-project", true)]
    public void ParentRoles_SelectStartupProjectAndUniqueSession(string role, bool inspect)
    {
        using var fixture = new BackendFixture();
        var args = new[] { role, fixture.Files.ProjectDirectory, "--config", fixture.ConfigPath };
        Assert.True(BackendOptions.TryParse(args, new(), out var first, out var child, out var headless));
        Assert.False(child);
        Assert.Equal(inspect, headless);
        Assert.Equal(fixture.Files.ProjectDirectory, first!.ProjectDirectory);
        Assert.Equal(fixture.ConfigPath, first.ConfigPath);
        Assert.True(BackendOptions.TryParse(args, new(), out var second, out _, out _));
        Assert.NotEqual(first.Endpoint, second!.Endpoint);
        Assert.NotEqual(first.SessionKey, second.SessionKey);
    }

    [Fact]
    public void BackendRole_RequiresOwnedMithrilIdentityAndValidEndpoint()
    {
        using var fixture = new BackendFixture();
        var args = new[] { "--backend", fixture.Files.ProjectDirectory, fixture.ConfigPath, fixture.Options.Endpoint, fixture.Options.SessionKey };
        Assert.False(BackendOptions.TryParse(args, new(), out _, out _, out _));
        Assert.False(BackendOptions.TryParse(args, new(1), out _, out _, out _));
        Assert.False(BackendOptions.TryParse(args, new(2, 12345, "control"), out _, out _, out _));
        Assert.True(BackendOptions.TryParse(args, new(1, 12345, "control"), out var options, out var child, out var inspect));
        Assert.True(child);
        Assert.False(inspect);
        Assert.Equal(fixture.Options, options);
        args[3] = "../invalid-endpoint";
        Assert.False(BackendOptions.TryParse(args, new(1, 12345, "control"), out _, out _, out _));
    }

    [Fact]
    public void ParentRole_RejectsDisabledUnitsInheritedChildAndMalformedArguments()
    {
        using var fixture = new BackendFixture();
        var args = new[] { "--project", fixture.Files.ProjectDirectory, "--config", fixture.ConfigPath };
        Assert.False(BackendOptions.TryParse(args, new(UnitsDisabled: true), out _, out _, out _));
        Assert.False(BackendOptions.TryParse(args, new(1, 12345, "control"), out _, out _, out _));
        Assert.False(BackendOptions.TryParse(args[..3], new(), out _, out _, out _));
        args[2] = "--unknown";
        Assert.False(BackendOptions.TryParse(args, new(), out _, out _, out _));
    }

    [Fact]
    public void Access_DoesNotConvertExistingWriteGrantsIntoBackendWritePermission()
    {
        using var fixture = new BackendFixture();
        var options = BackendProjectAccess.Read(fixture.Options);
        Assert.Equal(fixture.Files.ProjectDirectory, options.ProjectDirectory);
        Assert.Empty(options.AllowedWriteRoots);
    }

    [Fact]
    public void Access_ReadsUtf8BomAndResolvesRootsRelativeToConfiguration()
    {
        using var fixture = new BackendFixture();
        File.WriteAllText(fixture.ConfigPath, "{\"version\":1,\"projects_root\":\".\",\"protected_roots\":[\"denied\"]}",
            new System.Text.UTF8Encoding(true));
        var options = BackendProjectAccess.Read(fixture.Options);
        Assert.Equal(fixture.Files.ProjectDirectory, options.ProjectDirectory);
        Assert.Contains(Path.Combine(fixture.Files.Root, "denied"), options.ProtectedRoots);
        Assert.Empty(options.AllowedWriteRoots);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("protected")]
    [InlineData("legacy")]
    [InlineData("prefix")]
    public void Access_RejectsOutsideProtectedLegacyAndPrefixSiblingRoots(string reason)
    {
        using var fixture = new BackendFixture();
        switch (reason)
        {
            case "outside": fixture.Configure(root: Path.Combine(fixture.Files.Root, "elsewhere")); break;
            case "protected": fixture.Configure(protectedRoots: [fixture.Files.ProjectDirectory]); break;
            case "legacy": fixture.Configure(legacyRoot: fixture.Files.ProjectDirectory); break;
            case "prefix": fixture.Configure(root: fixture.Files.ProjectDirectory[..^1]); break;
        }
        Assert.Throws<UnauthorizedAccessException>(() => BackendProjectAccess.Read(fixture.Options));
        Assert.Equal(fixture.Files.InitialBytes, File.ReadAllBytes(fixture.Files.ProjectPath));
    }
}
