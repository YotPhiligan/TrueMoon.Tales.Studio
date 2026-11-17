using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendWriteOptionsTests
{
    [Theory]
    [InlineData("--project", false, false, "read")]
    [InlineData("--inspect-project", false, true, "read")]
    [InlineData("--edit-project", true, false, "edit")]
    [InlineData("--migrate-project", true, true, "migrate")]
    [InlineData("--recover-project", true, true, "recover")]
    public void Launcher_EnablesWritesOnlyForExplicitCommands(string flag, bool writable, bool inspect, string action)
    {
        using var fixture = new BackendFixture();
        Assert.True(BackendOptions.TryParse([flag, fixture.Files.ProjectDirectory, "--config", fixture.ConfigPath], new(), out var options, out var child, out var isInspect));
        Assert.Equal(writable, options!.WriteEnabled); Assert.False(child);
        Assert.Equal(inspect, isInspect); Assert.Equal(action, options.Action);
        var args = new[] { "--backend", fixture.Files.ProjectDirectory, fixture.ConfigPath, options.Endpoint, options.SessionKey, "--writer" };
        Assert.False(BackendOptions.TryParse(args, new(), out _, out _, out _));
        Assert.True(BackendOptions.TryParse(args, new UnitLaunchSettings(1, 12345, "control"), out var managed, out child, out _));
        Assert.True(managed!.WriteEnabled); Assert.True(child);
    }
}
