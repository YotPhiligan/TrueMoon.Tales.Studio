using TrueMoon.Tales.Studio.Configuration;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Configuration;

public sealed class StudioOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("segment A")]
    public void Options_ParsePreservesProjectSegmentAndFullMediaPath(string? segment)
    {
        var executable = Path.GetRelativePath(Environment.CurrentDirectory, typeof(StudioOptions).Assembly.Location);
        string[] args = segment is null
            ? ["http://127.0.0.1:12345/", "project A", executable]
            : ["http://127.0.0.1:12345/", "project A", executable, segment];

        Assert.True(StudioOptions.TryParse(args, out var options));
        Assert.Equal(new Uri("http://127.0.0.1:12345/"), options.Endpoint);
        Assert.Equal("project A", options.ProjectId);
        Assert.Equal(Path.GetFullPath(executable), options.FfmpegPath);
        Assert.Equal(segment, options.SegmentId);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("https://127.0.0.1:12345/")]
    [InlineData("http://localhost:12345/")]
    [InlineData("http://127.0.0.2:12345/")]
    [InlineData("http://127.0.0.1:12345/api")]
    [InlineData("http://user@127.0.0.1:12345/")]
    [InlineData("http://127.0.0.1:12345/?x=1")]
    [InlineData("http://127.0.0.1:12345/#fragment")]
    public void Options_RejectsUnsupportedEndpoint(string endpoint)
    {
        Assert.False(StudioOptions.TryParse([endpoint, "qa", typeof(StudioOptions).Assembly.Location], out var options));
        Assert.Null(options);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Options_RejectsWrongArgumentCount(int count)
    {
        var args = new[] { "http://127.0.0.1:12345/", "qa", typeof(StudioOptions).Assembly.Location, "segment", "extra" }[..count];
        Assert.False(StudioOptions.TryParse(args, out var options));
        Assert.Null(options);
    }

    [Fact]
    public void Options_RejectsMissingDecoder()
    {
        Assert.False(StudioOptions.TryParse(["http://127.0.0.1:12345/", "qa", ""], out var options));
        Assert.Null(options);
    }

    [Fact]
    public async Task RunStudio_InvalidArgumentsReturnsTwoWithoutConfiguringApp()
    {
        var configured = false;
        var builder = App.Builder(_ => { }).Setup(_ => configured = true);

        Assert.Equal(2, await builder.RunStudioAsync([]));
        Assert.False(configured);
    }
}
