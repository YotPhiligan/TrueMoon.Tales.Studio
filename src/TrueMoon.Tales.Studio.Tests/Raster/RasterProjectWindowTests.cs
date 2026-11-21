using TrueMoon;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Presentation.Project;
using TrueMoon.Tales.Studio.Tests.Backend;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Raster;

public sealed class RasterProjectWindowTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task BackendView_RendersLoadingLoadedFailureAndReload_UsingPinnedFonts(float scale)
    {
        using var files = new BackendFixture();
        var baseline = await files.Files.Store().OpenAsync();
        var snapshot = new OpenedBackendProject(BackendConnectionTests.Identity(), 1, baseline.Fingerprint,
            baseline.Project with { Name = "Сказки истинной луны · Ёж перед рассветом" });
        var reader = new Reader();
        using var app = App.Builder(_ => { }).Setup(context => context.UseProjectBackend(files.Options, false).UseProjectWindow()
            .Services(r => r.Singleton<IBackendProjectReader>(_ => reader))).Build();
        var view = app.Services.Resolve<IFactory<ProjectPreviewView>>()!.Create()!;
        using var host = new RasterWindow<ProjectPreviewView>(view, "project", 1000, 720, scale);
        host.Update();
        Assert.False(view.ReloadButton.IsEnabled);
        Assert.Equal("Открытие проекта…", view.ProjectTitle.Value);
        var loading = host.Capture("loading", new { calls = reader.Calls });
        reader.Pending.SetResult(snapshot); host.Update();
        Assert.Equal(snapshot.Project.Name, view.ProjectTitle.Value);
        Assert.Contains("11", view.RevisionText.Value);
        Assert.True(view.ReloadButton.IsEnabled);
        var loaded = host.Capture("loaded", new { revision = 11 });
        RasterAssert.Dimensions(loaded, (int)(1000 * scale), (int)(720 * scale));
        RasterAssert.TextInk(loaded, view.ProjectTitle.Bounds, scale);
        Assert.NotEqual(loading, loaded);
        Assert.Equal(loaded, host.Capture("loaded-repeat", new { revision = 11 }));
        host.Click(view.ReloadButton);
        Assert.Equal(2, reader.Calls);
        Assert.False(view.ReloadButton.IsEnabled);
        reader.Pending.SetException(new IOException("Нет связи с backend")); host.Update();
        Assert.Contains("Нет связи с backend", view.StatusText.Value);
        Assert.Contains("предыдущий снимок", view.RevisionText.Value);
        Assert.Equal(snapshot.Project.Name, view.ProjectTitle.Value);
        var failure = host.Capture("failure", new { revision = 11, error = true });
        Assert.NotEqual(loaded, failure);
        host.Update(); Assert.Equal(2, reader.Calls);
        host.Click(view.ReloadButton);
        reader.Pending.SetResult(snapshot with { Project = snapshot.Project with { Revision = 12 } }); host.Update();
        Assert.Equal(3, reader.Calls);
        Assert.Contains("12", view.RevisionText.Value);
        Assert.DoesNotContain("предыдущий снимок", view.RevisionText.Value);
        Assert.Equal("Проект открыт · только чтение", view.StatusText.Value);
        host.Capture("reloaded", new { revision = 12 });
        host.Resize(760, 600, scale);
        var resized = host.Capture("resized", new { width = 760, height = 600 });
        RasterAssert.Dimensions(resized, (int)(760 * scale), (int)(600 * scale));
        Assert.True(view.StatusText.Bounds.Y + view.StatusText.Bounds.Height <= 600);
        Assert.Equal(files.Files.InitialBytes, File.ReadAllBytes(files.Files.ProjectPath));
        Assert.Empty(files.Files.HistoryFiles);
    }

    private sealed class Reader : IBackendProjectReader
    {
        internal int Calls;
        internal TaskCompletionSource<OpenedBackendProject> Pending = null!;
        public Task<OpenedBackendProject> OpenAsync(CancellationToken cancellationToken = default)
        {
            Calls++; var pending = Pending = new();
            return pending.Task.WaitAsync(cancellationToken);
        }
    }
}
