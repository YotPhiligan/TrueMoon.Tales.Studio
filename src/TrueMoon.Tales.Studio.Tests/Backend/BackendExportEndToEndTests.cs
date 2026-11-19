using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon;
using TrueMoon.Argentis;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Presentation.Scenario;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendExportEndToEndTests
{
    [BackendMediaFact]
    public async Task Tracks_FromRaster_SaveCyrillicTitleAndMusic_AndReviewBeforeExport()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync(withTransition: true);
        string previewId;
        byte[] savedBytes;
        byte[][] savedHistory;
        await using (var session = await files.OpenAsync(workspace: true))
        {
            var connection = session.Connection;
            using var host = Workspace(session);
            var model = session.App.Services.Resolve<SceneViewModel>()!;
            Ready(host, model);
            var initialBytes = File.ReadAllBytes(files.ProjectPath);
            host.Click(ButtonIn(host.Window.Timeline, "Дорожки"));
            var workspace = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<MontageTrackWorkspace>());
            host.PumpUntil(() => !workspace.NeedsFrame);
            host.Click(ButtonIn(workspace, "Музыка"));
            SetField(workspace, "Медиа (ID ассета или @mount/путь)", "video-a");
            SetField(workspace, "Кадр проекта", "0");
            SetField(workspace, "Длительность, к.", "48");
            SetField(workspace, "Громкость, 0–1", "0,35");
            var save = ButtonIn(workspace, "Сохранить дорожку");
            host.Click(save);
            host.PumpUntil(() => !workspace.NeedsFrame);
            var music = connection.OpenAsync().GetAwaiter().GetResult();
            Assert.Equal(13, music.Project.Revision);
            Assert.Equal(.35, music.Project.Montage.Properties["music"].GetProperty("gain").GetDouble());
            Assert.Contains(files.History(), row => row.SequenceEqual(initialBytes));

            host.Click(ButtonIn(workspace, "+ Титр"));
            SetField(workspace, "Название", "Карточка");
            SetField(workspace, "Кадр проекта", "36");
            SetField(workspace, "Длительность, к.", "4");
            SetField(workspace, "Фон (transparent / black)", "black");
            SetField(workspace, "Размер, пиксели", "18");
            var caption = Assert.Single(RasterWindow<SceneView>.Walk(workspace).OfType<RichTextEditor>());
            host.Click(caption);
            host.Input(new(InputKind.Text, Text: "Клэр"));
            host.Input(new(InputKind.KeyDown, Key: UiKey.Enter));
            host.Input(new(InputKind.Text, Text: "Луна"));
            Assert.Equal("Клэр\nЛуна", caption.Text);
            var newHead = connection.EditScenarioAsync(new SaveMontageMarker(null, "Стык", 36), music).GetAwaiter().GetResult();
            host.Click(save);
            host.PumpUntil(() => !workspace.NeedsFrame);
            Assert.False(save.IsEnabled);
            Assert.Equal("Клэр\nЛуна", caption.Text);
            host.Click(ButtonIn(workspace, "Перечитать дорожки"));
            host.PumpUntil(() => save.IsEnabled);
            host.Click(save);
            host.PumpUntil(() => !workspace.NeedsFrame);
            var baseline = connection.OpenAsync().GetAwaiter().GetResult();
            Assert.Equal(15, baseline.Project.Revision);
            Assert.Equal("Клэр\nЛуна", Assert.Single(baseline.Project.Montage.Properties["titles"].EnumerateArray()).GetProperty("text").GetString());
            Assert.Contains(files.History(), row => Hash(row).Equals(newHead.Fingerprint, StringComparison.OrdinalIgnoreCase));
            savedBytes = File.ReadAllBytes(files.ProjectPath);
            savedHistory = files.History();
            host.Click(ButtonIn(workspace, "К монтажу"));
            Ready(host, model);
            Assert.Equal(15, model.Scene!.Revision);
            Assert.Contains(model.Scene.Tracks!, row => row.Track == "music" && row.DurationFrames == 48);
            Assert.Contains(model.Scene.Tracks!, row => row.Track == "titles" && row.StartFrame == 36);
            host.Capture("montage-tracks", new { baseline.Project.Revision });

            host.Click(ButtonIn(host.Window.Preview, "Рабочий просмотр"));
            var preparing = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<WorkingReviewWorkspace>());
            host.Click(ButtonIn(preparing, "Подготовить проект"));
            var play = ButtonIn(preparing, "Смотреть проект");
            host.PumpUntil(() => play.IsEnabled, TimeSpan.FromSeconds(30));
            Assert.Empty(Directory.GetDirectories(files.OutputDirectory, "export-*", SearchOption.AllDirectories));
            var temp = Assert.Single(Directory.GetDirectories(files.OutputDirectory, "working-*", SearchOption.AllDirectories));
            previewId = "export-" + Path.GetFileName(temp)[8..];
            Assert.Equal("export_not_found", Assert.Throws<BackendCommandException>(() =>
                connection.ExportStatusAsync(previewId).GetAwaiter().GetResult()).Code);
            host.Click(play);
            var player = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<RenderedReviewPanel>());
            ReviewReady(host, player);
            SetField(player, "Кадр предпросмотра", "37");
            host.Click(ButtonIn(player, "Перейти"));
            ReviewReady(host, player);
            Assert.Contains(RasterWindow<SceneView>.Walk(player).OfType<Text>(), row => row.Value.Contains("0037 / 0071"));
            host.Capture("working-review-title", new { baseline.Project.Revision, Frame = 37 });
            host.Click(ButtonIn(player, "К монтажу"));
            Ready(host, model);
            host.PumpUntil(() => !model.NeedsFrame);

            var review = BackendRenderedMontage.Identifier(previewId, null);
            BackendMediaReply Read(string key, int frame) =>
                connection.FrameAsync(key, frame, baseline.Project.Revision, baseline.Fingerprint).GetAwaiter().GetResult();
            var titled = Read(review, 37);
            var outside = Read(review, 40);
            int GlyphPixels(byte[] pixels) => Enumerable.Range(0, pixels.Length / 4).Count(i =>
                pixels[i * 4] >= 195 && pixels[i * 4 + 1] >= 195 && pixels[i * 4 + 2] >= 195);
            Assert.True(GlyphPixels(titled.Pixels) > 100);
            Assert.Equal(0, GlyphPixels(outside.Pixels));
            Assert.InRange(Read(review, 35).Pixels[(135 * 480 + 240) * 4 + 2], 10, 55);
            var export = connection.StartExportAsync("tracks-parity-export", "draft", baseline.Project.Revision, baseline.Fingerprint).GetAwaiter().GetResult();
            var result = session.UntilAsync(export.Id, row => row["state"]!.GetValue<string>() != "running",
                files.JobDirectory(export.Id)).GetAwaiter().GetResult();
            Assert.Equal("completed", result["state"]!.GetValue<string>());
            var finalKey = BackendRenderedMontage.Identifier(export.Id, null);
            Assert.Equal(titled.PixelSha256, Read(finalKey, 37).PixelSha256);
            Assert.Equal(Read(review, 35).PixelSha256, Read(finalKey, 35).PixelSha256);
            Assert.Equal(outside.PixelSha256, Read(finalKey, 40).PixelSha256);
            var previewPcm = FirstAudio(review);
            Assert.Contains(MemoryMarshal.Cast<byte, float>(previewPcm).ToArray(), value => Math.Abs(value) > .012f);
            Assert.Equal(previewPcm, FirstAudio(finalKey));
            files.AssertProject(savedBytes, savedHistory);

            byte[] FirstAudio(string key)
            {
                var scene = new BackendRenderedMontage(key, 0, export.Frames, export.Fps, (int)baseline.Project.Revision, baseline.Fingerprint).Scene();
                var opened = connection.OpenPlaybackAsync(scene, "audio", 0, CancellationToken.None).GetAwaiter().GetResult();
                try { return connection.ReadPlaybackAsync(opened, CancellationToken.None).GetAwaiter().GetResult().Data; }
                finally { connection.ClosePlaybackAsync(opened.SessionId, CancellationToken.None).GetAwaiter().GetResult(); }
            }
        }
        Assert.Single(Directory.GetDirectories(files.OutputDirectory, "export-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(files.OutputDirectory, "working-*", SearchOption.AllDirectories));
        files.AssertProject(savedBytes, savedHistory);
    }

    [BackendMediaFact]
    public async Task RangeReview_FromRaster_PreservesProjectCoordinatesAndMatchesFullExport()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync(withTransition: true);
        await using (var session = await files.OpenAsync(workspace: true))
        {
            var connection = session.Connection;
            var head = await connection.AcquireWriterAsync(await connection.OpenAsync());
            head = await connection.EditScenarioAsync(new SaveMontageMusic("video-a", 8, 40, 0, .35, false), head);
            head = await connection.EditScenarioAsync(new SaveMontageTitle(null,
                new("Карточка", "Клэр", 36, 4, 2, 1, OperatingSystem.IsWindows() ? "arial" : "dejavu",
                    18, "#FFFFFF", "center", "center", "black", false)), head);
            var bytes = File.ReadAllBytes(files.ProjectPath);
            var history = files.History();
            const int start = 19, end = 43;

            using var host = Workspace(session);
            var model = session.App.Services.Resolve<SceneViewModel>()!;
            Ready(host, model);
            host.Click(ButtonIn(host.Window.Preview, "Рабочий просмотр"));
            var setup = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<WorkingReviewWorkspace>());
            Assert.Empty(files.Requests());
            SetField(setup, "Начало диапазона", "43");
            SetField(setup, "Конец диапазона (не входит)", "43");
            var prepare = ButtonIn(setup, "Подготовить диапазон");
            host.Click(prepare);
            Assert.Contains(RasterWindow<SceneView>.Walk(setup).OfType<Text>(), row => row.HasError);
            Assert.Empty(files.Requests());
            SetField(setup, "Начало диапазона", "19");
            host.Click(prepare);
            var view = ButtonIn(setup, "Смотреть диапазон");
            host.PumpUntil(() => view.IsEnabled, TimeSpan.FromSeconds(30));
            var folder = Assert.Single(Directory.GetDirectories(files.OutputDirectory, "working-*", SearchOption.AllDirectories));
            var jobId = "export-" + Path.GetFileName(folder)[8..];
            var completed = connection.WorkingReviewStatusAsync(jobId, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(start, completed.RangeStart);
            Assert.Equal(end - start, completed.Frames);
            Assert.Equal(head.Fingerprint, completed.ProjectFingerprint);
            Assert.Equal(head.Project.Revision, completed.ProjectRevision);
            var request = JsonNode.Parse(File.ReadAllText(Assert.Single(files.Requests())))!;
            Assert.Equal(start, (int)request["start_frame"]!);
            Assert.Equal(end - start, (int)request["frames"]!);
            Assert.Empty(Directory.GetDirectories(files.OutputDirectory, "export-*", SearchOption.AllDirectories));

            host.Click(view);
            var player = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<RenderedReviewPanel>());
            ReviewReady(host, player);
            Assert.Contains(RasterWindow<SceneView>.Walk(player).OfType<Text>(), row => row.Value.Contains("кадры 19–42"));
            SetField(player, "Кадр предпросмотра", "23");
            host.Click(ButtonIn(player, "Перейти"));
            ReviewReady(host, player);
            Assert.Contains(RasterWindow<SceneView>.Walk(player).OfType<Text>(), row => row.Value.Contains("0023 / 0023"));
            host.Capture("working-review-range", new { start, end, head.Project.Revision });
            host.Click(ButtonIn(player, "К монтажу"));
            Ready(host, model);
            host.PumpUntil(() => !model.NeedsFrame);

            var bad = Assert.Throws<BackendCommandException>(() => connection.StartWorkingReviewAsync("range-outside-timeline",
                head.Project.Revision, head.Fingerprint, CancellationToken.None, new(71, 73)).GetAwaiter().GetResult());
            Assert.Equal("invalid_export", bad.Code);
            var stale = Assert.Throws<BackendCommandException>(() => connection.StartWorkingReviewAsync("range-stale-snapshot",
                head.Project.Revision - 1, head.Fingerprint, CancellationToken.None, new(start, end)).GetAwaiter().GetResult());
            Assert.Equal("revision_conflict", stale.Code);
            Assert.Single(files.Requests());

            var export = connection.StartExportAsync("range-full-comparison", "draft", head.Project.Revision, head.Fingerprint).GetAwaiter().GetResult();
            var final = session.UntilAsync(export.Id, row => row["state"]!.GetValue<string>() != "running",
                files.JobDirectory(export.Id)).GetAwaiter().GetResult();
            Assert.Equal("completed", (string?)final["state"]);
            Assert.Equal(72, export.Frames);
            var reviewKey = BackendRenderedMontage.Identifier(jobId, null);
            var exportKey = BackendRenderedMontage.Identifier(export.Id, null);
            BackendMediaReply Frame(string asset, int frame) =>
                connection.FrameAsync(asset, frame, head.Project.Revision, head.Fingerprint).GetAwaiter().GetResult();
            Assert.Equal("invalid_frame", Assert.Throws<BackendCommandException>(() => Frame(reviewKey, end - start)).Code);
            foreach (var frame in new[] { start, 21, 23, 24, 35, 36, 37, 39, 40, end - 1 })
            {
                var bounded = Frame(reviewKey, frame - start);
                var full = Frame(exportKey, frame);
                Assert.Equal(end - start, bounded.FrameCount);
                Assert.InRange(bounded.Pixels.Zip(full.Pixels, (a, b) => Math.Abs(a - b)).Average(), 0, 4);
            }
            var pcm = Audio(reviewKey, start, end - start);
            var fullPcm = Audio(exportKey, 0, export.Frames).AsSpan(start * 4000, (end - start) * 4000).ToArray();
            Assert.Equal(fullPcm.Length, pcm.Length);
            double error = Math.Sqrt(pcm.Zip(fullPcm, (a, b) => (double)(a - b) * (a - b)).Average());
            Assert.InRange(error, 0, .018);
            Assert.Contains(pcm, sample => Math.Abs(sample) > .02f);
            files.AssertProject(bytes, history);

            float[] Audio(string key, int origin, int length)
            {
                var scene = new BackendRenderedMontage(key, origin, length, 24, (int)head.Project.Revision, head.Fingerprint).Scene();
                var stream = connection.OpenPlaybackAsync(scene, "audio", 0, CancellationToken.None).GetAwaiter().GetResult();
                using var data = new MemoryStream();
                try
                {
                    while (true)
                    {
                        var packet = connection.ReadPlaybackAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
                        Assert.Equal(data.Length / 8, packet.SampleOffset);
                        if (packet.Completed) break;
                        data.Write(packet.Data);
                    }
                }
                finally { connection.ClosePlaybackAsync(stream.SessionId, CancellationToken.None).GetAwaiter().GetResult(); }
                return MemoryMarshal.Cast<byte, float>(data.ToArray()).ToArray();
            }
        }
        Assert.Empty(Directory.GetDirectories(files.OutputDirectory, "working-*", SearchOption.AllDirectories));
    }

    [BackendMediaFact]
    public async Task RangeReview_CancelThroughRaster_LeavesProjectAndDurableExportsUntouched()
    {
        using var files = new ExportFixture(longRender: true);
        await files.PrepareAsync();
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        await using (var session = await files.OpenAsync(workspace: true))
        {
            using var host = Workspace(session);
            Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
            host.Click(ButtonIn(host.Window.Preview, "Рабочий просмотр"));
            var setup = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<WorkingReviewWorkspace>());
            SetField(setup, "Начало диапазона", "1");
            SetField(setup, "Конец диапазона (не входит)", "3635");
            host.Click(ButtonIn(setup, "Подготовить диапазон"));
            var cancel = ButtonIn(setup, "Отменить подготовку");
            host.PumpUntil(() => cancel.IsEnabled);
            var folder = Assert.Single(Directory.GetDirectories(files.OutputDirectory, "working-*", SearchOption.AllDirectories));
            var id = "export-" + Path.GetFileName(folder)[8..];
            host.Click(cancel);
            host.PumpUntil(() => !setup.NeedsFrame, TimeSpan.FromSeconds(20));
            var job = session.Connection.WorkingReviewStatusAsync(id, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal("cancelled", job.State);
            Assert.Null(job.ArtifactSha256);
            Assert.False(ButtonIn(setup, "Смотреть диапазон").IsEnabled);
            Assert.Empty(Directory.GetDirectories(files.OutputDirectory, "export-*", SearchOption.AllDirectories));
            files.AssertProject(bytes, history);
        }
        Assert.Empty(Directory.GetDirectories(files.OutputDirectory, "working-*", SearchOption.AllDirectories));
    }

    [BackendMediaFact]
    public async Task AdaptationText_RasterBindsExactChapterVersion_AndRejectsChangesBeforeSaving()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        var texts = Directory.CreateDirectory(Path.Combine(files.Files.Files.Root, "texts")).FullName;
        var chapter = Path.Combine(texts, "Глава.md");
        File.WriteAllText(chapter, "# Глава\r\nСтарый фрагмент.\r\nСвет в окне.\r\n");
        var configPath = files.Files.Base.ConfigPath;
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        config["mounts"]!["texts"] = texts;
        File.WriteAllText(configPath, config.ToJsonString());
        await using var session = await files.OpenAsync(workspace: true);
        var backend = session.Connection;
        var head = await backend.AcquireWriterAsync(await backend.OpenAsync());
        head = await backend.EditScenarioAsync(new SaveSegment(null, "Адаптация главы", "", 72, 48), head);
        var segment = head.Project.Segments.Last();
        head = await backend.EditScenarioAsync(new SaveAction(segment.Id, null, "Свет в окне", "", 24), head);
        var action = head.Project.Segments.Last().Actions.Single();
        const string reference = "@texts/Глава.md";
        var observed = await backend.ReadAdaptationTextAsync(reference, 1, 1, CancellationToken.None);
        Assert.Equal(Hash(File.ReadAllBytes(chapter)), observed.Sha256, ignoreCase: true);
        head = await backend.EditScenarioAsync(new BindAdaptationText(segment.Id, null, reference, 1, 1, observed.Sha256), head);
        var denied = await Assert.ThrowsAsync<BackendCommandException>(() =>
            backend.ReadAdaptationTextAsync("@videos/Красный.mp4", 1, 1, CancellationToken.None));
        Assert.Equal("invalid_text_reference", denied.Code);
        var before = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();

        using var host = Workspace(session);
        Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
        host.Click(ButtonIn(host.Window.Navigation, "Сценарий и материалы"));
        var scenario = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>());
        host.PumpUntil(() => !scenario.Busy);
        host.Click(ButtonIn(scenario, "Адаптация главы"));
        host.Click(ButtonIn(scenario, "Текст действия"));
        SetField(scenario, "Глава (@texts/… .md)", reference);
        SetField(scenario, "Первая строка", "2");
        SetField(scenario, "Последняя строка", "3");
        host.Click(ButtonIn(scenario, "Прочитать фрагмент"));
        host.PumpUntil(() => !scenario.Busy);
        var fragment = Assert.Single(RasterWindow<SceneView>.Walk(scenario).OfType<RichTextEditor>());
        Assert.True(fragment.Options.IsReadOnly);
        Assert.Equal("Старый фрагмент.\nСвет в окне.", fragment.Text);

        File.WriteAllText(chapter, "# Глава\nУточнённый фрагмент.\nСвет в окне.\n");
        var changed = File.ReadAllBytes(chapter);
        host.Click(ButtonIn(scenario, "Сохранить"));
        host.PumpUntil(() => !scenario.Busy);
        Assert.Contains("Исходный текст изменился", scenario.Status);
        Assert.True(scenario.HasDraft);
        files.AssertProject(before, history);

        host.Click(ButtonIn(scenario, "Прочитать фрагмент"));
        host.PumpUntil(() => !scenario.Busy);
        Assert.Equal("Уточнённый фрагмент.\nСвет в окне.", fragment.Text);
        host.Capture("adaptation-text-version", new { segment.Id, Action = action.Id });
        host.Click(ButtonIn(scenario, "Сохранить"));
        host.PumpUntil(() => !scenario.Busy && !scenario.HasDraft);
        var saved = backend.OpenAsync().GetAwaiter().GetResult();
        Assert.Equal(head.Project.Revision + 1, saved.Project.Revision);
        var notes = BackendAdaptation.Read(saved.Project);
        var binding = Assert.Single(notes.Texts, row => row.ActionId == action.Id);
        Assert.Equal((segment.Id, reference, 2, 3), (binding.SegmentId, binding.Fragment.Reference, binding.Fragment.FirstLine, binding.Fragment.LastLine));
        Assert.Equal(Hash(changed), binding.Fragment.Sha256, ignoreCase: true);
        Assert.Equal("Уточнённый фрагмент.\nСвет в окне.", binding.Fragment.Excerpt);
        Assert.Equal(observed, Assert.Single(notes.Texts, row => row.ActionId is null).Fragment);
        Assert.Contains(files.History(), row => row.SequenceEqual(before));
        Assert.Equal(changed, File.ReadAllBytes(chapter));
        var savedBytes = File.ReadAllBytes(files.ProjectPath);
        var savedHistory = files.History();

        File.WriteAllText(chapter, "# Другая версия\n");
        host.Click(ButtonIn(scenario, "Текст действия"));
        Assert.Equal(binding.Fragment.Excerpt,
            Assert.Single(RasterWindow<SceneView>.Walk(scenario).OfType<RichTextEditor>()).Text);
        files.AssertProject(savedBytes, savedHistory);
        Assert.Empty(files.Requests());
    }

    [BackendMediaFact]
    public async Task Animatic_RasterStartsWithoutAnyVideo_AndRequiresPlannedTiming()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync(scenarioOnly: true);
        await using var session = await files.OpenAsync(workspace: true);
        var connection = session.Connection;
        var head = await connection.AcquireWriterAsync(await connection.OpenAsync());
        head = await connection.EditScenarioAsync(new SaveSegment(null, "Сцена до съёмки", "", 0, 24), head);
        var segment = head.Project.Segments.Last();
        head = await connection.EditScenarioAsync(new SaveAction(segment.Id, null, "Черновой план", "", null), head);
        var action = head.Project.Segments.Last().Actions.Single();

        var missing = await Assert.ThrowsAsync<BackendCommandException>(() => connection.StartWorkingReviewAsync(Guid.NewGuid().ToString("N"),
            head.Project.Revision, head.Fingerprint, CancellationToken.None, animatic: true));
        Assert.Equal("animatic_timing", missing.Code);
        Assert.Empty(files.Requests());
        head = await connection.EditScenarioAsync(new SaveAction(segment.Id, action.Id, action.Title, "", 16), head);
        var stale = await Assert.ThrowsAsync<BackendCommandException>(() => connection.StartWorkingReviewAsync(Guid.NewGuid().ToString("N"),
            head.Project.Revision, new string('1', 64), CancellationToken.None, animatic: true));
        Assert.Equal("revision_conflict", stale.Code);
        Assert.Empty(files.Requests());
        var before = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();

        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        var animatic = ButtonIn(host.Window.Preview, "Аниматик");
        host.PumpUntil(() => animatic.IsEnabled);
        Assert.NotNull(model.EmptyProject);
        host.Click(animatic);
        var preparation = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<WorkingReviewWorkspace>());
        host.Click(ButtonIn(preparation, "Подготовить аниматик"));
        var play = ButtonIn(preparation, "Смотреть аниматик");
        host.PumpUntil(() => play.IsEnabled || !preparation.NeedsFrame, TimeSpan.FromSeconds(30));
        Assert.True(play.IsEnabled, string.Join(" · ", RasterWindow<SceneView>.Walk(preparation).OfType<Text>().Select(row => row.Value)));
        var request = JsonNode.Parse(File.ReadAllText(Assert.Single(files.Requests())))!;
        Assert.Empty(request["clips"]!.AsArray());
        var card = Assert.Single(request["titles"]!.AsArray())!;
        Assert.Equal((0, 16), ((int)card["start_frame"]!, (int)card["duration_frames"]!));
        host.Click(play);
        var player = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<RenderedReviewPanel>());
        ReviewReady(host, player);
        Assert.Contains(RasterWindow<SceneView>.Walk(player).OfType<Text>(), row => row.Value.Contains("0000 / 0023"));
        files.AssertProject(before, history);
    }

    [BackendMediaFact]
    public async Task Animatic_RasterPlaysFullTimelineWithVideoStillCardGapAndTemporarySound()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        var ffmpeg = Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!;
        await BackendTrimFixture.RunAsync(ffmpeg, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=lime:s=160x120",
            "-frames:v", "1", "-threads", "1", "-n", files.MediaPath("Опора.png")], 2048);
        byte[] bytes;
        byte[][] history;
        await using (var session = await files.OpenAsync(workspace: true))
        {
            var connection = session.Connection;
            var head = await connection.AcquireWriterAsync(await connection.OpenAsync());
            head = await connection.EditScenarioAsync(new SaveSegment(null, "Поздний сегмент", "", 72, 72), head);
            var segment = head.Project.Segments.Last();
            head = await connection.EditScenarioAsync(new SaveAction(segment.Id, null, "Эскиз", "", 24), head);
            var action = head.Project.Segments.Last().Actions.Single();
            head = await connection.EditScenarioAsync(new SaveAnimaticStill(segment.Id, action.Id, "@videos/Опора.png"), head);
            head = await connection.EditScenarioAsync(new SaveAction(segment.Id, null, "Свет в окне", "Немой план", 24), head);
            head = await connection.EditScenarioAsync(new SaveMontageSound(null, segment.Id, null, "Черновая реплика",
                "voice", "video-a", 0, 0, 48, 0, .8, false), head);
            bytes = File.ReadAllBytes(files.ProjectPath);
            history = files.History();

            using var host = Workspace(session);
            Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
            host.Click(ButtonIn(host.Window.Preview, "Аниматик"));
            var preparing = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<WorkingReviewWorkspace>());
            host.Click(ButtonIn(preparing, "Подготовить аниматик"));
            var view = ButtonIn(preparing, "Смотреть аниматик");
            host.PumpUntil(() => view.IsEnabled || !preparing.NeedsFrame, TimeSpan.FromSeconds(35));
            Assert.True(view.IsEnabled, string.Join(" · ", RasterWindow<SceneView>.Walk(preparing).OfType<Text>().Select(row => row.Value)));
            var folder = Assert.Single(Directory.GetDirectories(files.OutputDirectory, "working-*", SearchOption.AllDirectories));
            var jobId = "export-" + Path.GetFileName(folder)[8..];
            var status = connection.WorkingReviewStatusAsync(jobId, CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(status.Animatic);
            Assert.Equal((144, 24d), (status.Frames, status.Fps));
            Assert.Equal(head.Fingerprint, status.ProjectFingerprint);
            Assert.Equal(head.Project.Revision, status.ProjectRevision);
            var request = JsonNode.Parse(File.ReadAllText(Assert.Single(files.Requests())))!;
            var still = Assert.Single(request["animatic_stills"]!.AsArray())!;
            Assert.Equal((72, 24), ((int)still["start_frame"]!, (int)still["duration_frames"]!));
            Assert.Single(request["titles"]!.AsArray());
            Assert.Equal(2, request["clips"]!.AsArray().Count);

            host.Click(view);
            var player = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<RenderedReviewPanel>());
            ReviewReady(host, player);
            SetField(player, "Кадр предпросмотра", "99");
            host.Click(ButtonIn(player, "Перейти"));
            ReviewReady(host, player);
            Assert.Contains(RasterWindow<SceneView>.Walk(player).OfType<Text>(), row => row.Value.Contains("0099 / 0143"));
            host.Capture("animatic-whole-timeline", new { status.Frames, status.ProjectRevision });
            host.Click(ButtonIn(player, "К монтажу"));
            var model = session.App.Services.Resolve<SceneViewModel>()!;
            Ready(host, model);
            host.PumpUntil(() => !model.NeedsFrame);

            var key = BackendRenderedMontage.Identifier(status.Id, null);
            BackendMediaReply Frame(int frame) =>
                connection.FrameAsync(key, frame, head.Project.Revision, head.Fingerprint).GetAwaiter().GetResult();
            int center = (135 * 480 + 240) * 4;
            var red = Frame(head.Project.Montage.Clips.First(clip => clip.Id == "clip-main").StartFrame + 1).Pixels;
            var green = Frame(72).Pixels;
            Assert.InRange(red[center + 2], 200, 255);
            Assert.InRange(green[center + 1], 200, 255);
            Assert.InRange(green[center + 2], 0, 35);
            Assert.Equal(Frame(72).PixelSha256, Frame(95).PixelSha256);
            Assert.Contains(Frame(96).Pixels.Where((pixel, index) => index % 4 != 3), pixel => pixel > 120);
            foreach (var pause in new[] { 71, 120, 143 })
                Assert.All(Frame(pause).Pixels.Where((pixel, index) => index % 4 != 3), pixel => Assert.InRange(pixel, 0, 6));

            var rendered = new BackendRenderedMontage(key, 0, 144, 24, (int)head.Project.Revision, head.Fingerprint, Animatic: true);
            var audio = connection.OpenPlaybackAsync(rendered.Scene(), "audio", 0, CancellationToken.None).GetAwaiter().GetResult();
            using var data = new MemoryStream();
            try
            {
                while (true)
                {
                    var packet = connection.ReadPlaybackAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
                    Assert.Equal(data.Length / 8, packet.SampleOffset);
                    if (packet.Completed) break;
                    data.Write(packet.Data);
                }
            }
            finally { connection.ClosePlaybackAsync(audio.SessionId, CancellationToken.None).GetAwaiter().GetResult(); }
            Assert.Equal(144 * 4000 * sizeof(float), data.Length);
            var samples = MemoryMarshal.Cast<byte, float>(data.ToArray()).ToArray();
            double Rms(int frame) => Math.Sqrt(samples.Skip(frame * 4000).Take(4 * 4000).Select(value => (double)value * value).Average());
            Assert.True(Rms(100) > .01);
            Assert.InRange(Rms(130), 0, .003);
            Assert.Empty(Directory.GetDirectories(files.OutputDirectory, "export-*", SearchOption.AllDirectories));
            files.AssertProject(bytes, history);
        }
        Assert.Empty(Directory.GetDirectories(files.OutputDirectory, "working-*", SearchOption.AllDirectories));
        files.AssertProject(bytes, history);
    }

    [BackendMediaFact]
    public async Task Storyboard_RasterShowsBothCutsAndJumpsToSavedFrameOrAction()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        await BackendTrimFixture.RunAsync(Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!,
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=lime:s=160x120", "-frames:v", "1", "-threads", "1", "-n",
             files.MediaPath("Опора.png")], 2048);
        await using var session = await files.OpenAsync(workspace: true);
        var api = session.Connection;
        var snapshot = await api.AcquireWriterAsync(await api.OpenAsync());
        var existing = snapshot.Project.Montage.Clips.First(clip => clip.Id == "clip-main");
        snapshot = await api.EditScenarioAsync(new SaveSegment(null, "Ночная сцена", "", 72, 72), snapshot);
        var segment = snapshot.Project.Segments.Last();
        snapshot = await api.EditScenarioAsync(new SaveAction(segment.Id, null, "Две склейки", "Она подходит к окну.", 36), snapshot);
        var action = snapshot.Project.Segments.Last().Actions.Single();
        snapshot = await api.EditScenarioAsync(new LinkSource(segment.Id, action.Id, existing.SourceId, "Окно"), snapshot);
        snapshot = await api.EditScenarioAsync(new PlaceClip(segment.Id, action.Id, existing.SourceId, existing.TakeId!, 72, 12, 4), snapshot);
        snapshot = await api.EditScenarioAsync(new PlaceClip(segment.Id, action.Id, existing.SourceId, existing.TakeId!, 88, 12, 11), snapshot);
        var secondCut = snapshot.Project.Montage.Clips.Last();
        snapshot = await api.EditScenarioAsync(new SaveAction(segment.Id, null, "Опорный план", "Свет в комнате гаснет.", 16), snapshot);
        var still = snapshot.Project.Segments.Last().Actions.Last();
        snapshot = await api.EditScenarioAsync(new SaveAnimaticStill(segment.Id, still.Id, "@videos/Опора.png"), snapshot);
        snapshot = await api.EditScenarioAsync(new SaveAction(segment.Id, null, "Без времени", "", null), snapshot);
        var untimed = snapshot.Project.Segments.Last().Actions.Last();
        var initialHistory = files.History();
        var bytes = File.ReadAllBytes(files.ProjectPath);

        var bitmap = await api.StoryboardStillAsync(segment.Id, still.Id, snapshot.Project.Revision, snapshot.Fingerprint, CancellationToken.None);
        Assert.InRange(bitmap.Pixels[(135 * 480 + 240) * 4 + 1], 210, 255);
        var wrongAction = await Assert.ThrowsAsync<BackendCommandException>(() => api.StoryboardStillAsync(segment.Id, action.Id,
            snapshot.Project.Revision, snapshot.Fingerprint, CancellationToken.None));
        Assert.Equal("media_not_found", wrongAction.Code);

        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        Ready(host, model);
        StoryboardWorkspace OpenBoard()
        {
            host.Click(ButtonIn(host.Window.Navigation, "Раскадровка"));
            var board = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<StoryboardWorkspace>());
            host.PumpUntil(() => !board.NeedsFrame);
            return board;
        }
        void Select(StoryboardWorkspace board, string label)
        {
            var button = ButtonIn(board, label);
            for (var node = button.Parent; node is not null; node = node.Parent)
                if (node is ScrollViewer scroll) { scroll.BringIntoView(button.Bounds); host.Update(); break; }
            host.Click(button);
            host.PumpUntil(() => !board.NeedsFrame);
        }
        var board = OpenBoard();
        Assert.True(ButtonIn(board, "0072 · Две склейки").Bounds.Y < ButtonIn(board, "0088 · Две склейки").Bounds.Y);
        Select(board, "0088 · Две склейки");
        Assert.Contains(RasterWindow<SceneView>.Walk(board).OfType<Image>(), picture => picture.AccessibleName == "Видео · Две склейки" && picture.Source is not null);
        host.Click(ButtonIn(board, "К действию"));
        host.PumpUntil(() => RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>().Any());
        var scenario = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>());
        host.PumpUntil(() => ButtonIn(scenario, action.Title).IsFocused);
        Assert.Equal(ControlTone.Accent, Assert.Single(RasterWindow<SceneView>.Walk(scenario).OfType<Button>(),
            button => button.Value.StartsWith("88 · ") && button.Value.EndsWith("12 к.")).Tone);
        host.Click(ButtonIn(scenario, "К монтажу"));
        Ready(host, model);
        board = OpenBoard();
        Select(board, "0088 · Две склейки");
        host.Click(ButtonIn(board, "К кадру"));
        host.PumpUntil(() => model.Scene?.Clip.Id == secondCut.Id && model.Idle);
        Assert.Equal((88d, 11), (model.TimelineCursor, model.FrameIndex));
        Assert.Equal(snapshot.Fingerprint, model.Scene!.Fingerprint);

        board = OpenBoard();
        Select(board, "0100 · Опорный план");
        Assert.Contains(RasterWindow<SceneView>.Walk(board).OfType<Image>(), picture => picture.AccessibleName == "Стоп-кадр · Опорный план" && picture.Source is not null);
        host.Capture("storyboard-still", new { snapshot.Project.Revision, Frame = 100 });
        host.Resize(840, 660, 1.5f);
        host.Capture("storyboard-compact", new { snapshot.Project.Revision, Frame = 100 });
        host.Resize(1280, 820, 1);
        host.Click(ButtonIn(board, "К кадру"));
        host.PumpUntil(() => model.StoryboardPlan is not null);
        Assert.Equal((100d, 16), (model.TimelineCursor, model.StoryboardPlan!.Duration));
        var strip = Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Timeline).OfType<SceneStrip>());
        Assert.Contains("Опорный план", strip.AccessibleName);
        host.Capture("storyboard-plan-on-timeline", new { Frame = model.TimelineCursor, model.StoryboardPlan.Duration });

        board = OpenBoard();
        Select(board, "— · Без времени");
        Assert.False(ButtonIn(board, "К кадру").IsEnabled);
        host.Click(ButtonIn(board, "К действию"));
        host.PumpUntil(() => RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>().Any());
        scenario = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>());
        Assert.Equal(ControlTone.Accent, ButtonIn(scenario, untimed.Title).Tone);
        Assert.Equal(ControlTone.Accent, ButtonIn(scenario, segment.Name).Tone);
        files.AssertProject(bytes, initialHistory);
        Assert.Empty(files.Requests());

        host.Click(ButtonIn(scenario, "К монтажу"));
        Ready(host, model);
        board = OpenBoard();
        Select(board, "0100 · Опорный план");
        snapshot = api.EditScenarioAsync(new SaveAction(segment.Id, still.Id, "Обновлённая опора", still.Description, 16), snapshot).GetAwaiter().GetResult();
        bytes = File.ReadAllBytes(files.ProjectPath);
        var updatedHistory = files.History();
        host.Click(ButtonIn(board, "К кадру"));
        host.PumpUntil(() => RasterWindow<SceneView>.Walk(board).OfType<Text>().Any(row => row.Value.Contains("Выберите план заново")));
        Assert.False(ButtonIn(board, "К кадру").IsEnabled);
        Assert.True(ButtonIn(board, "0100 · Обновлённая опора").IsEnabled);
        Assert.Null(model.StoryboardPlan);
        files.AssertProject(bytes, updatedHistory);
    }

    [BackendMediaFact]
    public async Task TimelineReadiness_ListsBothProblems_AndRefreshesAfterRelink()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        File.Move(files.MediaPath("Синий.mp4"), files.MediaPath("Исправленный.mp4"));
        var store = files.Files.Files.Store();
        var original = await store.OpenAsync();
        var main = original.Project.Montage.Clips.Single(clip => clip.Id == "clip-main");
        var extra = original.Project.Montage.Clips.Single(clip => clip.Id == "clip-extra");
        var saved = await store.SaveAsync(original, original.Project with
        {
            Montage = original.Project.Montage with
            {
                Clips = original.Project.Montage.Clips.Select(clip => clip.Id == main.Id
                    ? clip with { TakeId = null, SourceOutFrame = null, SourceInFrame = 0 }
                    : clip).ToArray()
            }
        }, original.Project.Revision, "timeline-readiness-fixture");
        var before = File.ReadAllBytes(files.ProjectPath);
        var originalHistory = files.History();

        await using var session = await files.OpenAsync(workspace: true);
        var api = session.Connection;
        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        host.PumpUntil(() => model.ProjectReady && model.Idle || model.HasError);
        Assert.False(model.HasError, model.Status);
        host.Click(ButtonIn(host.Window.Timeline, "Проблемы"));
        var panel = Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Timeline).OfType<TimelineReadiness>());
        host.PumpUntil(() => HasText(panel, "Проблем: 2"));
        Assert.Contains(RasterWindow<SceneView>.Walk(panel).OfType<Text>(), row => row.Value.Contains("Клип без видеодубля"));
        Assert.Contains(RasterWindow<SceneView>.Walk(panel).OfType<Text>(), row => row.Value.Contains("Файл материала не найден"));
        var strip = Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Timeline).OfType<SceneStrip>());
        Assert.True(strip.Bounds.X + strip.Bounds.Width <= panel.Bounds.X);
        host.Capture("timeline-two-problems", new { saved.Project.Revision });

        host.Click(ButtonIn(AtFrame(panel, main.StartFrame), "К клипу"));
        host.PumpUntil(() => model.Scene?.Clip.Id == main.Id && model.Idle);
        Assert.Equal(saved.Fingerprint, model.Scene!.Fingerprint);
        files.AssertProject(before, originalHistory);

        var jump = ButtonIn(AtFrame(panel, extra.StartFrame), "К ассету");
        for (var node = jump.Parent; node is not null; node = node.Parent)
            if (node is ScrollViewer scroll) { scroll.BringIntoView(jump.Bounds); host.Update(); }
        host.Click(jump);
        var library = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>());
        host.PumpUntil(() => !library.Busy);
        host.Click(ButtonIn(library, "Перепривязать файл"));
        SetField(library, "Путь или ссылка на видео", "@videos/Исправленный.mp4");
        host.Click(ButtonIn(library, "Сохранить"));
        host.PumpUntil(() => !library.Busy && !library.HasDraft);
        var repaired = api.OpenAsync().GetAwaiter().GetResult();
        Assert.Equal(saved.Project.Revision + 1, repaired.Project.Revision);
        var take = repaired.Project.Sources.Single(source => source.Id == extra.SourceId).Takes.Single(take => take.Id == extra.TakeId);
        Assert.Equal("@videos/Исправленный.mp4", BackendScenarioEdit.TryReference(repaired.Project, take.VideoAssetId!));
        Assert.Contains(files.History(), row => row.SequenceEqual(before));
        host.Click(ButtonIn(library, "К монтажу"));
        host.PumpUntil(() => HasText(panel, $"Проблем: 1 · рев. {repaired.Project.Revision}"));
        Assert.DoesNotContain(RasterWindow<SceneView>.Walk(panel).OfType<Button>(), button => button.Value == "К ассету");
        Assert.True(ButtonIn(AtFrame(panel, main.StartFrame), "К клипу").IsEnabled);

        var ready = api.EditScenarioAsync(new AssignTake(main.Id, main.TakeId!), repaired).GetAwaiter().GetResult();
        var finalBytes = File.ReadAllBytes(files.ProjectPath);
        var finalHistory = files.History();
        host.Click(ButtonIn(panel, "Обновить"));
        host.PumpUntil(() => HasText(panel, $"Проблем нет · рев. {ready.Project.Revision}"));
        Assert.DoesNotContain(RasterWindow<SceneView>.Walk(panel).OfType<Button>(), button => button.Value is "К клипу" or "К ассету");
        Assert.Empty(files.Requests());
        files.AssertProject(finalBytes, finalHistory);

        static Element AtFrame(Element panel, int frame) =>
            Assert.Single(RasterWindow<SceneView>.Walk(panel).OfType<VStack>(), row => row.AccessibleName.StartsWith($"Кадр {frame} ·", StringComparison.Ordinal));
        static bool HasText(Element panel, string value) =>
            RasterWindow<SceneView>.Walk(panel).OfType<Text>().Any(row => row.Value.Contains(value, StringComparison.Ordinal));
    }

    [BackendMediaFact]
    public async Task TimelineReadiness_ReportsTotalWithBoundedRows_AndChecksTheSavedSnapshot()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        var store = files.Files.Files.Store();
        var original = await store.OpenAsync();
        var montage = original.Project.Montage;
        var first = montage.Clips.First();
        var saved = await store.SaveAsync(original, original.Project with
        {
            Montage = montage with
            {
                Clips = Enumerable.Range(0, 30).Select(frame => first with
                {
                    Id = $"problem-{frame}", ActionId = null, TakeId = null, StartFrame = frame,
                    DurationFrames = 1, SourceInFrame = 0, SourceOutFrame = null, AnchorFrame = null
                }).ToArray()
            }
        }, original.Project.Revision, "timeline-readiness-many");
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        await using var session = await files.OpenAsync();
        var api = session.Connection;
        var report = await api.CheckReadinessAsync(saved.Project.Revision, saved.Fingerprint, CancellationToken.None);
        Assert.Equal(30, report.Total);
        Assert.Equal(24, report.Issues.Length);
        Assert.Equal(Enumerable.Range(0, 24).Select(frame => (int?)frame), report.Issues.Select(row => row.Frame));
        Assert.All(report.Issues, row => Assert.Equal("clip_take_missing", row.Reason));
        Assert.Equal("revision_conflict", (await Assert.ThrowsAsync<BackendCommandException>(() =>
            api.CheckReadinessAsync(saved.Project.Revision, original.Fingerprint, CancellationToken.None))).Code);
        Assert.Empty(files.Requests());
        files.AssertProject(bytes, history);
    }

    [BackendMediaFact]
    public async Task Readiness_Placeholder_FromExport_SelectsTheClipWithoutWriting()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        var store = files.Files.Files.Store();
        var original = await store.OpenAsync();
        var project = original.Project with
        {
            Montage = original.Project.Montage with
            {
                Clips = original.Project.Montage.Clips.Select(clip => clip with { TakeId = null, SourceInFrame = 0, SourceOutFrame = null }).ToArray()
            }
        };
        var saved = await store.SaveAsync(original, project, original.Project.Revision, "diagnostic-fixture");
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        await using var session = await files.OpenAsync(workspace: true);
        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        host.PumpUntil(() => model.EmptyProject is not null || model.HasError);
        Assert.False(model.HasError, model.Status);

        var panel = CheckReadiness(host);
        Assert.Contains(RasterWindow<SceneView>.Walk(panel).OfType<Text>(), row => row.Value.Contains("Клип без видеодубля"));
        Assert.False(ButtonIn(panel, "Запустить экспорт").IsEnabled);
        Assert.False(ButtonIn(panel, "К ассету").IsVisible);
        host.Click(ButtonIn(panel, "К клипу"));
        host.PumpUntil(() => model.Scene?.Clip.Id == "clip-main" && model.Idle || model.HasError);
        Assert.False(model.HasError, model.Status);
        Assert.Null(model.Scene!.Clip.TakeId);
        Assert.Equal(saved.Fingerprint, model.Scene.Fingerprint);
        Assert.Null(model.Pixels);
        Assert.Null(host.Window.Overlays.Popup);
        Assert.Empty(files.Requests());
        files.AssertProject(bytes, history);
    }

    [BackendMediaFact]
    public async Task Readiness_MissingVideo_FromRange_OpensTheExactLibraryTake()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        File.Move(files.MediaPath("Синий.mp4"), files.MediaPath("Синий.fixture"));
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        await using var session = await files.OpenAsync(workspace: true);
        var opened = await session.Connection.OpenAsync();
        var clip = opened.Project.Montage.Clips.Single(row => row.Id == "clip-extra");
        var source = opened.Project.Sources.Single(row => row.Id == clip.SourceId);
        var take = source.Takes.Single(row => row.Id == clip.TakeId);
        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        Ready(host, model);
        var export = CheckReadiness(host);
        host.Click(ButtonIn(export, "К клипу"));
        host.PumpUntil(() => model.Scene?.Clip.Id == clip.Id && model.Idle || model.HasError);
        Assert.False(model.HasError, model.Status);
        Assert.Null(model.Pixels);
        Assert.Equal("Видеофайл не найден", model.MediaStatus);

        host.Click(ButtonIn(host.Window.Preview, "Рабочий просмотр"));
        var review = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<WorkingReviewWorkspace>());
        host.Click(ButtonIn(review, "Подготовить диапазон"));
        var asset = ButtonIn(review, "К ассету");
        host.PumpUntil(() => asset.IsEnabled);
        Assert.Contains(RasterWindow<SceneView>.Walk(review).OfType<Text>(), row => row.Value.Contains("Файл материала не найден"));
        host.Capture("readiness-missing-video", new { clip.Id, take.VideoAssetId });
        host.Click(asset);
        var library = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>());
        host.PumpUntil(() => !library.Busy);
        Assert.Contains(RasterWindow<SceneView>.Walk(library).OfType<TextBox>(), row =>
            row.AccessibleName == "Поиск источника" && row.Value == source.Title);
        Assert.Contains(RasterWindow<SceneView>.Walk(library).OfType<Button>(), row =>
            row.Value == BackendScenarioEdit.TakeName(take) && row.Tone == ControlTone.Accent);
        Assert.Contains(RasterWindow<SceneView>.Walk(library).OfType<Text>(), row => row.Value.Contains("Синий.mp4"));
        Assert.True(ButtonIn(library, "Перепривязать файл").IsEnabled);
        File.Move(files.MediaPath("Синий.fixture"), files.MediaPath("Синий.mp4"));
        host.Click(ButtonIn(library, "К монтажу"));
        Ready(host, model);
        host.Click(ButtonIn(host.Window.Heading, "Экспорт MP4"));
        var repaired = WaitForExport(host);
        var check = ButtonIn(repaired, "Проверить монтаж");
        host.PumpUntil(() => check.IsEnabled);
        host.Click(check);
        host.PumpUntil(() => ButtonIn(repaired, "Запустить экспорт").IsEnabled);
        Assert.False(ButtonIn(repaired, "К ассету").IsVisible);
        Assert.Empty(files.Requests());
        files.AssertProject(bytes, history);
    }

    [BackendMediaFact]
    public async Task Readiness_MissingMusic_OpensTheTrack_AndRejectsAStaleLink()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        File.Copy(files.MediaPath("Красный.mp4"), files.MediaPath("Музыка.mp4"));
        await using var session = await files.OpenAsync(workspace: true);
        var connection = session.Connection;
        var initial = await connection.AcquireWriterAsync(await connection.OpenAsync());
        var head = await connection.EditScenarioAsync(new SaveMontageMusic("@videos/Музыка.mp4", 0, 24, 0, .4, false), initial);
        var assetId = head.Project.Montage.Properties["music"].GetProperty("asset_id").GetString();
        File.Move(files.MediaPath("Музыка.mp4"), files.MediaPath("Музыка.fixture"));
        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        Ready(host, model);
        var panel = CheckReadiness(host);
        Assert.False(ButtonIn(panel, "К клипу").IsVisible);
        host.Click(ButtonIn(panel, "К ассету"));
        var tracks = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<MontageTrackWorkspace>());
        host.PumpUntil(() => !tracks.NeedsFrame);
        Assert.Contains(RasterWindow<SceneView>.Walk(tracks).OfType<TextBox>(), row =>
            row.AccessibleName == "Медиа (ID ассета или @mount/путь)" && row.Value == assetId);
        Assert.False(tracks.HasDraft);
        host.Click(ButtonIn(tracks, "К монтажу"));
        Ready(host, model);
        var outdated = CheckReadiness(host);
        var latest = connection.EditScenarioAsync(new SaveMontageMarker(null, "Новая ревизия", 8), head).GetAwaiter().GetResult();
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        host.Click(ButtonIn(outdated, "К ассету"));
        var staleTracks = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<MontageTrackWorkspace>());
        host.PumpUntil(() => !staleTracks.NeedsFrame);
        Assert.Contains(RasterWindow<SceneView>.Walk(staleTracks).OfType<Text>(), row => row.Value.Contains("Проверьте готовность снова"));
        Assert.DoesNotContain(RasterWindow<SceneView>.Walk(staleTracks).OfType<TextBox>(), row => row.Value == assetId);
        Assert.Equal(latest.Fingerprint, connection.OpenAsync().GetAwaiter().GetResult().Fingerprint);
        Assert.Empty(files.Requests());
        files.AssertProject(bytes, history);
    }

    [BackendMediaFact]
    public async Task Readiness_DisabledLegacyOverlap_ShowsConfigurationCauseWithoutAnEntityLink()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        var configPath = files.Files.Base.ConfigPath;
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        config["legacy_read_enabled"] = false;
        config["legacy_root"] = files.MediaPath("disabled");
        File.WriteAllText(configPath, config.ToJsonString());
        await using var session = await files.OpenAsync(workspace: true);
        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        host.PumpUntil(() => model.ProjectReady && model.Idle || model.HasError);
        Assert.False(model.HasError, model.Status);
        var panel = CheckReadiness(host);
        Assert.Contains(RasterWindow<SceneView>.Walk(panel).OfType<Text>(), row =>
            row.Value.Contains("legacy_root пересекается с корнем чтения"));
        Assert.False(ButtonIn(panel, "К клипу").IsVisible);
        Assert.False(ButtonIn(panel, "К ассету").IsVisible);
        Assert.Empty(files.Requests());
    }

    private static SceneExportPanel CheckReadiness(RasterWindow<SceneView> host)
    {
        host.Click(ButtonIn(host.Window.Heading, "Экспорт MP4"));
        var panel = WaitForExport(host);
        var check = ButtonIn(panel, "Проверить монтаж");
        host.PumpUntil(() => check.IsEnabled);
        host.Click(check);
        host.PumpUntil(() => RasterWindow<SceneView>.Walk(panel).OfType<Text>().Any(row => row.HasError));
        return panel;
    }

    [BackendMediaFact]
    public async Task TrackSplit_ThroughWriter_PreservesSoundAndUndoRestoresItsExactPlacement()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        await using var session = await files.OpenAsync();
        var backend = session.Connection;
        var initial = await backend.AcquireWriterAsync(await backend.OpenAsync());
        var refused = await Assert.ThrowsAsync<BackendCommandException>(() =>
            backend.EditScenarioAsync(new SaveMontageSound(null, "segment-main", "clip-main", "Звук", "voice",
                "video-a", 0, 4, 13, 40, .4, false), initial));
        Assert.Equal("montage_audio_source", refused.Code);
        var before = await backend.EditScenarioAsync(new SaveMontageSound(null, "segment-main", "clip-main", "Звук", "voice",
            "video-a", 0, 4, 13, 2, .4, false), initial);
        var beforeBytes = File.ReadAllBytes(files.ProjectPath);
        var split = await backend.EditScenarioAsync(new SplitMontageClip("clip-main", 24), before);
        var sound = split.Project.Montage.Properties["sound_events"].EnumerateArray().ToArray();
        var right = split.Project.Montage.Clips.Single(clip => clip.StartFrame == 24);
        Assert.Equal(2, sound.Length);
        Assert.Equal(8, sound[0].GetProperty("duration_frames").GetInt32());
        Assert.Equal(5, sound[1].GetProperty("duration_frames").GetInt32());
        Assert.Equal(10d, sound[1].GetProperty("source_in_frame").GetDouble());
        Assert.Equal(right.Id, sound[1].GetProperty("clip_id").GetString());
        Assert.All(sound, row => Assert.Equal(.4, row.GetProperty("gain").GetDouble()));
        Assert.Contains(files.History(), row => row.SequenceEqual(beforeBytes));
        var undone = await backend.EditScenarioAsync(new UndoMontage(), split);
        Assert.Equal(split.Project.Revision + 1, undone.Project.Revision);
        Assert.Equal(before.Project.Montage.Properties["sound_events"].GetRawText(), undone.Project.Montage.Properties["sound_events"].GetRawText());
        var redone = await backend.EditScenarioAsync(new RedoMontage(), undone);
        Assert.Equal(split.Project.Montage.Properties["sound_events"].GetRawText(), redone.Project.Montage.Properties["sound_events"].GetRawText());
    }

    [BackendMediaFact]
    public async Task Montage_FromRaster_EditsAndReviewsVerifiedProjectAndSegment()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        await using var session = await files.OpenAsync(workspace: true);
        var backend = session.Connection;
        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        Ready(host, model);
        var initial = backend.OpenAsync().GetAwaiter().GetResult();
        var initialBytes = File.ReadAllBytes(files.ProjectPath);
        var initialHistory = files.History();
        Assert.Equal("clip-main", model.Scene!.Clip.Id);
        Assert.Contains(24, model.Scene.MarkerFrames!);

        model.SeekTimeline(24);
        host.PumpUntil(() => model.Idle && model.SplitFrame == 24);
        ClickInspector(host, "Разрезать по курсору");
        host.PumpUntil(() => model.Idle && model.Scene?.Revision == 13 || model.HasError);
        Ready(host, model);
        var split = backend.OpenAsync().GetAwaiter().GetResult();
        var halves = split.Project.Montage.Clips.Where(row => row.WindowId == model.Scene!.Segment.Id).ToArray();
        Assert.Collection(halves,
            left => { Assert.Equal("clip-main", left.Id); Assert.Equal(12, left.DurationFrames); Assert.Equal(12d, left.SourceOutFrame); },
            right => { Assert.Equal(24, right.StartFrame); Assert.Equal(12, right.DurationFrames); Assert.Equal(12d, right.SourceInFrame); });
        var right = halves[1];
        Assert.Equal(halves[0].TakeId, right.TakeId);
        Assert.Equal(halves[0].ActionId, right.ActionId);
        Assert.Equal(initial.Project.Segments.SelectMany(row => row.Actions).Select(row => row.Id),
            split.Project.Segments.SelectMany(row => row.Actions).Select(row => row.Id));
        Assert.Contains(files.History(), row => row.SequenceEqual(initialBytes));
        Assert.Equal("revision_conflict", Assert.Throws<BackendCommandException>(() =>
            backend.EditScenarioAsync(new UndoMontage(), initial).GetAwaiter().GetResult()).Code);
        Assert.Equal(initialHistory.Length + 1, files.History().Length);

        ClickInspector(host, "Undo");
        host.PumpUntil(() => model.Idle && model.Scene?.Revision == 14 || model.HasError);
        Ready(host, model);
        Assert.Equal(24, model.Scene!.Clip.DurationFrames);
        ClickInspector(host, "Redo");
        host.PumpUntil(() => model.Idle && model.Scene?.Revision == 15 || model.HasError);
        Ready(host, model);
        Assert.Equal(12, model.Scene!.Clip.DurationFrames);

        model.SelectClip(right.Id);
        host.PumpUntil(() => model.Idle && model.Scene?.Clip.Id == right.Id);
        ClickInspector(host, "Удалить клип…");
        host.Input(new(InputKind.KeyDown, Key: UiKey.Escape));
        Assert.Equal(15, model.Scene!.Revision);
        ClickInspector(host, "Удалить клип…");
        ClickInspector(host, "Подтвердить удаление");
        host.PumpUntil(() => model.Idle && model.Scene?.Revision == 16 || model.HasError);
        Ready(host, model);
        Assert.DoesNotContain(backend.OpenAsync().GetAwaiter().GetResult().Project.Montage.Clips, clip => clip.Id == right.Id);
        ClickInspector(host, "Undo");
        host.PumpUntil(() => model.Idle && model.Scene?.Revision == 17 || model.HasError);
        Ready(host, model);
        model.SelectClip(right.Id);
        host.PumpUntil(() => model.Idle && model.Scene?.Clip.Id == right.Id || model.HasError);
        Assert.False(model.HasError, model.Status);

        SetField(host.Window.Inspector, "Затемнение, кадры", "4");
        SetField(host.Window.Inspector, "Чёрная пауза, кадры", "4");
        SetField(host.Window.Inspector, "Появление, кадры", "4");
        ClickInspector(host, "Сохранить переход");
        host.PumpUntil(() => model.Idle && model.Scene?.Revision == 18 || model.HasError);
        Ready(host, model);
        var rendered = backend.OpenAsync().GetAwaiter().GetResult();
        var join = Assert.Single(BackendBlackTransitions.Compile(rendered.Project.Montage));
        Assert.Equal((32, 36, 40, 44), (join.OutStartFrame, join.BlackStartFrame, join.RightStartFrame, join.InEndFrame));
        Assert.Equal(initialHistory.Length + 6, files.History().Length);
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        Assert.Equal("montage_transition_conflict", Assert.Throws<BackendCommandException>(() =>
            backend.EditScenarioAsync(new SetBlackTransition(right.Id, "clip-extra", 4, 3, 4), rendered).GetAwaiter().GetResult()).Code);
        var unchanged = backend.EditScenarioAsync(new SetBlackTransition(right.Id, "clip-extra", 4, 4, 4), rendered).GetAwaiter().GetResult();
        Assert.Equal(rendered.Fingerprint, unchanged.Fingerprint);

        model.SelectClip("clip-extra");
        host.PumpUntil(() => model.Idle && model.Scene?.Clip.Id == "clip-extra");
        var entry = ButtonIn(host.Window.Heading, "Экспорт MP4");
        host.Click(entry);
        var panel = WaitForExport(host);
        var check = ButtonIn(panel, "Проверить монтаж");
        host.PumpUntil(() => check.IsEnabled);
        host.Click(check);
        var start = ButtonIn(panel, "Запустить экспорт");
        host.PumpUntil(() => start.IsEnabled, TimeSpan.FromSeconds(16));
        host.Click(start);
        var reviewSegment = ButtonIn(panel, "Смотреть сегмент");
        host.PumpUntil(() => reviewSegment.IsEnabled, TimeSpan.FromSeconds(30));
        var job = backend.FindExportAsync(File.ReadAllText(files.Checkpoint()), CancellationToken.None).GetAwaiter().GetResult();
        var artifactPath = Path.Combine(files.JobDirectory(job.Id), "Draft.mp4");
        var artifact = File.ReadAllBytes(artifactPath);
        Assert.Equal(job.ArtifactSha256, Hash(artifact), StringComparer.OrdinalIgnoreCase);

        host.Click(reviewSegment);
        var review = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<RenderedReviewPanel>());
        ReviewReady(host, review);
        var seek = ButtonIn(review, "Перейти");
        SetField(review, "Кадр предпросмотра", "12");
        host.Click(seek);
        ReviewReady(host, review);
        Assert.Contains(RasterWindow<SceneView>.Walk(review).OfType<Text>(), text => text.Value.Contains("0012 / 0035"));
        host.Capture("montage-segment-review", new { job.ProjectRevision, Frame = 12 });
        host.Click(ButtonIn(review, "К монтажу"));
        Assert.Empty(RasterWindow<SceneView>.Walk(host.Window).OfType<RenderedReviewPanel>());
        Ready(host, model);
        host.Click(ButtonIn(host.Window.Heading, "Экспорт MP4"));
        var reviewProject = ButtonIn(WaitForExport(host), "Смотреть проект");
        host.PumpUntil(() => reviewProject.IsEnabled);
        host.Click(reviewProject);
        review = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<RenderedReviewPanel>());
        ReviewReady(host, review);
        host.Click(ButtonIn(review, "К монтажу"));
        Ready(host, model);
        host.PumpUntil(() => !model.NeedsFrame);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var segment = BackendRenderedMontage.Identifier(job.Id, 36);
        var project = BackendRenderedMontage.Identifier(job.Id, null);
        BackendMediaReply Read(string id, int frame) =>
            backend.FrameAsync(id, frame, rendered.Project.Revision, rendered.Fingerprint, token).GetAwaiter().GetResult();
        Assert.Equal(Read(project, 48).PixelSha256, Read(segment, 12).PixelSha256);
        byte Channel(BackendMediaReply frame, int channel) => frame.Pixels[(135 * 480 + 240) * 4 + channel];
        Assert.InRange(Channel(Read(project, 30), 2), 230, 255);
        Assert.InRange(Channel(Read(project, 35), 2), 10, 55);
        Assert.InRange(Channel(Read(segment, 0), 0), 0, 8);
        Assert.InRange(Channel(Read(segment, 4), 0), 10, 55);
        Assert.InRange(Channel(Read(segment, 12), 0), 230, 255);

        var source = new BackendRenderedMontage(segment, 36, 36, 24, 18, rendered.Fingerprint).Scene();
        var video = backend.OpenPlaybackAsync(source, "video", 12, token).GetAwaiter().GetResult();
        try
        {
            var streamed = backend.ReadPlaybackAsync(video, token).GetAwaiter().GetResult();
            Assert.Equal(12, streamed.LocalFrame);
            Assert.Equal(12, streamed.NativeFrame);
            Assert.Equal(Read(project, 48).PixelSha256, streamed.DataSha256);
        }
        finally { backend.ClosePlaybackAsync(video.SessionId, CancellationToken.None).GetAwaiter().GetResult(); }
        var opened = backend.OpenPlaybackAsync(source, "audio", 0, token).GetAwaiter().GetResult();
        try
        {
            Assert.Equal(72_000, opened.TotalSamples);
            var sound = new List<float>();
            BackendPlaybackReply next;
            do
            {
                next = backend.ReadPlaybackAsync(opened, token).GetAwaiter().GetResult();
                Assert.Equal(sound.Count / 2, next.SampleOffset);
                sound.AddRange(MemoryMarshal.Cast<byte, float>(next.Data).ToArray());
            } while (!next.Completed);
            Assert.InRange(sound.Take(4000).Max(Math.Abs), 0, .003f);
            Assert.InRange(sound.Skip(20_000).Take(20_000).Max(Math.Abs), .04f, .20f);
        }
        finally { backend.ClosePlaybackAsync(opened.SessionId, CancellationToken.None).GetAwaiter().GetResult(); }
        artifact[0] ^= 1;
        File.WriteAllBytes(artifactPath, artifact);
        Assert.Equal("invalid_export_artifact", Assert.Throws<BackendCommandException>(() => Read(segment, 12)).Code);
        files.AssertProject(bytes, history);
        Assert.Single(files.Requests());
    }

    private static void SetField(Element root, string name, string value) =>
        Assert.Single(RasterWindow<SceneView>.Walk(root).OfType<TextBox>(), field => field.AccessibleName == name).Value = value;

    private static void ReviewReady(RasterWindow<SceneView> host, RenderedReviewPanel review)
    {
        host.PumpUntil(() => !review.NeedsFrame, TimeSpan.FromSeconds(15));
        Assert.True(ButtonIn(review, "Старт").IsEnabled,
            string.Join(" · ", RasterWindow<SceneView>.Walk(review).OfType<Text>().Select(row => row.Value)));
    }

    private static void ClickInspector(RasterWindow<SceneView> host, string name)
    {
        var button = ButtonIn(host.Window.Inspector, name);
        for (var node = button.Parent; node is not null; node = node.Parent)
            if (node is ScrollViewer scroll) { scroll.BringIntoView(button.Bounds); host.Update(); }
        host.Click(button);
    }

    [BackendMediaFact]
    public async Task Export_FromWorkspace_SavesCheckedMp4_AndReopensExistingJobByKey()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        byte[] saved;
        byte[][] history;
        string key;
        string id;
        string sha;
        var output = Path.Combine(files.OutputDirectory, "montage.mp4");
        await using (var session = await files.OpenAsync(workspace: true))
        {
            using var host = Workspace(session);
            var model = session.App.Services.Resolve<SceneViewModel>()!;
            Ready(host, model);
            var timeline = host.Window.Timeline;
            Assert.True(timeline.Bounds.Width > host.Window.Preview.Bounds.Width);
            host.Click(ButtonIn(host.Window.Preview, "След. кадр"));
            Ready(host, model);
            var cursor = model.TimelineCursor;
            host.Click(ButtonIn(host.Window.Preview, "Исходник"));
            host.PumpUntil(() => model.ViewingSource && model.Idle);
            Assert.Equal(cursor, model.TimelineCursor);
            host.Click(ButtonIn(host.Window.Preview, "Монтаж"));
            host.PumpUntil(() => !model.ViewingSource && model.Idle);
            Assert.Equal(cursor, model.TimelineCursor);

            var before = File.ReadAllBytes(files.ProjectPath);
            host.Click(ButtonIn(host.Window.Inspector, "Начало по кадру"));
            Assert.True(model.Modified);
            host.Click(ButtonIn(timeline, "Компактно"));
            Assert.True(model.Modified);
            Assert.Equal(host.Window.Preview.Bounds.Width, timeline.Bounds.Width);
            host.Resize(880, 640, 1.5f);
            host.Window.ApplyTheme(Theme.Light);
            host.Update();
            Assert.Same(timeline, host.Window.Timeline);
            Assert.True(model.Modified);
            Assert.Equal(before, File.ReadAllBytes(files.ProjectPath));

            var entry = ButtonIn(host.Window.Heading, "Экспорт MP4");
            host.Click(entry);
            var panel = WaitForExport(host);
            Assert.False(ButtonIn(panel, "Проверить монтаж").IsEnabled);
            Assert.Empty(files.Requests());
            host.Input(new(InputKind.KeyDown, Key: UiKey.Escape));
            Assert.Null(host.Window.Overlays.Popup);
            Assert.True(entry.IsFocused);

            host.Click(host.Window.SaveButton);
            host.PumpUntil(() => !model.Modified && model.Scene!.Revision == 13 || model.HasError, TimeSpan.FromSeconds(15));
            Assert.False(model.HasError, model.Status);
            saved = File.ReadAllBytes(files.ProjectPath);
            history = files.History();
            Assert.Equal(2, history.Length);
            Assert.Equal("revision_conflict", Assert.Throws<BackendCommandException>(() =>
                session.Connection.CheckExportAsync("project", 12, Hash(before), CancellationToken.None).GetAwaiter().GetResult()).Code);
            Assert.Equal("export_not_found", Assert.Throws<BackendCommandException>(() =>
                session.Connection.FindExportAsync("missing-workspace-job", CancellationToken.None).GetAwaiter().GetResult()).Code);
            Assert.Empty(files.Requests());
            host.Click(entry);
            panel = WaitForExport(host);
            var check = ButtonIn(panel, "Проверить монтаж");
            host.PumpUntil(() => check.IsEnabled);
            host.Click(check);
            var start = ButtonIn(panel, "Запустить экспорт");
            host.PumpUntil(() => start.IsEnabled, TimeSpan.FromSeconds(16));
            Assert.Contains(RasterWindow<SceneView>.Walk(panel).OfType<Text>(), row =>
                row.Value.Contains("160 × 120", StringComparison.Ordinal) && row.Value.Contains("рев. 13", StringComparison.Ordinal));
            Assert.Empty(files.Requests());
            host.Capture("export-checked", new { model.Scene!.Revision });
            host.Click(start);
            var save = ButtonIn(panel, "Сохранить MP4");
            host.PumpUntil(() => save.IsEnabled, TimeSpan.FromSeconds(30));
            key = File.ReadAllText(files.Checkpoint());
            var job = session.Connection.FindExportAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            id = job.Id;
            sha = Assert.IsType<string>(job.ArtifactSha256);
            Assert.Equal(13, job.ProjectRevision);
            Assert.Single(files.Requests());
            host.Click(save);
            host.PumpUntil(() => File.Exists(output) && save.IsEnabled, TimeSpan.FromSeconds(16));
            Assert.Equal(sha, Hash(File.ReadAllBytes(output)), StringComparer.OrdinalIgnoreCase);
            host.Click(save);
            host.PumpUntil(() => RasterWindow<SceneView>.Walk(panel).OfType<Text>().Any(row =>
                row.Value.Contains("уже существует", StringComparison.Ordinal)));
            Assert.Equal(sha, Hash(File.ReadAllBytes(output)), StringComparer.OrdinalIgnoreCase);
            files.AssertProject(saved, history);
        }

        await using (var session = await files.OpenAsync(workspace: true))
        {
            using var host = Workspace(session);
            Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
            host.Click(ButtonIn(host.Window.Heading, "Экспорт MP4"));
            var panel = WaitForExport(host);
            var recover = ButtonIn(panel, "Восстановить");
            host.PumpUntil(() => recover.IsEnabled, TimeSpan.FromSeconds(15));
            host.Click(recover);
            host.PumpUntil(() => ButtonIn(panel, "Сохранить MP4").IsEnabled, TimeSpan.FromSeconds(15));
            var job = session.Connection.FindExportAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(id, job.Id);
            Assert.Equal(sha, job.ArtifactSha256);
            Assert.Single(files.Requests());
            files.AssertProject(saved, history);
        }
    }

    [BackendMediaFact]
    public async Task Export_ActiveWorkspaceJobCancels_AndReopenDoesNotSubmit()
    {
        using var files = new ExportFixture(longRender: true);
        await files.PrepareAsync();
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        string key;
        string id;
        await using (var session = await files.OpenAsync(workspace: true))
        {
            using var host = Workspace(session);
            Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
            host.Click(ButtonIn(host.Window.Heading, "Экспорт MP4"));
            var panel = WaitForExport(host);
            var check = ButtonIn(panel, "Проверить монтаж");
            host.PumpUntil(() => check.IsEnabled);
            host.Click(check);
            var start = ButtonIn(panel, "Запустить экспорт");
            host.PumpUntil(() => start.IsEnabled, TimeSpan.FromSeconds(16));
            host.Click(start);
            var cancel = ButtonIn(panel, "Отменить экспорт");
            host.PumpUntil(() => cancel.IsEnabled, TimeSpan.FromSeconds(16));
            key = File.ReadAllText(files.Checkpoint());
            var job = session.Connection.FindExportAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            id = job.Id;
            var directory = files.JobDirectory(id);
            host.PumpUntil(() => File.Exists(Path.Combine(directory, "Video.mp4")) && cancel.IsEnabled, TimeSpan.FromSeconds(20));
            host.Click(cancel);
            host.PumpUntil(() => RasterWindow<SceneView>.Walk(panel).OfType<Text>().Any(row =>
                row.Value.Contains("Экспорт отменён", StringComparison.Ordinal)), TimeSpan.FromSeconds(12));
            Assert.False(ButtonIn(panel, "Сохранить MP4").IsEnabled);
            WaitForWorkerLockAsync(directory, CancellationToken.None).GetAwaiter().GetResult();
            files.AssertProject(bytes, history);
        }
        await using (var session = await files.OpenAsync(workspace: true))
        {
            using var host = Workspace(session);
            Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
            host.Click(ButtonIn(host.Window.Heading, "Экспорт MP4"));
            var panel = WaitForExport(host);
            host.PumpUntil(() => RasterWindow<SceneView>.Walk(panel).OfType<Text>().Any(row =>
                row.Value.Contains("Экспорт отменён", StringComparison.Ordinal)));
            Assert.False(ButtonIn(panel, "Сохранить MP4").IsEnabled);
            Assert.Equal(id, session.Connection.FindExportAsync(key, CancellationToken.None).GetAwaiter().GetResult().Id);
            Assert.Single(files.Requests());
            files.AssertProject(bytes, history);
        }
    }

    private static RasterWindow<SceneView> Workspace(ExportSession session) =>
        new(session.App.Services.Resolve<IFactory<SceneView>>()!.Create()!, "workspace-export", 1280, 820);

    private static Button ButtonIn(Element root, string label) =>
        Assert.Single(RasterWindow<SceneView>.Walk(root).OfType<Button>(), button => button.Value == label);

    private static SceneExportPanel WaitForExport(RasterWindow<SceneView> host)
    {
        host.PumpUntil(() => host.Window.Overlays.Popup is SceneExportPanel);
        return Assert.IsType<SceneExportPanel>(host.Window.Overlays.Popup);
    }

    private static void Ready(RasterWindow<SceneView> host, SceneViewModel model)
    {
        host.PumpUntil(() => model.Loaded && model.Pixels is not null && model.Idle || model.HasError, TimeSpan.FromSeconds(15));
        Assert.False(model.HasError, model.Status);
    }

    [BackendMediaFact]
    public async Task Export_TrimmedMontageViaCli_DownloadsVerifiedMp4_AndRecoversAfterRestart()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        string id;
        string sha;
        string requestHash;
        byte[] finishedProject;
        string jobDirectory;
        await using (var session = await files.OpenAsync())
        {
            var opened = await session.Connection.OpenAsync();
            var saved = await session.Connection.TrimAsync("clip-main", 4, 20, 4, opened.Project.Revision, opened.Fingerprint);
            Assert.Equal(13, saved.Project.Revision);
            var projectBytes = File.ReadAllBytes(files.ProjectPath);
            var history = files.History();
            var start = await session.CliAsync("backend-export-start", "--client-key", "trimmed-export-first",
                "--profile", "project", "--revision", "13", "--sha256", saved.Fingerprint);
            id = start["id"]!.GetValue<string>();
            requestHash = start["request_sha256"]!.GetValue<string>();
            jobDirectory = files.JobDirectory(id);
            var requestPath = Path.Combine(jobDirectory, "Request.json");
            var requestBytes = File.ReadAllBytes(requestPath);
            Assert.Equal(requestHash, Hash(requestBytes), StringComparer.OrdinalIgnoreCase);
            var request = JsonNode.Parse(requestBytes)!;
            Assert.Equal(13, request["project_revision"]!.GetValue<int>());
            Assert.Equal(saved.Fingerprint, request["project_fingerprint"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(4, request["clips"]![0]!["start_frame"]!.GetValue<int>());
            Assert.Equal(4d, request["clips"]![0]!["source_in_frame"]!.GetValue<double>());
            Assert.Equal(20d, request["clips"]![0]!["source_out_frame"]!.GetValue<double>());
            Assert.All(request["assets"]!.AsObject(), row =>
                Assert.Equal(Hash(File.ReadAllBytes(row.Value!["path"]!.GetValue<string>())),
                    row.Value["expected_sha256"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase));
            files.AssertProject(projectBytes, history);

            // Changes after submission belong to the next export, even when a client repeats Start.
            var moved = await session.Connection.MoveAsync("clip-main", 8, 13, saved.Fingerprint);
            Assert.Equal(14, moved.Project.Revision);
            finishedProject = File.ReadAllBytes(files.ProjectPath);
            var finishedHistory = files.History();
            var same = await session.CliAsync("backend-export-start", "--client-key", "trimmed-export-first",
                "--profile", "project", "--revision", "13", "--sha256", saved.Fingerprint);
            Assert.Equal(id, same["id"]!.GetValue<string>());
            Assert.Single(files.Requests());
            var result = await session.UntilAsync(id, row => row["state"]!.GetValue<string>() != "running", jobDirectory);
            Assert.True(result["state"]!.GetValue<string>() == "completed", FailureDetails(jobDirectory, result));
            Assert.Equal(13, result["project_revision"]!.GetValue<int>());
            Assert.Equal(72, result["frames"]!.GetValue<int>());
            sha = result["artifact_sha256"]!.GetValue<string>();
            var output = Path.Combine(files.Files.Files.Root, "verified.mp4");
            await session.CliAsync("backend-export-download", "--id", id, "--sha256", sha, "--output", output);
            Assert.Equal(result["artifact_size_bytes"]!.GetValue<long>(), new FileInfo(output).Length);
            Assert.Equal(sha, Hash(File.ReadAllBytes(output)), StringComparer.OrdinalIgnoreCase);
            await files.AssertMediaAsync(output);
            files.AssertProject(finishedProject, finishedHistory);
            Assert.Equal(requestBytes, File.ReadAllBytes(requestPath));
        }

        await using (var session = await files.OpenAsync())
        {
            var recovered = await session.CliAsync("backend-export-recover", "--id", id);
            Assert.Equal("completed", recovered["state"]!.GetValue<string>());
            Assert.Equal(sha, recovered["artifact_sha256"]!.GetValue<string>());
            Assert.Equal(requestHash, recovered["request_sha256"]!.GetValue<string>());
            var artifact = Path.Combine(jobDirectory, "Draft.mp4");
            using (var stream = File.Open(artifact, FileMode.Open, FileAccess.ReadWrite))
            {
                var changed = (byte)(stream.ReadByte() ^ 1);
                stream.Position = 0;
                stream.WriteByte(changed);
            }
            var corrupt = await session.PostAsync("export-recover", new { id });
            Assert.Equal("failed", corrupt["state"]!.GetValue<string>());
            Assert.Equal("invalid_export_artifact", corrupt["error_code"]!.GetValue<string>());
            using var chunk = await session.RawAsync("export-chunk", new { id, artifact_sha256 = sha, offset = 0 });
            Assert.False(chunk.IsSuccessStatusCode);
            Assert.Equal(finishedProject, File.ReadAllBytes(files.ProjectPath));
            Assert.Single(files.Requests());
        }
    }

    [BackendMediaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_ActiveWorkerIsReaped_AndJobDoesNotReplay(bool crash)
    {
        using var files = new ExportFixture(longRender: true);
        await files.PrepareAsync();
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        string id;
        byte[] request;
        string jobDirectory;
        await using (var session = await files.OpenAsync())
        {
            var observed = await session.Connection.OpenAsync();
            var started = await session.PostAsync("export-start", new
            {
                client_key = "shutdown-test-export", profile = "project",
                expected_revision = observed.Project.Revision, fingerprint = observed.Fingerprint
            });
            id = started["id"]!.GetValue<string>();
            jobDirectory = files.JobDirectory(id);
            request = File.ReadAllBytes(Path.Combine(jobDirectory, "Request.json"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(jobDirectory, "Video.mp4")) && !deadline.IsCancellationRequested)
            {
                var status = await session.PostAsync("export-status", new { id });
                if (status["state"]!.GetValue<string>() != "running")
                    Assert.Fail(FailureDetails(jobDirectory, status));
                await Task.Delay(20);
            }
            var active = await session.PostAsync("export-status", new { id });
            Assert.True(File.Exists(Path.Combine(jobDirectory, "Video.mp4")), FailureDetails(jobDirectory, active));
            Assert.Equal("running", active["state"]!.GetValue<string>());
            if (crash)
            {
                var factory = session.App.Services.Resolve<BackendProcessFactory>()!;
                Assert.True(factory.Owned!.IsServing(observed.Identity.ProcessId));
                using var backend = Process.GetProcessById(observed.Identity.ProcessId);
                // Kill just the owned backend. Its worker must respond to pipe closure independently.
                backend.Kill();
                await backend.WaitForExitAsync(deadline.Token);
                await WaitForWorkerLockAsync(jobDirectory, deadline.Token);
            }
            else
            {
                using var stale = await session.RawAsync("export-cancel", new { id, expected_job_revision = active["revision"]!.GetValue<long>() + 1 });
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
                await session.CliAsync("backend-export-cancel", "--id", id, "--job-revision",
                    active["revision"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture));
                var cancelled = await session.UntilAsync(id, row => row["state"]!.GetValue<string>() == "cancelled", jobDirectory);
                Assert.Null(cancelled["artifact_sha256"]);
                await WaitForWorkerLockAsync(jobDirectory, deadline.Token);
            }
        }

        await using (var session = await files.OpenAsync())
        {
            var recovered = await session.CliAsync("backend-export-recover", "--id", id);
            Assert.Equal(crash ? "interrupted" : "cancelled", recovered["state"]!.GetValue<string>());
            var repeated = await session.CliAsync("backend-export-start", "--client-key", "shutdown-test-export", "--profile", "project",
                "--revision", "12", "--sha256", recovered["project_fingerprint"]!.GetValue<string>());
            Assert.Equal(id, repeated["id"]!.GetValue<string>());
            Assert.Equal(recovered["state"]!.GetValue<string>(), repeated["state"]!.GetValue<string>());
            Assert.Equal(request, File.ReadAllBytes(Path.Combine(jobDirectory, "Request.json")));
            Assert.Single(files.Requests());
            await WaitForWorkerLockAsync(jobDirectory, CancellationToken.None);
            files.AssertProject(bytes, history);
        }
    }

    private static async Task WaitForWorkerLockAsync(string folder, CancellationToken token)
    {
        if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                using var owner = File.Open(Path.Combine(folder, "Worker.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                owner.Lock(0, 1);
                owner.Unlock(0, 1);
                return;
            }
            catch (IOException) { await Task.Delay(20, deadline.Token); }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string FailureDetails(string directory, JsonNode status)
    {
        var log = Path.Combine(directory, "Worker.log");
        var ffmpeg = Path.Combine(directory, "FFmpeg.log");
        var files = string.Join(", ", Directory.EnumerateFiles(directory).Select(Path.GetFileName));
        return $"{status}\nFiles: {files}\n{ReadLog(log)}\n{ReadLog(ffmpeg)}";

        static string ReadLog(string path)
        {
            try
            {
                using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(file);
                return reader.ReadToEnd();
            }
            catch (IOException) { return ""; }
        }
    }

    private sealed class ExportFixture(bool longRender = false) : IDisposable
    {
        internal BackendWriteFixture Files { get; } = new();
        internal string ProjectPath => Files.Files.ProjectPath;
        private string Root => Files.Files.Root;
        private string Exports => Path.Combine(Root, "exports");
        internal string OutputDirectory => Exports;
        private string Media => Path.Combine(Root, "media");
        internal string MediaPath(string name) => Path.Combine(Media, name);
        private static string Ffmpeg => Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!;
        private static string Ffprobe => Environment.GetEnvironmentVariable("STUDIO_TEST_FFPROBE")!;
        internal string[] Requests() => Directory.GetFiles(Exports, "Request.json", SearchOption.AllDirectories);
        internal string Checkpoint() => Directory.GetFiles(Exports, "ClientKey", SearchOption.AllDirectories).Single();
        internal string JobDirectory(string id) => Directory.GetDirectories(Exports, id, SearchOption.AllDirectories).Single();
        internal byte[][] History() => Files.Files.HistoryFiles.Order().Select(File.ReadAllBytes).ToArray();

        internal async Task PrepareAsync(bool withTransition = false, bool scenarioOnly = false)
        {
            Directory.CreateDirectory(Media);
            Directory.CreateDirectory(Exports);
            var config = JsonNode.Parse(File.ReadAllText(Files.Base.ConfigPath))!;
            config["projects_root"] = Files.Files.ProjectDirectory;
            config["mounts"] = new JsonObject { ["videos"] = Media };
            config["exports_root"] = Exports;
            config["python_executable"] = Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON");
            config["ffmpeg_executable"] = Ffmpeg;
            config["ffprobe_executable"] = Ffprobe;
            File.WriteAllText(Files.Base.ConfigPath, config.ToJsonString());
            if (!scenarioOnly)
            {
                await SourceAsync("red", 440, "Красный.mp4");
                await SourceAsync("blue", 660, "Синий.mp4");
            }
            var project = JsonNode.Parse(File.ReadAllText(ProjectPath))!;
            project["output_size"] = longRender ? new JsonArray(1600, 900) : new JsonArray(160, 120);
            project["assets"]!["video-a"]!["path"] = "@videos/Красный.mp4";
            project["assets"]!["video-b"]!["path"] = "@videos/Синий.mp4";
            var timeline = project["timeline"]!;
            timeline["clip_audio"] = true;
            var windows = timeline["segments"]!.AsArray();
            windows[0]!["duration_frames"] = 36;
            windows[1]!["start_frame"] = 36;
            windows[1]!["duration_frames"] = longRender ? 3600 : 36;
            var clips = timeline["clips"]!.AsArray();
            clips[1]!["start_frame"] = 40;
            if (withTransition)
            {
                clips[0]!["speed"] = 1.25;
                clips[0]!["source_out_frame"] = 30;
                timeline["transitions"] = new JsonArray(new JsonObject
                {
                    ["left_clip_id"] = "clip-main", ["right_clip_id"] = "clip-extra",
                    ["fade_out_frames"] = 4, ["pause_frames"] = 4, ["fade_in_frames"] = 4
                });
            }
            if (scenarioOnly)
            {
                windows.Clear();
                clips.Clear();
            }
            File.WriteAllText(ProjectPath, project.ToJsonString());
            var store = Files.Files.Store();
            await store.MigrateAsync(await store.OpenAsync(), 11, "export-fixture");
        }

        private Task<byte[]> SourceAsync(string color, int frequency, string name) => BackendTrimFixture.RunAsync(Ffmpeg,
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", $"color=c={color}:size=160x120:rate=24:duration=2", "-f", "lavfi", "-i",
             $"sine=frequency={frequency}:sample_rate=48000:duration=2", "-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264",
             "-c:a", "alac", "-ac", "2", "-threads", "1", "-n", Path.Combine(Media, name)], 4096);

        internal async Task<ExportSession> OpenAsync(bool workspace = false)
        {
            var options = BackendOptions.Create(Files.Files.ProjectDirectory, Files.Base.ConfigPath) with
            { WriteEnabled = true, Action = "serve", ExecutablePath = BackendWriteFixture.Executable() };
            var app = TrueMoon.App.Builder(_ => { }).Setup(context =>
            {
                context.UseProjectBackend(options, false);
                if (workspace) context.UseBackendScene();
            }).Build();
            var session = new ExportSession(app);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await app.StartAsync(deadline.Token);
                await session.Http.StartAsync(deadline.Token);
                return session;
            }
            catch { await session.DisposeAsync(); throw; }
        }

        internal void AssertProject(byte[] bytes, byte[][] history)
        {
            Assert.Equal(bytes, File.ReadAllBytes(ProjectPath));
            var current = History();
            Assert.Equal(history.Length, current.Length);
            for (var index = 0; index < current.Length; index++) Assert.Equal(history[index], current[index]);
            Assert.False(File.Exists(Files.Files.JournalPath));
        }

        internal async Task AssertMediaAsync(string output)
        {
            var metadata = JsonNode.Parse(await BackendTrimFixture.RunAsync(Ffprobe,
                ["-v", "error", "-show_streams", "-of", "json", output], 32 * 1024))!["streams"]!.AsArray();
            Assert.Equal(2, metadata.Count);
            Assert.Equal("h264", metadata[0]!["codec_name"]!.GetValue<string>());
            Assert.Equal("72", metadata[0]!["nb_frames"]!.GetValue<string>());
            Assert.Equal("24/1", metadata[0]!["avg_frame_rate"]!.GetValue<string>());
            Assert.Equal("aac", metadata[1]!["codec_name"]!.GetValue<string>());
            Assert.Equal("48000", metadata[1]!["sample_rate"]!.GetValue<string>());
            Assert.Equal(2, metadata[1]!["channels"]!.GetValue<int>());
            var frames = await BackendTrimFixture.RunAsync(Ffmpeg, ["-nostdin", "-v", "error", "-i", output, "-map", "0:v:0",
                "-an", "-vf", "select='eq(n,3)+eq(n,4)+eq(n,19)+eq(n,20)+eq(n,40)+eq(n,64)',crop=2:2:80:60",
                "-fps_mode", "passthrough", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"], 72);
            Assert.Equal(72, frames.Length);
            foreach (var index in new[] { 0, 3, 5 }) Assert.All(frames.Skip(index * 12).Take(12), value => Assert.InRange(value, 0, 8));
            foreach (var index in new[] { 1, 2 })
            {
                Assert.InRange(frames[index * 12], 230, 255);
                Assert.InRange(frames[index * 12 + 2], 0, 10);
            }
            Assert.InRange(frames[48], 0, 10);
            Assert.InRange(frames[50], 230, 255);
            var pcm = await BackendTrimFixture.RunAsync(Ffmpeg, ["-nostdin", "-v", "error", "-i", output, "-map", "0:a:0",
                "-vn", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"], 1200000);
            var samples = MemoryMarshal.Cast<byte, float>(pcm).ToArray();
            double Rms(int firstFrame, int length) => Math.Sqrt(samples.Skip(firstFrame * 4000).Take(length * 4000).Select(s => (double)s * s).Average());
            Assert.InRange(Rms(0, 2), 0, .003);
            Assert.InRange(Rms(6, 10), .03, .15);
            Assert.InRange(Rms(23, 12), 0, .003);
            Assert.InRange(Rms(42, 15), .03, .15);
        }

        public void Dispose() => Files.Dispose();
    }

    private sealed class ExportSession(IApp app) : IAsyncDisposable
    {
        internal IApp App => app;
        internal BackendConnection Connection => app.Services.Resolve<BackendConnection>()!;
        internal BackendHttpServer Http { get; } = new(app.Services.Resolve<BackendConnection>()!);

        internal async Task<HttpResponseMessage> RawAsync(string action, object body)
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                { BaseAddress = Http.Address, Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Add("X-Studio-Token", Http.Token);
            return await client.PostAsJsonAsync("/api/backend/v1/" + action, body);
        }

        internal async Task<JsonNode> PostAsync(string action, object body)
        {
            using var response = await RawAsync(action, body);
            var data = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, data);
            return JsonNode.Parse(data)!;
        }

        internal async Task<JsonNode> CliAsync(params string[] args)
        {
            using var cli = new Support.BackendCliProcess(Http.Address, Http.Token, args);
            var result = await cli.WaitAsync();
            Assert.True(result.Code == 0, result.Error);
            return JsonNode.Parse(result.Output)!;
        }

        internal async Task<JsonNode> UntilAsync(string id, Func<JsonNode, bool> predicate, string jobDirectory)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            while (true)
            {
                var row = await PostAsync("export-status", new { id });
                if (predicate(row)) return row;
                if (deadline.IsCancellationRequested) Assert.Fail(FailureDetails(jobDirectory, row));
                await Task.Delay(50);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Http.DisposeAsync(); }
            finally { try { await app.StopAsync(); } finally { await app.DisposeAsync(); } }
        }
    }
}
