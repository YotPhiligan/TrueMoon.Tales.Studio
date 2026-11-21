using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Tests.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Configuration;

public sealed class StudioStartupTests
{
    [Fact]
    public void ProjectPicker_UsesDefaultOnlyForInteractiveLaunchWithoutArguments()
    {
        var user = new UnitLaunchSettings();
        Assert.Equal("default.json", StudioStartup.ProjectPickerConfig([], user, () => "default.json"));
        Assert.Equal("chosen.json", StudioStartup.ProjectPickerConfig(["--projects", "--config", "chosen.json"], user, Unexpected));
        Assert.Null(StudioStartup.ProjectPickerConfig(["--projects"], user, Unexpected));
        Assert.Null(StudioStartup.ProjectPickerConfig(["--inspect-project", "p", "--config", "c"], user, Unexpected));
        Assert.Null(StudioStartup.ProjectPickerConfig([], new(UnitId: 1), Unexpected));
        Assert.Null(StudioStartup.ProjectPickerConfig([], new(UnitsDisabled: true), Unexpected));

        static string Unexpected() => throw new InvalidOperationException("The default configuration must not be loaded.");
    }

    [Fact]
    public void DebugLaunch_UsesAppDataAndHonorsOverrides()
    {
        using var files = new PersistenceFixture();
        var checkout = Path.Combine(files.Root, "Checkout");
        var source = Path.Combine(checkout, "src");
        var output = Path.Combine(source, "Studio", "bin", "Debug");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(source, "TrueMoon.Tales.Studio.slnx"), "");
        var workspace = Path.Combine(checkout, "config.json");
        File.WriteAllText(workspace, "{}");
        File.WriteAllText(Path.Combine(output, "Studio.config.local.json"), "{}");
        var personal = Path.Combine(files.Root, "Personal");
        Assert.Equal(Path.Combine(source, "override.json"), StudioStartup.DefaultConfig("override.json", source, personal));
        Assert.False(Directory.Exists(personal));

        var expected = Path.Combine(personal, "TrueMoon.Tales", "Studio", "config.json");
        Assert.Equal(expected, StudioStartup.DefaultConfig(null, output, personal));
        Assert.True(File.Exists(expected));
        File.WriteAllText(expected, "{\"version\":1}");
        Assert.Equal(expected, StudioStartup.DefaultConfig(null, checkout, personal));
        Assert.Equal("{\"version\":1}", File.ReadAllText(expected));
        Assert.Equal("{}", File.ReadAllText(workspace));
    }

    [Fact]
    public void DefaultConfig_PrefersShortNameThenEarlierProfilesInOrder()
    {
        using var files = new PersistenceFixture();
        var legacy = Path.Combine(files.Root, "TrueMoon", "Tales Studio", "Studio.config.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        const string legacyJson = """{"version":1,"projects_root":"LegacyProjects"}""";
        File.WriteAllText(legacy, legacyJson);

        var profileDirectory = Path.Combine(files.Root, "TrueMoon.Tales", "Studio");
        var current = Path.Combine(profileDirectory, "config.json");
        Assert.Equal(legacy, StudioStartup.DefaultConfig(null, files.Root, files.Root));
        Assert.False(Directory.Exists(profileDirectory));

        Directory.CreateDirectory(profileDirectory);
        var previous = Path.Combine(profileDirectory, "Studio.config.local.json");
        const string previousJson = """{"version":1,"projects_root":"Projects"}""";
        File.WriteAllText(previous, previousJson);
        Assert.Equal(previous, StudioStartup.DefaultConfig(null, files.Root, files.Root));
        Assert.False(File.Exists(current));

        File.WriteAllText(current, "{}");
        Assert.Equal(current, StudioStartup.DefaultConfig(null, files.Root, files.Root));
        Assert.Equal(previous, StudioStartup.DefaultConfig(previous, files.Root, files.Root));
        Assert.Equal(legacyJson, File.ReadAllText(legacy));
        Assert.Equal(previousJson, File.ReadAllText(previous));
        Assert.Equal("{}", File.ReadAllText(current));
    }
}
