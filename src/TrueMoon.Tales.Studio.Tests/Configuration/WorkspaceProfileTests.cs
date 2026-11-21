using TrueMoon.Configuration;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Tests.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Configuration;

public sealed class WorkspaceProfileTests
{
    [Fact]
    public async Task AppAndStandaloneReadFlatV1WithDetachedSettings()
    {
        using var fixture = new PersistenceFixture();
        var config = Path.Combine(fixture.Root, "config.json");
        const string json = """
            {
              "version": 1,
              "legacy_root": "Old",
              "legacy_read_enabled": false,
              "projects_root": "Projects",
              "mounts": {"arts": "Art", "media-2": "Video"},
              "project_write_roots": ["Projects"],
              "protected_roots": ["Protect"],
              "path_aliases": [{"from":"Old/reference.png","to":"@arts/reference.png"}]
            }
            """;
        File.WriteAllText(config, json);

        await using var app = App.Build(context => context.UseConfiguration(WorkspaceProfile.Create(config)));
        var reader = app.Services.Resolve<IConfigurationReader>()!;
        var appSettings = reader.Current.GetOptions(WorkspaceProfile.Schema);
        var cliSettings = await WorkspaceProfile.ReadAsync(config);

        Assert.Equal(Path.Combine(fixture.Root, "Projects"), WorkspaceProfile.ResolvePath(appSettings.ProjectsRoot!, config));
        Assert.False(appSettings.LegacyReadEnabled);
        Assert.Equal(cliSettings.Mounts["media-2"], appSettings.Mounts["media-2"]);
        Assert.Equal(cliSettings.ProjectWriteRoots, appSettings.ProjectWriteRoots);
        Assert.Equal(cliSettings.ProtectedRoots, appSettings.ProtectedRoots);
        Assert.Equal(new WorkspaceAlias("Old/reference.png", "@arts/reference.png"), Assert.Single(appSettings.PathAliases));
        appSettings.Mounts.Clear();
        appSettings.ProjectWriteRoots[0] = "Changed";
        Assert.Equal("Art", reader.Current.GetOptions(WorkspaceProfile.Schema).Mounts["arts"]);
        Assert.Equal("Projects", Assert.Single(reader.Current.GetOptions(WorkspaceProfile.Schema).ProjectWriteRoots));
        Assert.Equal(json, File.ReadAllText(config));
    }

    [Theory]
    [InlineData("{}", "workspace:version")]
    [InlineData("""{"version":"1"}""", "workspace:version")]
    [InlineData("""{"version":2}""", "workspace:version")]
    [InlineData("""{"version":1,"version":1}""", "DuplicateKey")]
    [InlineData("""{"version":1,"PROJECT_WRITE_ROOTS":["Projects"]}""", "UnknownJsonKey")]
    [InlineData("""{"version":1,"legacy_read_enabled":"false"}""", "workspace:legacy_read_enabled")]
    [InlineData("""{"version":1,"projects_root":""}""", "workspace:projects_root")]
    [InlineData("""{"version":1,"project_write_roots":[null]}""", "workspace:project_write_roots")]
    [InlineData("""{"version":1,"mounts":{"arts":"Art","arts":"Other"}}""", "workspace:mounts")]
    [InlineData("""{"version":1,"path_aliases":[{"to":"@arts"}]}""", "workspace:path_aliases")]
    public async Task InvalidAccessProfileIsRejectedBeforeActivation(string json, string issue)
    {
        using var fixture = new PersistenceFixture();
        var config = Path.Combine(fixture.Root, "config.json");
        File.WriteAllText(config, json);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceProfile.ReadAsync(config).AsTask());
        Assert.Contains(issue, error.Message);
        Assert.Throws<ConfigurationException>(() => App.Build(context => context.UseConfiguration(WorkspaceProfile.Create(config))));
        Assert.Equal(json, File.ReadAllText(config));
    }
}
