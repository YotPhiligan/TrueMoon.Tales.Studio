using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Alloy.Hosting;
using TrueMoon.Argentis;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Presentation.Project;
using TrueMoon.Tales.Studio.Presentation.Projects;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Persistence;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class ProjectPickerTests
{
    [Fact]
    public async Task Catalog_MergesAliasesAndDiscovery_AndExplainsUnavailableRowsWithoutWriting()
    {
        using var files = new PickerFixture();
        var aliased = await files.ProjectAsync("outside", location: files.VideoRoot, legacyLayout: true);
        var ready = await files.ProjectAsync("ready");
        var schema1 = await files.ProjectAsync("schema1", migrate: false);
        var duplicate = await files.ProjectAsync("duplicate");
        var denied = await files.ProjectAsync("denied");
        var missingClip = await files.ProjectAsync("missing-clip", clip: false);
        var pending = await files.ProjectAsync("pending");
        var unsupported = await files.ProjectAsync("unsupported", migrate: false);
        var linkedTarget = await files.ProjectAsync("linked-target", location: files.Root);
        Directory.CreateDirectory(Path.Combine(duplicate, "Production"));
        File.Copy(PickerFixture.FileAt(duplicate), PickerFixture.FileAt(duplicate, legacy: true));
        File.WriteAllText(Path.Combine(pending, ".Studio.project.transaction.json"), "{}");
        File.WriteAllText(PickerFixture.FileAt(unsupported), """{"schema_version":8}""");
        var linked = Path.Combine(files.Projects, "linked");
        BackendMediaResolverTests.CreateDirectoryLink(linked, linkedTarget);
        try
        {
            var stale = Path.Combine(files.VideoRoot, "stale");
            files.Registry(new { id = "outside", root = "Old/outside" }, new { id = "ready", root = "@projects/ready" },
                new { id = "stale", root = stale }, new { id = "bad", root = 42 },
                new { id = "protected", root = linkedTarget }, new { id = "wrong-id", root = "@projects/ready" });
            files.Catalog.Remember(new("Исчезнувший проект", stale, ProjectAvailability.Ready, stale, "stale"));
            var before = files.Snapshot([ready, schema1, duplicate, denied, missingClip, pending, unsupported, aliased, linkedTarget]);
            var registry = Hash(File.ReadAllBytes(files.RegistryPath));

            var listing = await files.Catalog.ReadAsync(CancellationToken.None);
            Assert.Empty(listing.Notices);
            Assert.Equal(5, listing.Items.Count(row => row.CanOpen));
            Assert.Equal(2, Row("outside").Schema);
            Assert.Equal(aliased, Row("outside").Directory);
            Assert.Equal(ProjectAvailability.MigrationRequired, Row("schema1").Availability);
            Assert.Equal(ProjectAvailability.WriteDenied, Row("denied").Availability);
            Assert.Equal(ProjectAvailability.NoClip, Row("missing-clip").Availability);
            Assert.Equal(ProjectAvailability.DuplicateLocation, At(duplicate).Availability);
            Assert.Equal(ProjectAvailability.RecoveryRequired, At(pending).Availability);
            Assert.Equal(ProjectAvailability.Unsupported, At(unsupported).Availability);
            Assert.Equal(ProjectAvailability.AccessDenied, At(linked).Availability);
            Assert.Contains(listing.Items, row => row.Location == linkedTarget && row.Availability == ProjectAvailability.AccessDenied);
            Assert.Contains(listing.Items, row => row.Location == ready && row.Availability == ProjectAvailability.IdentityMismatch);
            Assert.Contains(listing.Items, row => row.Availability == ProjectAvailability.Invalid && row.Location == "Registry.json");
            Assert.Single(listing.Items, row => row.Recent && row.Location == stale && row.Availability == ProjectAvailability.Missing);
            Assert.Equal(before, files.Snapshot([ready, schema1, duplicate, denied, missingClip, pending, unsupported, aliased, linkedTarget]));
            Assert.Equal(registry, Hash(File.ReadAllBytes(files.RegistryPath)));
            Assert.False(File.Exists(Path.Combine(ready, ".Studio.project.writer.lock")));
            Assert.False(Directory.Exists(files.LegacyRoot));
            return;

            ProjectEntry Row(string id) => Assert.Single(listing.Items, row => row.Id == id && row.Availability != ProjectAvailability.IdentityMismatch);
            ProjectEntry At(string path) => Assert.Single(listing.Items, row => row.Location == path && row.Availability != ProjectAvailability.IdentityMismatch);
        }
        finally { Directory.Delete(linked); }
    }

    [Fact]
    public async Task PickerStartup_BuildsHostedWindowWithProductionChrome()
    {
        using var files = new PickerFixture();
        await using var app = ProjectPickerApplication.BuildPicker(files.Catalog, _ => false);
        var options = new SilkUiWindowOptions();
        ProjectPickerApplication.ConfigurePickerWindow(options);

        Assert.NotNull(app.Services.Resolve<HostedUiWindow>());
        Assert.False(options.Appearance.Decorated);
        Assert.True(options.Chrome?.NativeDrag == true);
    }

    [Fact]
    public void EmptyPicker_RendersAndAllowsRefresh()
    {
        using var files = new PickerFixture();
        using var host = new RasterWindow<ProjectPickerWindow>(new(files.Catalog, _ => throw new InvalidOperationException()),
            "picker-empty", 740, 480);
        host.PumpUntil(() => host.Window.RefreshButton.IsEnabled);
        Assert.Empty(host.Window.Rows);
        Assert.False(host.Window.OpenButton.IsEnabled);
        Assert.Contains("Проекты не найдены", host.Window.Status);
        host.Click(host.Window.RefreshButton);
        host.PumpUntil(() => host.Window.RefreshButton.IsEnabled);
        Assert.Empty(host.Window.Rows);
        host.Capture("empty", new { host.Window.Status });
        Assert.False(Directory.Exists(files.State));
    }

    [Fact]
    public async Task FirstLaunch_OpensEmptyEditorAndShowsItInRecentsOnNextLaunch()
    {
        using var files = new PersistenceFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        var personal = Path.Combine(files.Root, "Personal");
        var config = StudioStartup.ProjectPickerConfig([], new(),
            () => StudioStartup.DefaultConfig(null, files.Root, personal))!;
        var catalog = new ProjectCatalog(config);
        var (first, notice) = await ProjectPickerApplication.PrepareEmptyWorkspaceAsync(catalog, token);
        Assert.Null(notice);
        var project = Assert.IsType<ProjectEntry>(first);
        Assert.Equal(ProjectAvailability.NoClip, project.Availability);
        Assert.StartsWith(personal + Path.DirectorySeparatorChar, project.Directory);
        var path = PickerFixture.FileAt(project.Directory!);
        var before = File.ReadAllBytes(path);
        var entered = false;
        var error = await ProjectPickerApplication.OpenSelectedAsync(catalog, project, token,
            app => RasterEditor(app, (services, host) =>
            {
                var model = services.Resolve<SceneViewModel>()!;
                Assert.Null(model.Scene);
                var empty = Assert.IsType<EmptyProject>(model.EmptyProject);
                Assert.Equal(("Без названия", 1L, 24d, "1280 × 736"), (empty.Name, empty.Revision, empty.Fps, empty.Dimensions));
                Assert.False(ButtonAt(host.Window.Heading, "Экспорт MP4").IsEnabled);
                Assert.Contains(RasterWindow<SceneView>.Walk(host.Window.Timeline), item => item.AccessibleName == "Монтажных клипов пока нет");
                host.Capture("first-launch", new { empty.Name, empty.Revision });
                entered = true;
            }), BackendWriteFixture.Executable());

        Assert.Null(error);
        Assert.True(entered);
        Assert.Equal(config, StudioStartup.DefaultConfig(null, files.Root, personal));
        Assert.Equal((null, null), await ProjectPickerApplication.PrepareEmptyWorkspaceAsync(catalog, token));
        using var picker = new RasterWindow<ProjectPickerWindow>(new(catalog, _ => throw new InvalidOperationException()), "startup-recents", 740, 480);
        picker.PumpUntil(() => picker.Window.RefreshButton.IsEnabled);
        var recent = Assert.Single(picker.Window.Rows).Entry;
        Assert.True(recent.Recent);
        Assert.Equal(project.Id, recent.Id);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(project.Directory!, "Production", "History")));
        picker.Capture("recent", new { recent.Name, recent.Revision });
    }

    [Fact]
    public async Task DefaultLaunch_LeavesUnavailableProjectsVisibleAndExplainsDeniedCreation()
    {
        using var files = new PickerFixture();
        var token = CancellationToken.None;
        var (empty, notice) = await ProjectPickerApplication.PrepareEmptyWorkspaceAsync(files.Catalog, token);
        Assert.Null(empty);
        Assert.Contains("Не удалось начать новый проект", notice);
        Assert.Empty(Directory.GetDirectories(files.Projects));

        await files.ProjectAsync("schema1", migrate: false);
        var item = Assert.Single((await files.Catalog.ReadAsync(token)).Items);
        Assert.Equal(ProjectAvailability.MigrationRequired, item.Availability);
        Assert.Equal((null, null), await ProjectPickerApplication.PrepareEmptyWorkspaceAsync(files.Catalog, token));
        Assert.Single(Directory.GetDirectories(files.Projects));
    }

    [Theory]
    [InlineData("schema1", false, true)]
    [InlineData("denied", false, false)]
    [InlineData("denied", true, false)]
    public async Task Picker_OpensReadableProjectWithoutWriter(string id, bool migrated, bool canMigrate)
    {
        using var files = new PickerFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var path = await files.ProjectAsync(id, migrate: migrated);
        var before = files.Snapshot([path]);
        ProjectEntry? selection = null;
        using (var host = new RasterWindow<ProjectPickerWindow>(new(files.Catalog, entry => selection = entry), "picker-read-only", 1000, 720))
        {
            host.PumpUntil(() => host.Window.RefreshButton.IsEnabled);
            var row = Assert.Single(host.Window.Rows);
            host.Click(row.Button);
            Assert.True(host.Window.OpenButton.IsVisible && host.Window.OpenButton.IsEnabled);
            Assert.Equal(canMigrate, host.Window.MaintenanceButton.IsVisible && host.Window.MaintenanceButton.IsEnabled);
            Assert.Contains("Только чтение", row.Button.AccessibleName);
            host.Capture("available-for-reading", new { row.Entry.Schema, canMigrate });
            host.Click(host.Window.OpenButton);
        }

        var selected = Assert.IsType<ProjectEntry>(selection);
        var opened = false;
        var error = await ProjectPickerApplication.OpenSelectedAsync(files.Catalog, selected, deadline.Token, app =>
        {
            var services = app.Services;
            Assert.False(services.Resolve<BackendOptions>()!.WriteEnabled);
            var window = services.Resolve<IFactory<ProjectWindow>>()!.Create()!;
            using var view = new RasterWindow<ProjectWindow>(window, "read-only-project", 1000, 720);
            view.PumpUntil(() => window.ReloadButton.IsEnabled);
            Assert.Equal(selected.Name, window.ProjectTitle.Value);
            Assert.Contains($"схема {(migrated ? 2 : 1)}", window.RevisionText.Value);
            Assert.Contains("только чтение", window.StatusText.Value);
            Assert.False(File.Exists(Path.Combine(path, ".Studio.project.writer.lock")));
            view.Capture("read-only", new { selected.Id, selected.Revision });
            opened = true;
            services.Resolve<IAppLifetime>()!.Cancel();
        }, BackendWriteFixture.Executable());

        Assert.Null(error);
        Assert.True(opened);
        Assert.Equal(before, files.Snapshot([path]));
        Assert.True(Assert.Single((await files.Catalog.ReadAsync(deadline.Token)).Items).Recent);
    }

    [Fact]
    public async Task Picker_OpenExistingFolder_OpensUnlistedLegacyProjectAndRemembersItWithoutWrites()
    {
        using var files = new PickerFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var path = await files.ProjectAsync("unlisted", migrate: false, location: files.VideoRoot, legacyLayout: true);
        var before = files.Snapshot([path]);
        ProjectEntry? selection = null;
        using (var host = new RasterWindow<ProjectPickerWindow>(new(files.Catalog, entry => selection = entry), "picker-open-folder", 740, 480, 1.5f))
        {
            host.PumpUntil(() => host.Window.OpenExistingButton.IsEnabled);
            Assert.Empty(host.Window.Rows);
            host.Click(host.Window.NewButton);
            host.Click(host.Window.OpenExistingButton);
            host.PumpUntil(() => host.Window.FolderInput.IsFocused);
            Fill(host, host.Window.FolderInput, $"\"{path}\"");
            host.Capture("open-folder", new { Schema = 1 });
            host.Input(new(InputKind.KeyDown, Key: UiKey.Enter));
            host.PumpUntil(() => selection is not null);
        }

        var selected = Assert.IsType<ProjectEntry>(selection);
        Assert.Equal(path, selected.Directory);
        Assert.Equal(1, selected.Schema);
        Assert.True(selected.OpensReadOnly);
        Assert.False(Directory.Exists(files.State));
        var opened = false;
        var error = await ProjectPickerApplication.OpenSelectedAsync(files.Catalog, selected, deadline.Token, app =>
        {
            var services = app.Services;
            Assert.False(services.Resolve<BackendOptions>()!.WriteEnabled);
            var window = services.Resolve<IFactory<ProjectWindow>>()!.Create()!;
            using var host = new RasterWindow<ProjectWindow>(window, "picker-open-folder-read-only", 1000, 720);
            host.PumpUntil(() => window.ReloadButton.IsEnabled);
            Assert.Equal(selected.Name, window.ProjectTitle.Value);
            Assert.Contains("только чтение", window.StatusText.Value);
            Assert.False(File.Exists(Path.Combine(path, ".Studio.project.writer.lock")));
            opened = true;
            services.Resolve<IAppLifetime>()!.Cancel();
        }, BackendWriteFixture.Executable());

        Assert.Null(error);
        Assert.True(opened);
        var recent = Assert.Single((await files.Catalog.ReadAsync(deadline.Token)).Items);
        Assert.True(recent.Recent);
        Assert.Equal(path, recent.Directory);
        Assert.Equal(before, files.Snapshot([path]));
    }

    [Fact]
    public async Task Picker_OpenExistingFolder_ReportsInvalidPathsAndCanCancel()
    {
        using var files = new PickerFixture();
        var outside = await files.ProjectAsync("blocked", migrate: false, location: files.Root);
        var missing = Path.Combine(files.VideoRoot, "missing");
        Directory.CreateDirectory(missing);
        var duplicate = await files.ProjectAsync("duplicate", migrate: false);
        var secondLocation = PickerFixture.FileAt(duplicate, legacy: true);
        Directory.CreateDirectory(Path.GetDirectoryName(secondLocation)!);
        File.Copy(PickerFixture.FileAt(duplicate), secondLocation);
        using var host = new RasterWindow<ProjectPickerWindow>(new(files.Catalog, _ => throw new InvalidOperationException("Unexpected open")),
            "picker-open-folder-errors", 740, 480);
        host.PumpUntil(() => host.Window.OpenExistingButton.IsEnabled);
        host.Click(host.Window.OpenExistingButton);
        host.PumpUntil(() => host.Window.FolderInput.IsFocused);
        Check("relative/path", "полный путь");
        Check(outside, "вне разрешённых корней");
        Check(missing, "Файл проекта не найден");
        Check(duplicate, "Найдены два Studio.project.json");
        host.Input(new(InputKind.KeyDown, Key: UiKey.Escape));
        Assert.True(host.Window.OpenExistingButton.IsEnabled);
        Assert.True(host.Window.NewButton.IsEnabled);
        Assert.Empty(host.Window.Status);
        Assert.False(Directory.Exists(files.State));

        void Check(string path, string error)
        {
            Fill(host, host.Window.FolderInput, path);
            host.Click(host.Window.ConfirmButton);
            host.PumpUntil(() => host.Window.Status.Contains(error, StringComparison.Ordinal));
            Assert.Equal(path, host.Window.FolderInput.Value);
        }
    }

    [Fact]
    public async Task LinkedStateDirectory_DoesNotGrantRegistryReadsOrRecentWrites()
    {
        using var files = new PickerFixture();
        await files.ProjectAsync("alpha");
        var external = Path.Combine(files.Root, "External");
        Directory.CreateDirectory(external);
        var foreignRegistry = Path.Combine(external, "Registry.json");
        File.WriteAllText(foreignRegistry, """{"projects":[{"id":"foreign","root":"@projects/foreign"}]}""");
        BackendMediaResolverTests.CreateDirectoryLink(files.State, external);
        try
        {
            var listing = await files.Catalog.ReadAsync(CancellationToken.None);
            var item = Assert.Single(listing.Items);
            Assert.Equal("alpha", item.Id);
            Assert.True(item.CanOpen);
            Assert.Contains(listing.Notices, message => message.StartsWith("Реестр проектов", StringComparison.Ordinal));
            Assert.Throws<UnauthorizedAccessException>(() => files.Catalog.Remember(item));
            Assert.Equal(foreignRegistry, Assert.Single(Directory.GetFiles(external)));
            Assert.Contains("foreign", File.ReadAllText(foreignRegistry));
        }
        finally { Directory.Delete(files.State); }
    }

    [Fact]
    public async Task Picker_RasterSelectionOpensTwoOwnedEditorsAndRecent_WithIsolatedJobsAndWriterHandoff()
    {
        using var files = new PickerFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var alpha = await files.ProjectAsync("alpha", revision: 11);
        var beta = await files.ProjectAsync("beta", revision: 16);
        var alphaJob = files.Export(alpha, "running");
        var betaJob = files.Export(beta, "cancelled");
        var before = files.Snapshot([alpha, beta]);
        Assert.False(File.Exists(Path.Combine(alpha, ".Studio.project.writer.lock")));
        Assert.False(File.Exists(Path.Combine(beta, ".Studio.project.writer.lock")));

        var first = Pick(files, "alpha", keyboard: true);
        Assert.False(File.Exists(Path.Combine(alpha, ".Studio.project.writer.lock")));
        Assert.False(Directory.Exists(files.State));
        await OpenAndClose(first, alphaJob, betaJob, expectWarning: true);
        var second = Pick(files, "beta", keyboard: false, previous: first);
        await OpenAndClose(second, betaJob, alphaJob, expectWarning: false);
        var recent = Pick(files, "alpha", keyboard: true, previous: second);
        Assert.True(recent.Recent);
        await OpenAndClose(recent, alphaJob, betaJob, expectWarning: true);
        Assert.Equal(new[] { "alpha", "beta" }, (await files.Catalog.ReadAsync(deadline.Token)).Items
            .Where(row => row.Recent).Select(row => row.Id));
        Assert.Equal(before, files.Snapshot([alpha, beta]));
        Assert.Equal("running", files.Status(alpha, alphaJob).State);
        Assert.Equal("cancelled", files.Status(beta, betaJob).State);

        async Task OpenAndClose(ProjectEntry selected, string job, string otherJob, bool expectWarning)
        {
            BackendProcess? owned = null;
            var observed = false;
            var error = await ProjectPickerApplication.OpenSelectedAsync(files.Catalog, selected, deadline.Token,
                app => RasterEditor(app, (services, host) =>
                {
                    owned = services.Resolve<BackendProcessFactory>()!.Owned;
                    Assert.NotNull(owned);
                    Assert.True(owned.IsAlive);
                    Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(files.Storage(selected.Directory!)));
                    var model = services.Resolve<SceneViewModel>()!;
                    Assert.Equal(selected.Revision, model.Scene!.Revision);
                    Assert.Equal(selected.Fingerprint, model.Scene.Fingerprint, ignoreCase: true);
                    Assert.Equal("clip-" + selected.Id, model.Scene.Clip.Id);
                    var connection = services.Resolve<BackendConnection>()!;
                    var fetched = connection.FindExportAsync(job, deadline.Token).GetAwaiter().GetResult();
                    Assert.Equal(selected.Id, fetched.ProjectId);
                    Assert.Equal(selected.Revision, fetched.ProjectRevision);
                    Assert.Equal("export_not_found", Assert.Throws<BackendCommandException>(() =>
                        connection.FindExportAsync(otherJob, deadline.Token).GetAwaiter().GetResult()).Code);
                    host.Click(ButtonAt(host.Window.Heading, "Экспорт MP4"));
                    var status = expectWarning ? "Рендеринг…" : "Экспорт отменён";
                    host.PumpUntil(() => RasterWindow<SceneView>.Walk(host.Window).OfType<Text>().Any(item => item.Value == status));
                    host.Input(new(InputKind.KeyDown, Key: UiKey.Escape));
                    observed = true;
                }, expectWarning), BackendWriteFixture.Executable());
            Assert.Null(error);
            Assert.True(observed);
            Assert.NotNull(owned);
            Assert.False(owned.IsAlive);
            using (ProjectWriterLease.Acquire(files.Storage(selected.Directory!))) { }
        }
    }

    [Fact]
    public async Task CreateFromRasterForm_OpensAndReopensEmptyProjectWithoutChangingLegacyRegistry()
    {
        using var files = new PickerFixture(canCreate: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        files.Registry();
        var registry = Hash(File.ReadAllBytes(files.RegistryPath));
        ProjectEntry? selection = null;
        using (var host = new RasterWindow<ProjectPickerWindow>(new(files.Catalog, item => selection = item), "create-project", 740, 480, 1.5f))
        {
            host.PumpUntil(() => host.Window.NewButton.IsEnabled);
            host.Click(host.Window.NewButton);
            host.Update();
            Fill(host, host.Window.NameInput, "Новый ролик");
            Fill(host, host.Window.WidthInput, "641");
            Fill(host, host.Window.HeightInput, "480");
            Fill(host, host.Window.FpsInput, "25");
            host.Click(host.Window.ConfirmButton);
            host.PumpUntil(() => host.Window.Status.Contains("чётными", StringComparison.Ordinal));
            Assert.Empty(Directory.GetDirectories(files.Projects));
            Assert.Null(selection);
            Fill(host, host.Window.WidthInput, "640");
            host.Capture("project-form", new { width = 640, height = 480, fps = 25 });
            host.Click(host.Window.ConfirmButton);
            host.PumpUntil(() => selection is not null);
        }
        var created = Assert.IsType<ProjectEntry>(selection);
        Assert.Equal(ProjectAvailability.NoClip, created.Availability);
        var directory = created.Directory!;
        var json = File.ReadAllBytes(PickerFixture.FileAt(directory));
        var initial = ProjectFileCodec.Read(json);
        Assert.Equal(2, initial.SchemaVersion);
        Assert.Equal(1, initial.Project.Revision);
        Assert.Equal(25, initial.Project.Fps);
        Assert.Equal(new[] { 640, 480 }, initial.Project.Properties["output_size"].Deserialize<int[]>());
        Assert.Empty(initial.Project.Montage.Windows);
        Assert.Empty(initial.Project.Montage.Clips);
        Assert.Empty(initial.Project.Segments);
        Assert.Empty(initial.Project.Sources);
        Assert.False(initial.OriginalDocument.TryGetProperty("legacy_document", out _));
        Assert.Equal(registry, Hash(File.ReadAllBytes(files.RegistryPath)));
        Assert.False(File.Exists(Path.Combine(directory, ".Studio.project.writer.lock")));

        var openedCount = 0;
        await Open(created);
        var recent = Assert.Single((await files.Catalog.ReadAsync(deadline.Token)).Items);
        Assert.True(recent.Recent);
        Assert.Equal(created.Id, recent.Id);
        await Open(recent);
        Assert.Equal(2, openedCount);
        Assert.Equal(json, File.ReadAllBytes(PickerFixture.FileAt(directory)));
        Assert.False(Directory.Exists(Path.Combine(directory, "Production", "History")));
        Assert.Equal(registry, Hash(File.ReadAllBytes(files.RegistryPath)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            files.Catalog.CreateAsync(new(" НОВЫЙ РОЛИК ", 640, 480, 25), deadline.Token));
        Assert.Equal(directory, Assert.Single(Directory.GetDirectories(files.Projects)));

        async Task Open(ProjectEntry entry)
        {
            var error = await ProjectPickerApplication.OpenSelectedAsync(files.Catalog, entry, deadline.Token,
                app => RasterEditor(app, (services, host) =>
                {
                    var model = services.Resolve<SceneViewModel>()!;
                    Assert.True(model.ProjectReady);
                    Assert.Null(model.Scene);
                    var empty = Assert.IsType<EmptyProject>(model.EmptyProject);
                    Assert.Equal(1, empty.Revision);
                    Assert.Equal("640 × 480", empty.Dimensions);
                    Assert.False(host.Window.SaveButton.IsEnabled);
                    Assert.False(ButtonAt(host.Window.Heading, "Экспорт MP4").IsEnabled);
                    Assert.Contains(RasterWindow<SceneView>.Walk(host.Window.Timeline), element =>
                        element.AccessibleName == "Монтажных клипов пока нет");
                    Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(files.Storage(directory)));
                    host.Capture("empty-editor", new { empty.Name, empty.Revision, empty.Fps });
                    openedCount++;
                }), BackendWriteFixture.Executable());
            Assert.Null(error);
            using (ProjectWriterLease.Acquire(files.Storage(directory))) { }
        }
    }

    [Fact]
    public async Task Creation_WritesOnlyWithinGrantAndPublishesAtMostOneDirectory()
    {
        using (var denied = new PickerFixture())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                denied.Catalog.CreateAsync(new("Недоступный", 640, 480, 25), CancellationToken.None));
            Assert.Empty(Directory.GetDirectories(denied.Projects));
        }
        using var files = new PickerFixture(canCreate: true);
        var cancelled = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            files.Catalog.CreateAsync(new("Отменённый", 640, 480, 25), cancelled));
        var root = new ProjectStore(files.Storage(files.Projects));
        var draft = new ProjectContract { Id = "project-race", Name = "Гонка", Revision = 1 };
        var occupied = Path.Combine(files.Projects, "project-occupied");
        File.WriteAllText(occupied, "existing file");
        await Assert.ThrowsAsync<IOException>(() => root.CreateChildAsync("project-occupied", draft));
        Assert.Equal("existing file", File.ReadAllText(occupied));
        async Task<ProjectFileDocument?> TryCreate()
        {
            try { return await root.CreateChildAsync("project-race", draft); }
            catch (IOException) { return null; }
        }
        var results = await Task.WhenAll(Task.Run(TryCreate), Task.Run(TryCreate));
        Assert.Single(results.OfType<ProjectFileDocument>());
        var directory = Assert.Single(Directory.GetDirectories(files.Projects));
        Assert.Equal("project-race", Path.GetFileName(directory));
        Assert.Equal("project-race", ProjectFileCodec.Read(File.ReadAllBytes(PickerFixture.FileAt(directory))).Project.Id);
        Assert.False(Directory.Exists(files.State));
    }

    [Fact]
    public async Task ConfirmedMaintenance_MigratesOnceAndRecoversOnlyTheObservedJournalThroughBackend()
    {
        using var files = new PickerFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var legacy = await files.ProjectAsync("schema1", migrate: false, clip: false);
        var pending = await files.ProjectAsync("pending");
        var legacyJson = File.ReadAllBytes(PickerFixture.FileAt(legacy));
        var pendingJson = File.ReadAllBytes(PickerFixture.FileAt(pending));
        var store = new ProjectStore(files.Storage(pending), new FailBeforeReplace(), TimeProvider.System);
        var original = await store.OpenAsync(deadline.Token);
        await Assert.ThrowsAsync<IOException>(() =>
            store.SaveAsync(original, original.Project with { Name = "После восстановления" },
                original.Project.Revision, "picker-test", deadline.Token));
        var journal = Path.Combine(pending, ".Studio.project.transaction.json");
        var originalJournal = File.ReadAllBytes(journal);
        var selection = await Get("pending", ProjectAvailability.RecoveryRequired);
        var guarded = await files.Catalog.PrepareAsync(selection, deadline.Token, maintenance: true);
        File.AppendAllText(journal, " ");
        var conflict = await Assert.ThrowsAsync<BackendCommandException>(() =>
            BackendProjectMaintenance.ApplyAsync(guarded with { ExecutablePath = BackendWriteFixture.Executable() }, deadline.Token));
        Assert.Equal("recovery_conflict", conflict.Code);
        Assert.Equal(pendingJson, File.ReadAllBytes(PickerFixture.FileAt(pending)));
        Assert.True(File.Exists(journal));
        File.WriteAllText(journal, "{}");
        var malformed = await Get("pending", ProjectAvailability.RecoveryRequired);
        var rejected = await Assert.ThrowsAsync<BackendCommandException>(() =>
            files.Catalog.MaintainAsync(malformed, deadline.Token, BackendWriteFixture.Executable()));
        Assert.Equal("invalid_project", rejected.Code);
        Assert.Equal("{}", File.ReadAllText(journal));
        Assert.Equal(pendingJson, File.ReadAllBytes(PickerFixture.FileAt(pending)));
        File.WriteAllBytes(journal, originalJournal);

        using (var host = new RasterWindow<ProjectPickerWindow>(new(files.Catalog, _ => throw new InvalidOperationException(),
            maintain: (entry, token) => files.Catalog.MaintainAsync(entry, token, BackendWriteFixture.Executable())), "project-maintenance", 1000, 720))
        {
            host.PumpUntil(() => host.Window.RefreshButton.IsEnabled);
            host.Click(Find(host, "schema1").Button);
            host.Click(host.Window.MaintenanceButton);
            host.Click(host.Window.CancelButton);
            Assert.Equal(legacyJson, File.ReadAllBytes(PickerFixture.FileAt(legacy)));
            host.Click(host.Window.MaintenanceButton);
            host.Click(host.Window.ConfirmButton);
            host.PumpUntil(() => Find(host, "schema1").Entry.Availability == ProjectAvailability.NoClip && host.Window.OpenButton.IsEnabled,
                TimeSpan.FromSeconds(18));

            host.Click(Find(host, "pending").Button);
            host.Click(host.Window.MaintenanceButton);
            host.Capture("recovery-confirmation", new { journal = selection.Fingerprint });
            host.Click(host.Window.ConfirmButton);
            host.PumpUntil(() => Find(host, "pending").Entry.Availability == ProjectAvailability.Ready && host.Window.OpenButton.IsEnabled,
                TimeSpan.FromSeconds(18));
        }
        var migrated = await Get("schema1", ProjectAvailability.NoClip);
        Assert.Equal(12, migrated.Revision);
        var migratedHistory = Directory.GetFiles(Path.Combine(legacy, "Production", "History"), "*.json");
        Assert.Equal(legacyJson, File.ReadAllBytes(Assert.Single(migratedHistory)));
        var recovered = await Get("pending", ProjectAvailability.Ready);
        Assert.Equal(original.Project.Revision + 1, recovered.Revision);
        Assert.Equal("После восстановления", recovered.Name);
        Assert.False(File.Exists(journal));
        Assert.Contains(Directory.GetFiles(Path.Combine(pending, "Production", "History"), "*.json"),
            path => File.ReadAllBytes(path).SequenceEqual(pendingJson));
        using (ProjectWriterLease.Acquire(files.Storage(legacy))) { }
        using (ProjectWriterLease.Acquire(files.Storage(pending))) { }

        async Task<ProjectEntry> Get(string id, ProjectAvailability state)
        {
            var row = Assert.Single((await files.Catalog.ReadAsync(deadline.Token)).Items, item => item.Id == id);
            Assert.Equal(state, row.Availability);
            return row;
        }
    }

    private sealed class FailBeforeReplace : IAtomicFileReplacement
    {
        public void Replace(string pendingPath, string destinationPath) => throw new IOException("Simulated interruption.");
    }

    private static (ProjectEntry Entry, Button Button) Find(RasterWindow<ProjectPickerWindow> host, string id) =>
        Assert.Single(host.Window.Rows, row => row.Entry.Id == id);

    private static void Fill(RasterWindow<ProjectPickerWindow> host, TextBox input, string value)
    {
        host.Click(input);
        host.Input(new(InputKind.KeyDown, Key: UiKey.A, Control: true));
        host.Input(new(InputKind.Text, Text: value));
        Assert.Equal(value, input.Value);
    }

    [Fact]
    public async Task FailedOpen_ReturnsToListWithoutRememberingBusyChangedOrFailedBackend()
    {
        using var files = new PickerFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var alpha = await files.ProjectAsync("alpha");
        await files.ProjectAsync("beta");
        var selected = Pick(files, "alpha", keyboard: false);
        void Configure(IApp app) => RasterEditor(app, (_, _) => { });
        string? busy;
        using (ProjectWriterLease.Acquire(files.Storage(alpha)))
        {
            busy = await ProjectPickerApplication.OpenSelectedAsync(files.Catalog, selected, deadline.Token,
                Configure, BackendWriteFixture.Executable());
            Assert.Contains("другой сессии", busy);
        }
        Assert.False(Directory.Exists(files.State));
        using (var returned = new RasterWindow<ProjectPickerWindow>(new(files.Catalog, _ => { }, selected, busy), "picker-error", 1000, 720))
        {
            returned.PumpUntil(() => returned.Window.RefreshButton.IsEnabled);
            Assert.Equal(busy, returned.Window.Status);
            Assert.True(returned.Window.OpenButton.IsEnabled);
            Assert.Equal(2, returned.Window.Rows.Count);
        }

        File.AppendAllText(PickerFixture.FileAt(alpha), " ");
        var changed = await ProjectPickerApplication.OpenSelectedAsync(files.Catalog, selected, deadline.Token,
            _ => throw new InvalidOperationException("Should stop at preflight"));
        Assert.Contains("изменился после просмотра", changed);
        selected = Pick(files, "alpha", keyboard: false);
        var failed = await ProjectPickerApplication.OpenSelectedAsync(files.Catalog, selected, deadline.Token,
            Configure, Path.Combine(files.Root, "invalid-studio-backend.exe"));
        Assert.False(string.IsNullOrWhiteSpace(failed));
        Assert.False(Directory.Exists(files.State));
        using (ProjectWriterLease.Acquire(files.Storage(alpha))) { }
    }

    private static ProjectEntry Pick(PickerFixture files, string id, bool keyboard, ProjectEntry? previous = null)
    {
        ProjectEntry? selection = null;
        using var host = new RasterWindow<ProjectPickerWindow>(new(files.Catalog, row => selection = row, previous),
            "picker-" + id, 1000, 720);
        host.PumpUntil(() => host.Window.RefreshButton.IsEnabled);
        var row = Assert.Single(host.Window.Rows, row => row.Entry.Id == id);
        if (previous is not null)
        {
            host.PumpUntil(() => host.Window.Rows.Any(item => item.Entry.Id == previous.Id && item.Button.IsFocused));
            host.Click(host.Window.RefreshButton);
            host.PumpUntil(() => host.Window.Rows.Any(item => item.Entry.Id == previous.Id && item.Button.IsFocused));
            row = Assert.Single(host.Window.Rows, item => item.Entry.Id == id);
        }
        Assert.Equal("Одинаковое название", row.Entry.Name);
        Assert.Equal(2, host.Window.Rows.Select(item => item.Entry.Location).Distinct(ProjectCatalog.PathComparer).Count());
        if (keyboard)
        {
            host.Resize(740, 480, 1.5f);
            host.Click(host.Window.Rows.Last().Button);
            host.Input(new(InputKind.KeyDown, Key: row.Button == host.Window.Rows[0].Button ? UiKey.Up : UiKey.End));
            Assert.True(row.Button.IsFocused);
            Assert.Null(selection);
            host.Capture("keyboard-selection", new { row.Entry.Id, row.Entry.Recent, row.Entry.Revision });
            host.Input(new(InputKind.KeyDown, Key: UiKey.Enter));
        }
        else
        {
            host.Click(row.Button);
            Assert.Null(selection);
            host.Click(host.Window.OpenButton);
        }
        Assert.NotNull(selection);
        return selection;
    }

    private static Button ButtonAt(Element root, string title) =>
        Assert.Single(RasterWindow<SceneView>.Walk(root).OfType<Button>(), button => button.Value == title && button.IsVisible);

    private static void RasterEditor(IApp app, Action<IServiceProvider, RasterWindow<SceneView>> exercise, bool expectWarning = false)
    {
        var services = app.Services;
        var model = services.Resolve<SceneViewModel>()!;
        var window = services.Resolve<IFactory<SceneView>>()!.Create()!;
        var closeRequested = false;
        using var title = window.CreateTitleBar(() => closeRequested = true);
        using var host = new RasterWindow<SceneView>(window, "picker-editor", 1160, 760);
        host.PumpUntil(() => model.ProjectReady || model.HasError, TimeSpan.FromSeconds(18));
        if (model.ProjectReady)
        {
            exercise(services, host);
            host.Click(ButtonAt(window.Footer, "К проектам"));
            Assert.True(closeRequested);
            Assert.Equal(!expectWarning, host.RequestCloseAsync().GetAwaiter().GetResult());
            if (expectWarning)
            {
                Assert.True(window.CloseNotice.IsVisible);
                host.Capture("export-close", new { model.Scene!.Revision });
                host.Click(ButtonAt(window.CloseNotice, "Закрыть проект"));
                Assert.True(host.RequestCloseAsync().GetAwaiter().GetResult());
            }
        }
        services.Resolve<IAppLifetime>()!.Cancel();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class PickerFixture : IDisposable
    {
        private readonly PersistenceFixture _scratch = new();
        internal string Root => _scratch.Root;
        internal string Projects => Path.Combine(VideoRoot, "Projects");
        internal string VideoRoot => Path.Combine(Root, "Videos");
        internal string State => Path.Combine(Root, "State");
        internal string LegacyRoot => Path.Combine(Root, "Book");
        internal string RegistryPath => Path.Combine(State, "Registry.json");
        internal ProjectCatalog Catalog { get; }

        internal PickerFixture(bool canCreate = false)
        {
            Directory.CreateDirectory(Projects);
            Catalog = new(Path.Combine(Root, "Studio.config.json"));
            File.WriteAllText(Catalog.ConfigPath, JsonSerializer.Serialize(new
            {
                version = 1, projects_root = Projects, amv_root = VideoRoot, state_root = State,
                legacy_root = LegacyRoot, legacy_read_enabled = false, exports_root = Path.Combine(Root, "Export"),
                path_aliases = new[] { new { from = "Old", to = VideoRoot } },
                project_write_roots = canCreate ? new[] { Projects } : new[] { "ready", "alpha", "beta", "schema1", "duplicate", "missing-clip", "pending", "unsupported" }
                    .Select(name => Path.Combine(Projects, name))
                    .Append(Path.Combine(Projects, "denied", "Studio.project.json")).Append(Path.Combine(VideoRoot, "outside")),
                protected_roots = new[] { Path.Combine(Root, "linked-target") }
            }));
        }

        internal async Task<string> ProjectAsync(string id, bool migrate = true, bool clip = true,
            string? location = null, bool legacyLayout = false, int revision = 11)
        {
            var directory = Path.Combine(location ?? Projects, id);
            var path = FileAt(directory, legacyLayout);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = Encoding.UTF8.GetString(PersistenceFixture.LegacyBytes()).TrimStart('\uFEFF')
                .Replace("clip-main", "clip-" + id, StringComparison.Ordinal);
            var project = JsonNode.Parse(text)!.AsObject();
            project["id"] = id;
            project["name"] = "Одинаковое название";
            project["revision"] = revision;
            var timeline = project["timeline"]!.AsObject();
            timeline.Remove("scenario");
            if (!clip) timeline["clips"] = new JsonArray();
            File.WriteAllText(path, project.ToJsonString());
            if (migrate)
            {
                var store = new ProjectStore(new ProjectStoreOptions { ProjectDirectory = directory, AllowedWriteRoots = [directory] });
                await store.MigrateAsync(await store.OpenAsync(), revision, "picker-fixture");
            }
            return directory;
        }

        internal string Export(string directory, string state)
        {
            var project = ProjectFileCodec.Read(File.ReadAllBytes(FileAt(directory)));
            var key = "picker-export-" + project.Project.Id;
            var id = BackendExportJobs.JobId(key);
            var request = JsonSerializer.SerializeToUtf8Bytes(new { project.Project.Id, project.Fingerprint });
            var receipt = new BackendExportStatus(1, id, project.Project.Id, project.Project.Revision, project.Fingerprint,
                "project", 1, "running", Hash(request), 24, 24);
            var storage = ExportStorage(directory);
            var job = storage.JobDirectory(id);
            storage.Create(job, request, receipt);
            if (state == "cancelled") storage.Save(job, receipt with { State = state, Revision = 3 });
            storage.SaveClientKey(key);
            return key;
        }

        internal BackendExportStatus Status(string directory, string key)
        {
            var storage = ExportStorage(directory);
            return JsonSerializer.Deserialize<BackendExportStatus>(storage.Read(storage.JobDirectory(BackendExportJobs.JobId(key)),
                "Status.json", BackendExportStorage.MaxStatusBytes), BackendExportStorage.Json)!;
        }

        private BackendExportStorage ExportStorage(string directory) => new(BackendOptions.Create(directory, Catalog.ConfigPath));
        internal ProjectStoreOptions Storage(string directory) => new BackendProjectAccess(Catalog.ConfigPath).ForProject(directory, true);
        internal static string FileAt(string directory, bool legacy = false) =>
            legacy ? Path.Combine(directory, "Production", "Studio.project.json") : Path.Combine(directory, "Studio.project.json");

        internal string[] Snapshot(string[] projects) => projects.SelectMany(directory => Directory.EnumerateFiles(directory, "*.json",
            SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => path + ":" + Hash(File.ReadAllBytes(path)))).ToArray();

        internal void Registry(params object[] projects)
        {
            Directory.CreateDirectory(State);
            File.WriteAllText(RegistryPath, JsonSerializer.Serialize(new { projects }));
        }

        public void Dispose() => _scratch.Dispose();
    }
}
