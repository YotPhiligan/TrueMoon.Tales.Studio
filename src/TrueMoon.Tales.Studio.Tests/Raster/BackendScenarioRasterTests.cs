using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Argentis;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Presentation.Scenario;
using TrueMoon.Tales.Studio.Tests.Backend;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Raster;

public sealed class BackendScenarioRasterTests
{
    [BackendMediaFact]
    public async Task ScenarioAndLibrary_ThroughRasterAndIpc_PreserveManyToManyLinksAndExplicitWriter()
    {
        using var files = new BackendFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = files.Files.Root;
        var initial = await new ProjectStore(new() { ProjectDirectory = root, AllowedWriteRoots = [root] })
            .CreateChildAsync("project-story", new ProjectContract
            {
                Id = "scenario-project", Name = "Проверка сценария", Revision = 1,
                Properties = new Dictionary<string, JsonElement> { ["output_size"] = JsonSerializer.SerializeToElement(new[] { 160, 120 }) }
            });
        var directory = Path.GetDirectoryName(initial.FilePath)!;
        var media = Path.Combine(root, "Media");
        Directory.CreateDirectory(media);
        var red = Path.Combine(media, "red.mp4");
        var blue = Path.Combine(media, "blue.mp4");
        var relocated = Path.Combine(media, "red-copy.mp4");
        await VideoAsync(red, "red", deadline.Token);
        await VideoAsync(blue, "blue", deadline.Token);
        File.Copy(red, relocated);
        var cfg = JsonNode.Parse(File.ReadAllText(files.ConfigPath))!;
        cfg["projects_root"] = directory;
        cfg["mounts"] = new JsonObject { ["clips"] = media };
        cfg["project_write_roots"] = new JsonArray(directory);
        cfg["exports_root"] = Path.Combine(root, "Exports");
        cfg["python_executable"] = Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON");
        cfg["ffmpeg_executable"] = Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG");
        cfg["ffprobe_executable"] = Environment.GetEnvironmentVariable("STUDIO_TEST_FFPROBE");
        File.WriteAllText(files.ConfigPath, cfg.ToJsonString());
        var options = BackendOptions.Create(directory, files.ConfigPath) with { WriteEnabled = true, ExecutablePath = BackendWriteFixture.Executable() };
        await using var app = App.Build(context => context.UseProjectBackend(options, false).UseBackendScene());
        try
        {
            await app.StartAsync(deadline.Token);
            var model = app.Services.Resolve<SceneViewModel>()!;
            var backend = app.Services.Resolve<BackendConnection>()!;
            using var host = new RasterWindow<SceneView>(app.Services.Resolve<IFactory<SceneView>>()!.Create()!,
                "scenario-library", 1280, 900);
            host.PumpUntil(() => model.ProjectReady, TimeSpan.FromSeconds(15));
            Click(host, "Сценарий и материалы");
            host.PumpUntil(() => ButtonIn(host, "Новый сегмент").IsEnabled);
            var scenario = Assert.Single(Elements(host).OfType<ScenarioWorkspace>());
            Click(host, "Новый сегмент");
            Fill(host, "Название сегмента", "Сегмент QA");
            Fill(host, "Длительность окна, кадры", "48");
            Save();
            Click(host, "Новое действие");
            Fill(host, "Название действия", "Склейки QA");
            Fill(host, "Плановая длительность, кадры", "48");
            var description = Assert.Single(Elements(host).OfType<RichTextEditor>(), item => item.AccessibleName == "Описание");
            host.Click(description);
            host.Input(new(InputKind.Text, Text: "Сначала текст"));
            Save();
            Assert.Empty(Read().Project.Segments.Single().Actions.Single().Sources);
            Assert.Empty(Read().Project.Montage.Clips);
            Click(host, "Новое действие");
            Fill(host, "Название действия", "Второе действие");
            Save();

            Import("Источник А", "Дубль красный", "@clips/red.mp4");
            Link("Реакция");
            Click(host, "Склейки QA");
            Link("Общий план");
            Click(host, "Добавить дубль");
            Fill(host, "Название дубля", "Дубль синий");
            Fill(host, "Путь или ссылка на видео", "@clips/blue.mp4");
            Save();

            var beforePreview = File.ReadAllBytes(initial.FilePath!);
            Click(host, "Дубль красный");
            Click(host, "Кадр A");
            host.PumpUntil(() => Elements(host).OfType<Image>().Any(image =>
                image.Source is not null && image.AccessibleName.StartsWith("A · Источник А / Дубль красный", StringComparison.Ordinal)));
            Click(host, "Дубль синий");
            Click(host, "Кадр B");
            host.PumpUntil(() => Elements(host).OfType<Image>().Any(image =>
                image.Source is not null && image.AccessibleName.StartsWith("B · Источник А / Дубль синий", StringComparison.Ordinal)));
            Assert.Equal(2, Elements(host).OfType<Image>().Count(image => image.Source is not null));
            Assert.Equal(beforePreview, File.ReadAllBytes(initial.FilePath!));
            host.Capture("compare-takes", new { revision = Read().Project.Revision, imageCount = 2 });

            Click(host, "Дубль красный");
            Place(0);
            Import("Источник Б", "Дубль Б", "@clips/blue.mp4");
            Link("Вставка");
            Place(0);
            Fill(host, "Поиск источника", "красный");
            Click(host, "Найти");
            Assert.DoesNotContain(Elements(host).OfType<Button>(), button => button.Value == "Источник Б");
            Click(host, "Источник А");
            Place(24);
            var placed = Read();
            var sourceA = placed.Project.Sources.Single(row => row.Title == "Источник А");
            var sourceB = placed.Project.Sources.Single(row => row.Title == "Источник Б");
            var actions = placed.Project.Segments.Single().Actions;
            var clips = placed.Project.Montage.Clips;
            Assert.Equal(new[] { sourceA.Id, sourceB.Id, sourceA.Id }, clips.Select(row => row.SourceId));
            Assert.All(clips, row => Assert.Equal(actions[0].Id, row.ActionId));
            Assert.Equal(sourceA.Id, Assert.Single(actions[1].Sources).SourceId);
            Assert.Equal(new[] { 0, 12, 24 }, clips.Select(row => row.StartFrame));
            Assert.Equal(new double[] { 0, 0, 24 }, clips.Select(row => row.SourceInFrame));
            Assert.Equal(48, actions[0].PlannedDurationFrames);
            Assert.Equal("Сначала текст", actions[0].Description);
            Assert.Equal(2, actions[0].Sources.Length);
            VerifyExportAsync(placed, backend, options, deadline.Token).GetAwaiter().GetResult();

            Click(host, "0 · Источник А · 12 к.");
            Click(host, "Дубль синий");
            Click(host, "Назначить дубль клипу");
            Save();
            var assigned = Read();
            Assert.Equal(clips[0] with { TakeId = sourceA.Takes[1].Id }, assigned.Project.Montage.Clips[0]);
            Assert.Equal(clips[1..], assigned.Project.Montage.Clips[1..]);
            Click(host, "Дубль красный");
            Click(host, "Перепривязать файл");
            Fill(host, "Путь или ссылка на видео", "@clips/red-copy.mp4");
            Save();
            var linked = Read();
            Assert.Equal(assigned.Project.Montage.Clips, linked.Project.Montage.Clips);
            Assert.Equal("@clips/red-copy.mp4", BackendScenarioEdit.TryReference(linked.Project, sourceA.Takes[0].VideoAssetId!));
            Assert.Equal(File.ReadAllBytes(red), File.ReadAllBytes(relocated));

            Click(host, "Править действие");
            Fill(host, "Название действия", "Черновик QA");
            var outside = backend.OpenAsync(deadline.Token).GetAwaiter().GetResult();
            var segment = outside.Project.Segments.Single();
            backend.EditScenarioAsync(new SaveSegment(segment.Id, "Сегмент обновлён", segment.Description, 0, 0), outside, deadline.Token)
                .GetAwaiter().GetResult();
            var beforeConflict = File.ReadAllBytes(initial.FilePath!);
            Click(host, "Сохранить");
            host.PumpUntil(() => Elements(host).OfType<Text>().Any(text => text.Value.Contains("Проект изменился", StringComparison.Ordinal)));
            Assert.Equal("Черновик QA", Assert.Single(Elements(host).OfType<TextBox>(), input => input.AccessibleName == "Название действия").Value);
            Assert.False(ButtonIn(host, "Сохранить").IsEnabled);
            Click(host, "Перечитать");
            host.PumpUntil(() => ButtonIn(host, "Сохранить").IsEnabled);
            Click(host, "Отменить");
            Assert.Equal(beforeConflict, File.ReadAllBytes(initial.FilePath!));
            Click(host, "К монтажу");
            host.PumpUntil(() => model.Loaded && model.Idle, TimeSpan.FromSeconds(12));
            Assert.Equal(3, model.Scene!.Choices.Length);
            Assert.False(model.Scene.ScenarioActive);
            Assert.Equal(3, model.Scene.SegmentClips!.Length);
            Assert.Equal(beforeConflict, File.ReadAllBytes(initial.FilePath!));

            var forbidden = Assert.Throws<BackendCommandException>(() => backend.EditScenarioAsync(
                new ImportVideo(null, "За пределами", "Внешний дубль", "@clips/../forbidden.mp4"),
                backend.OpenAsync(deadline.Token).GetAwaiter().GetResult(), deadline.Token).GetAwaiter().GetResult());
            Assert.Equal("access_denied", forbidden.Code);
            Assert.Equal(beforeConflict, File.ReadAllBytes(initial.FilePath!));

            ProjectFileDocument Read() => ProjectFileCodec.Read(File.ReadAllBytes(initial.FilePath!));
            void Save()
            {
                var before = File.ReadAllBytes(initial.FilePath!);
                var revision = ProjectFileCodec.Read(before).Project.Revision;
                Click(host, "Сохранить");
                host.PumpUntil(() => !scenario.Busy, TimeSpan.FromSeconds(14));
                Assert.True(ButtonIn(host, "К монтажу").IsEnabled, scenario.Status);
                Assert.Equal(revision + 1, Read().Project.Revision);
                Assert.Contains(Directory.GetFiles(Path.Combine(directory, "Production", "History"), "*.json"),
                    file => File.ReadAllBytes(file).SequenceEqual(before));
            }
            void Import(string title, string take, string path)
            {
                Click(host, "Новый источник");
                Fill(host, "Название источника", title);
                Fill(host, "Название дубля", take);
                Fill(host, "Путь или ссылка на видео", path);
                Save();
            }
            void Link(string purpose)
            {
                Click(host, "Связать источник");
                Fill(host, "Назначение источника", purpose);
                Save();
            }
            void Place(int sourceIn)
            {
                Click(host, "Разместить клип");
                Fill(host, "Длительность клипа, кадры", "12");
                Fill(host, "Начало в дубле, кадр", sourceIn.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Save();
            }
        }
        finally { await app.StopAsync(); }
        using (ProjectWriterLease.Acquire(BackendProjectAccess.Read(options))) { }
    }

    private static IEnumerable<Element> Elements(RasterWindow<SceneView> host) =>
        RasterWindow<SceneView>.Walk(host.Window).Where(item => item.IsVisible);
    private static Button ButtonIn(RasterWindow<SceneView> host, string label) =>
        Assert.Single(Elements(host).OfType<Button>(), button => button.Value == label);

    private static void Click(RasterWindow<SceneView> host, string label)
    {
        var target = ButtonIn(host, label);
        Assert.True(target.IsEffectivelyEnabled, $"Disabled: {label}");
        for (var node = target.Parent; node is not null; node = node.Parent)
            if (node is ScrollViewer scroll) { scroll.BringIntoView(target.Bounds); host.Update(); }
        host.Click(target);
    }

    private static void Fill(RasterWindow<SceneView> host, string label, string value)
    {
        var field = Assert.Single(Elements(host).OfType<TextBox>(), item => item.AccessibleName == label);
        host.Click(field);
        host.Input(new(InputKind.KeyDown, Key: UiKey.A, Control: true));
        host.Input(new(InputKind.Text, Text: value));
        Assert.Equal(value, field.Value);
    }

    private static async Task VideoAsync(string path, string color, CancellationToken token)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-nostdin", "-v", "error", "-f", "lavfi", "-i",
            $"color=c={color}:size=160x120:rate=24:duration=3", "-threads", "1", "-c:v", "mpeg4", path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        try
        {
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            Assert.True(process.ExitCode == 0, await error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private static async Task VerifyExportAsync(ProjectFileDocument placed, BackendConnection backend,
        BackendOptions options, CancellationToken token)
    {
        var check = await backend.CheckExportAsync("project", placed.Project.Revision, placed.Fingerprint, token);
        Assert.Equal(48, check.Frames);
        var job = await backend.StartExportAsync("scenario-aba-export", "project", placed.Project.Revision, placed.Fingerprint, token);
        while (job.State == "running")
        {
            await Task.Delay(30, token);
            job = await backend.ExportStatusAsync(job.Id, token);
        }
        Assert.True(job.State == "completed", $"Export ended with {job.State}: {job.ErrorCode}");
        var video = Path.Combine(new BackendExportStorage(options).JobDirectory(job.Id), "Draft.mp4");
        Assert.Equal(job.ArtifactSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(video))), ignoreCase: true);
        var decoder = BackendMediaReader.Decoder(options);
        var info = await decoder.ProbeAsync(video, token);
        Assert.Equal(48, info.FrameCount);
        var redFirst = await decoder.ReadAsync(video, 0, info, token);
        var blue = await decoder.ReadAsync(video, 12, info, token);
        var redAgain = await decoder.ReadAsync(video, 24, info, token);
        var center = (FrameDecoder.Height / 2 * FrameDecoder.Width + FrameDecoder.Width / 2) * 4;
        Assert.True(redFirst[center + 2] > redFirst[center]);
        Assert.True(blue[center] > blue[center + 2]);
        Assert.True(redAgain[center + 2] > redAgain[center]);
    }
}
