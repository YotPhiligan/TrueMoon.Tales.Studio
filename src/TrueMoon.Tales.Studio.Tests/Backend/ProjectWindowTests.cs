using TrueMoon;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Cobalt;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Presentation.Project;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class ProjectWindowTests
{
    [Fact]
    public async Task Cobalt_CreatesReadOnlyWindow_AndFrameAppliesSnapshotAndRoutedReload()
    {
        using var fixture = new BackendFixture();
        var reply = await fixture.Service().OpenProjectAsync(fixture.Options.SessionKey);
        var opened = BackendConnection.Decode(BackendConnectionTests.Identity(), reply);
        var reader = new Reader(opened);
        await using var app = App.Builder(_ => { }).Setup(context => context
            .UseProjectBackend(fixture.Options, false).UseProjectWindow()
            .Services(r => r.Singleton<IBackendProjectReader>(_ => reader))).Build();
        Assert.IsType<CobaltServiceResolver>(app.Services);
        using var window = app.Services.Resolve<IFactory<ProjectWindow>>()!.Create()!;
        using var session = new UiSession(window, new HeadlessSurface(), new(1000, 720));
        window.AdvanceFrame(TimeSpan.Zero, session);
        session.Update();
        Assert.Equal(opened.Project.Name, window.ProjectTitle.Value);
        Assert.Contains("11", window.RevisionText.Value);
        Assert.Contains("только чтение", window.StatusText.Value);
        Assert.Equal(1, reader.Calls);
        Assert.True(window.ReloadButton.IsEnabled);
        Assert.True(window.ProjectTitle.Bounds.Height > 0);
        Assert.True(window.StatusText.Bounds.Y + window.StatusText.Bounds.Height <= 720);
        var button = window.ReloadButton.Bounds;
        session.HandleInput(new(InputKind.PointerDown, button.X + 4, button.Y + 4));
        session.HandleInput(new(InputKind.PointerUp, button.X + 4, button.Y + 4));
        window.AdvanceFrame(TimeSpan.Zero, session);
        session.Update();
        Assert.Equal(2, reader.Calls);
        Assert.Equal(opened.Project.Name, window.ProjectTitle.Value);
        Assert.Single(Walk(window).OfType<Button>()); // This role offers no editing or save controls.
    }

    private static IEnumerable<Element> Walk(Element element) => new[] { element }.Concat(element.Children.SelectMany(Walk));

    private sealed class Reader(OpenedBackendProject project) : IBackendProjectReader
    {
        internal int Calls;
        public Task<OpenedBackendProject> OpenAsync(CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(project); }
    }
}
