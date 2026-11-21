using System.Text.Json;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Presentation.Shell;
using TrueMoon.Tales.Studio.Tests.Persistence;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Configuration;

public sealed class StudioUiPreferencesTests
{
    [Fact]
    public async Task UiProfile_UsesAppDataDefaultsAndPersistsThemeWithoutTouchingWorkspace()
    {
        using var fixture = new PersistenceFixture();
        var path = StudioUiPreferences.ProfilePath(fixture.Root);
        Assert.Equal(Path.Combine(fixture.Root, "TrueMoon.Tales", "Studio", "ui.json"), path);
        await using var preferences = StudioUiPreferences.Open(path);
        Assert.Equal("dark", preferences.Current.Theme);
        Assert.Null(preferences.Current.EditorSize);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var workspace = Path.Combine(Path.GetDirectoryName(path)!, "config.json");
        const string workspaceJson = """{"version":1,"projects_root":"Projects"}""";
        File.WriteAllText(workspace, workspaceJson);
        var saved = await preferences.SaveThemeAsync("light");
        Assert.Equal(("light", null), saved);
        Assert.Equal("light", preferences.Current.Theme);
        using var written = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("light", written.RootElement.GetProperty("ui").GetProperty("theme").GetString());
        await using var reopened = StudioUiPreferences.Open(path);
        Assert.Equal("light", reopened.Current.Theme);
        Assert.Equal(workspaceJson, File.ReadAllText(workspace));
    }

    [Fact]
    public async Task UiProfile_DefersStartupSizeAndThemeTogetherUntilFreshWindow()
    {
        using var fixture = new PersistenceFixture();
        var path = Path.Combine(fixture.Root, "ui.json");
        File.WriteAllText(path, """{"ui":{"editor_size":[1280,820]}}""");
        await using var preferences = StudioUiPreferences.Open(path);
        File.WriteAllText(path, """{"ui":{"editor_size":[1360,900]}}""");

        var changed = await preferences.SaveThemeAsync("light");
        Assert.Equal("dark", changed.Theme);
        Assert.Contains("Откройте новое окно", changed.Notice);
        Assert.Equal(new[] { 1280, 820 }, preferences.Current.EditorSize);
        await using var fresh = StudioUiPreferences.Open(path);
        Assert.Equal("light", fresh.Current.Theme);
        Assert.Equal(new[] { 1360, 900 }, fresh.Current.EditorSize);
    }

    [Theory]
    [InlineData("""{"ui":{"theme":"unknown"}}""")]
    [InlineData("""{"ui":{"projects_size":[100,100]}}""")]
    [InlineData("""{"ui":{"editor_size":[1280]}}""")]
    [InlineData("""{"workspace":{"project_write_roots":["C:/"]}}""")]
    [InlineData("{not json}")]
    public async Task UiProfile_UsesSafeDefaultsForInvalidFileAndPreservesIt(string json)
    {
        using var fixture = new PersistenceFixture();
        var path = Path.Combine(fixture.Root, "ui.json");
        File.WriteAllText(path, json);
        await using var preferences = StudioUiPreferences.Open(path);
        Assert.Equal("dark", preferences.Current.Theme);
        Assert.Null(preferences.Current.ProjectsSize);
        Assert.NotNull(preferences.Notice);
        var local = await preferences.SaveThemeAsync("light");
        Assert.Equal("light", local.Theme);
        Assert.NotNull(local.Notice);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public async Task WindowProfile_StoresLastNormalSizeOnCloseAndUsesItOnNextOpen()
    {
        using var fixture = new PersistenceFixture();
        var path = Path.Combine(fixture.Root, "ui.json");
        await using var preferences = StudioUiPreferences.Open(path);
        var window = new Window { ClientSize = new(1000, 720) };
        preferences.Attach(window, StudioUiWindow.Projects);
        using (var host = new RasterWindow<Window>(window, "preferences-window-size", 1000, 720))
        {
            host.Resize(1100, 760, 1.5f);
            window.Maximize();
            host.Resize(1600, 920, 1);
            Assert.False(File.Exists(path));
            var close = window.RequestCloseAsync();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                if (!close.IsCompleted) host.Update();
                return close.IsCompleted;
            }, TimeSpan.FromSeconds(5)));
            Assert.True(await close);
        }

        await using var reopened = StudioUiPreferences.Open(path);
        using var projects = new Window { ClientSize = new(1000, 720) };
        using var editor = new Window { ClientSize = new(1280, 820) };
        reopened.Attach(projects, StudioUiWindow.Projects);
        reopened.Attach(editor, StudioUiWindow.Editor);
        Assert.Equal(new Size(1100, 760), projects.ClientSize);
        Assert.Equal(new Size(1280, 820), editor.ClientSize);
    }

    [Fact]
    public async Task EditorMenu_SwitchesThemeInFrameAndPersistsItForNextWindow()
    {
        using var fixture = new PersistenceFixture();
        var path = Path.Combine(fixture.Root, "ui.json");
        await using var preferences = StudioUiPreferences.Open(path);
        using var scene = new InteractionFixture();
        var editor = new SceneView(scene.Model, new SceneViewStyle(), new StudioTitleBarFactory(new StudioTitleBarStyle()),
            preferences: preferences);
        var frame = WindowFrame.CreateDefault(editor.Title, editor);
        frame.TitleBarOverContent = true;
        var header = editor.CreateTitleBar(() => throw new InvalidOperationException("Unexpected close"));
        frame.DefaultTitleBar!.SetHeader(header);
        var root = new Window();
        root.Content(() => frame);
        using var host = new RasterWindow<Window>(root, "preferences-editor-theme", 1280, 820);
        host.PumpUntil(() => scene.Model.Loaded && scene.Model.Idle);
        Assert.Equal(Theme.Dark.Surface, editor.Theme.Surface);
        var dark = host.Capture("dark", new { theme = "dark" });

        host.Input(new(InputKind.KeyDown, Key: UiKey.F10));
        host.Click(header.Menu.Headings["view"]);
        var theme = Assert.Single(RasterWindow<Window>.Walk(editor.Overlays.Popup!).OfType<MenuItem>(),
            item => item.ActionId == "view.theme");
        host.Click(theme);
        host.PumpUntil(() => root.Theme.Surface == Theme.Light.Surface);
        Assert.Equal(Theme.Light.Surface, frame.Theme.Surface);
        Assert.Equal(Theme.Light.Surface, editor.Theme.Surface);
        Assert.NotEqual(dark, host.Capture("light", new { theme = "light" }));
        Assert.Empty(scene.Server.Posts);
        await using var restarted = StudioUiPreferences.Open(path);
        Assert.Equal("light", restarted.Current.Theme);
    }
}
