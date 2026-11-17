using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendHttpOptionsTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("--with-ui", true)]
    [InlineData("--http-port 0", true)]
    [InlineData("--http-port 65535 --with-ui", true)]
    [InlineData("--http-port -1", false)]
    [InlineData("--http-port 65536", false)]
    [InlineData("--http-port", false)]
    [InlineData("--http-port x", false)]
    [InlineData("--with-ui --with-ui", false)]
    [InlineData("--http-port 0 --http-port 1", false)]
    [InlineData("--bind 0.0.0.0", false)]
    public void ServeMode_IsExplicitAndBounded_AndDoesNotChangeManagedChildProtocol(string extra, bool valid)
    {
        using var fixture = new BackendFixture();
        var args = new[] { "--serve-project", fixture.Files.ProjectDirectory, "--config", fixture.ConfigPath }
            .Concat(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        Assert.Equal(valid, BackendOptions.TryParse(args, new(), out var options, out var child, out var inspect));
        if (!valid) return;
        Assert.Equal("serve", options!.Action); Assert.True(options.WriteEnabled); Assert.False(child); Assert.False(inspect);
        Assert.Equal(extra.Contains("65535") ? 65535 : 0, options.HttpPort); Assert.Equal(extra.Contains("--with-ui"), options.HttpWithUi);
        Assert.False(BackendOptions.TryParse(args, new UnitLaunchSettings(1, 12345, "control"), out _, out _, out _));
    }
}
