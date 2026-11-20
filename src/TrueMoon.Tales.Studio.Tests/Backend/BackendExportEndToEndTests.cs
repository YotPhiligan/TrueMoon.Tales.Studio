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
    public async Task Dependencies_FromRaster_CompareSavedRevisionsAndKeepOriginalResults()
    {
        TrueMoon.Alloy.Rendering.Skia.SkiaNativeLibrary.Initialize();
        using var files = new ExportFixture();
        await files.PrepareAsync();
        var texts = Directory.CreateDirectory(Path.Combine(files.Files.Files.Root, "texts")).FullName;
        var chapter = Path.Combine(texts, "Глава.md");
        File.WriteAllText(chapter, "Прежний текст.\n");
        var config = JsonNode.Parse(File.ReadAllText(files.Files.Base.ConfigPath))!;
        config["mounts"]!["texts"] = texts;
        File.WriteAllText(files.Files.Base.ConfigPath, config.ToJsonString());
        Draw("Watch1.png", SkiaSharp.SKColors.Red);
        Draw("Watch2.png", SkiaSharp.SKColors.Green);
        await using var session = await files.OpenAsync(workspace: true);
        var api = session.Connection;
        var head = await api.AcquireWriterAsync(await api.OpenAsync());
        var source = head.Project.Sources.First();
        await Write(new SaveSegment(null, "Привал", "", 72, 48));
        var scene = head.Project.Segments.Last().Id;
        await Write(new SaveAction(scene, null, "Крупный план", "", 12));
        await Write(new SaveAction(scene, null, "Перебивка", "", 12));
        var (action, insert) = (head.Project.Segments.Last().Actions[0].Id, head.Project.Segments.Last().Actions[1].Id);
        await Write(new LinkSource(scene, action, source.Id, null));
        await Write(new PlaceClip(scene, action, source.Id, source.Takes[0].Id, 72, 12, 0));
        var clip = head.Project.Montage.Clips.Single(row => row.ActionId == action);
        await Write(new AddReferenceVersion(null, "Часы", "prop", "Версия A", "@videos/Watch1.png"));
        var reference = Assert.Single(BackendReferenceLibrary.Read(head.Project).Entries).Id;
        await Write(new AddReferenceVersion(reference, null, null, "Версия B", "@videos/Watch2.png"));
        await Write(new AssignSceneReference(scene, null, reference, 1));
        await Write(new AssignSceneReference(scene, insert, reference, 1));
        await Write(new RecordTakeReference(source.Id, source.Takes[0].Id, reference, 1));
        const string text = "@texts/Глава.md";
        await Write(new BindAdaptationText(scene, null, text, 1, 1, Hash(File.ReadAllBytes(chapter))));
        var voice = new SaveMontageSound(null, scene, null, "Рассказчик", "voice", "video-a", 0, 2, 10, 0, .6, false,
            new("narrator", "Рассказчик", action, 0, "Фикстура", "", 0, 0, false));
        await Write(voice);
        voice = voice with { Id = Assert.Single(head.Project.Montage.Properties["sound_events"].EnumerateArray()).GetProperty("id").GetString() };
        var before = head;
        var export = await api.StartExportAsync("dependencies-before", "draft", before.Project.Revision, before.Fingerprint);
        var directory = files.JobDirectory(export.Id);
        var completed = await session.UntilAsync(export.Id, row => (string?)row["state"] == "completed", directory);
        var mp4 = File.ReadAllBytes(Path.Combine(directory, "Draft.mp4"));
        var request = File.ReadAllBytes(Path.Combine(directory, "Request.json"));

        await Write(new AssignSceneReference(scene, null, reference, 2));
        var referenceOnly = await api.CheckDependenciesAsync(before.Project.Revision, head, CancellationToken.None);
        Assert.DoesNotContain(referenceOnly.Rows, row => row.Kind is "montage" or "animatic" or "export");
        Assert.DoesNotContain(referenceOnly.Rows, row => row.ActionId == insert);
        File.WriteAllText(chapter, "Обновлённый текст.\n");
        await Write(new BindAdaptationText(scene, action, text, 1, 1, Hash(File.ReadAllBytes(chapter))));
        var editorial = await api.CheckDependenciesAsync(before.Project.Revision, head, CancellationToken.None);
        Assert.Contains(editorial.Rows, row => row.Kind == "chapter" && row.ActionId == action);
        Assert.DoesNotContain(editorial.Rows, row => row.Kind is "montage" or "export");
        await Write(voice with { Input = "video-b" });
        await Write(new AssignTake(clip.Id, source.Takes[1].Id));

        var report = await api.CheckDependenciesAsync(before.Project.Revision, head, CancellationToken.None);
        Assert.Equal((before.Project.Revision, before.Fingerprint, head.Project.Revision, head.Fingerprint),
            (report.ComparedRevision, report.ComparedFingerprint, report.Revision, report.Fingerprint));
        Assert.Equal(new[] { "animatic", "chapter", "clip", "export", "montage", "reference", "sound", "take_reference" },
            report.Rows.Select(row => row.Kind).Order(StringComparer.Ordinal));
        Assert.Equal(action, Assert.Single(report.Rows, row => row.Kind == "chapter").ActionId);
        Assert.Equal(source.Takes[0].Id, Assert.Single(report.Rows, row => row.Kind == "take_reference").TakeId);
        Assert.Contains("v1", Assert.Single(report.Rows, row => row.Kind == "take_reference").Before);
        Assert.Contains("v2", Assert.Single(report.Rows, row => row.Kind == "sound").After);
        Assert.Equal(clip.Id, Assert.Single(report.Rows, row => row.Kind == "clip").ClipId);
        var savedExport = Assert.Single(report.Rows, row => row.Kind == "export");
        Assert.Equal(((string?)completed["artifact_sha256"], export.Id), (savedExport.After, savedExport.Label));
        Assert.Equal("revision_conflict", (await Assert.ThrowsAsync<BackendCommandException>(() =>
            api.CheckDependenciesAsync(before.Project.Revision - 1, before, CancellationToken.None))).Code);
        Assert.Equal("dependency_history_missing", (await Assert.ThrowsAsync<BackendCommandException>(() =>
            api.CheckDependenciesAsync(0, head, CancellationToken.None))).Code);

        var current = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        using var host = Workspace(session);
        Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
        host.Click(ButtonIn(host.Window.Navigation, "Сценарий и материалы"));
        var editor = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>());
        host.PumpUntil(() => !editor.Busy);
        host.Click(ButtonIn(editor, "Зависимости"));
        SetField(editor, "Ревизия для сравнения", before.Project.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        host.Click(ButtonIn(editor, "Сравнить ревизии"));
        host.PumpUntil(() => !editor.Busy);
        Assert.Contains(RasterWindow<SceneView>.Walk(editor).OfType<Text>(), row => row.Value == "Дубль с прежней опорой · " + BackendScenarioEdit.TakeName(source.Takes[0]));
        host.Capture("dependencies-revisions", new { before.Project.Revision, Current = head.Project.Revision });
        host.Resize(840, 660, 1);
        host.Capture("dependencies-compact", new { before.Project.Revision, Current = head.Project.Revision });
        host.Resize(1280, 820, 1);
        var open = RasterWindow<SceneView>.Walk(editor).OfType<Button>().First(row => row.Value == "К дублю");
        for (var user = open.Parent; user is not null; user = user.Parent)
            if (user is ScrollViewer scroll) { scroll.BringIntoView(open.Bounds); host.Update(); }
        host.Click(open);
        Assert.Equal(ControlTone.Accent, ButtonIn(editor, "Библиотека").Tone);
        Assert.Equal(ControlTone.Accent, ButtonIn(editor, BackendScenarioEdit.TakeName(source.Takes[0])).Tone);
        files.AssertProject(current, history);
        Assert.Equal(mp4, File.ReadAllBytes(Path.Combine(directory, "Draft.mp4")));
        Assert.Equal(request, File.ReadAllBytes(Path.Combine(directory, "Request.json")));

        async Task Write(ScenarioCommand command) => head = await api.EditScenarioAsync(command, head);
        void Draw(string name, SkiaSharp.SKColor color)
        {
            using var bitmap = new SkiaSharp.SKBitmap(8, 8);
            bitmap.Erase(color);
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(files.MediaPath(name), encoded.ToArray());
        }
    }

    [BackendMediaFact]
    public async Task JobSnapshots_FromRaster_CompareArchivedContinuationsAndPrepareOnlySelectedFullTransfer()
    {
        TrueMoon.Alloy.Rendering.Skia.SkiaNativeLibrary.Initialize();
        using var files = new ExportFixture();
        await files.PrepareAsync(fullTransfer: true);
        var ffmpeg = Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!;
        foreach (var color in new[] { "blue", "red" })
            await BackendTrimFixture.RunAsync(ffmpeg,
                ["-nostdin", "-v", "error", "-f", "lavfi", "-i", $"color=c={color}:s=160x120:r=24", "-frames:v", "56",
                 "-threads", "1", "-n", files.MediaPath(color + ".mp4")], 2048);
        await BackendTrimFixture.RunAsync(ffmpeg, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=lime:s=160x120",
            "-frames:v", "1", "-threads", "1", "-n", files.MediaPath("Ref.png")], 2048);
        var exe = await files.PrepareTransferWorkerFixtureAsync();
        await using var session = await files.OpenAsync(workspace: true, executable: exe);
        var api = session.Connection;
        var baseProject = await api.AcquireWriterAsync(await api.OpenAsync());
        var sourceId = baseProject.Project.Sources[0].Id;
        var old = await api.EditScenarioAsync(new ImportVideo(sourceId, "", "Синий черновик", "@videos/blue.mp4"), baseProject);
        var oldTake = old.Project.Sources[0].Takes.Last();
        const string prompt = "subject_definitions: <Picture 1> is the room. <Video 1> supplies motion.\n" +
            "summary: Follow the motion.\nretention_analysis: Keep Picture roles.\ndetailed_description: One continuous shot.\n" +
            "overall_soundscape: N/A\n\nnon_diegetic_music: N/A";
        var parent = await api.PrepareFullTransferAsync(new(new('a', 32), sourceId, oldTake.Id, ulong.MaxValue, prompt,
            [new("@videos/Ref.png", "Комната")], 1, "Ритм", "Прежнее движение"), old, CancellationToken.None);
        var head = await api.EditScenarioAsync(new ImportVideo(sourceId, "", "Красный черновик", "@videos/red.mp4"), old);
        var newTake = head.Project.Sources[0].Takes.Last();

        var production = Path.Combine(Path.GetDirectoryName(files.ProjectPath)!, "Production");
        const string archiveA = "job-0000000000000001", archiveB = "job-0000000000000002";
        const string archivedPrompt = "Сохранённый промпт продолжения.\r\nВторая строка.";
        var legacyA = WriteArchive(archiveA, "take-archive-1", archivedPrompt, new('a', 64));
        var legacyB = WriteArchive(archiveB, "take-archive-2", archivedPrompt, null);
        var jobs = Path.Combine(production, "FullTransfer.v1");
        var receipts = CaptureFiles(Path.Combine(jobs, parent.Id));
        var archiveFiles = CaptureFiles(Path.Combine(production, "Jobs"));
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        var input = Path.Combine(files.ComfyRoot, "input");
        var comfy = CaptureFiles(input);

        var archives = await Compare(archiveA, archiveB);
        Assert.Equal(archivedPrompt, archives.Before.Prompt);
        Assert.Equal(archives.Before.PromptSha256, archives.After.PromptSha256);
        Assert.Equal(new SavedJobMotion("motion_context", "take-archive-1", null, new('A', 64)), archives.Before.Motion);
        Assert.Equal(new SavedJobMotion("motion_context", "take-archive-2", null, null), archives.After.Motion);
        Assert.Null(archives.Before.TransferForm);
        Assert.Equal("job_snapshot_ids", await Reject(archiveA, archiveA));
        Assert.Equal("job_snapshot_ids", await Reject("../" + archiveA, archiveB));
        Assert.Equal("job_snapshot_missing", await Reject(archiveA, "job-0000000000000003"));
        Assert.Equal("revision_conflict", (await Assert.ThrowsAsync<BackendCommandException>(() =>
            api.CompareJobSnapshotsAsync(archiveA, archiveB, old, CancellationToken.None))).Code);
        var legacyRecord = File.ReadAllText(legacyB);
        var otherProject = JsonNode.Parse(legacyRecord)!;
        otherProject["project_id"] = "wrong-project";
        File.WriteAllText(legacyB, otherProject.ToJsonString());
        Assert.Equal("job_snapshot_invalid", await Reject(archiveA, archiveB));
        File.WriteAllText(legacyB, legacyRecord);
        var oldPrompt = Path.Combine(Path.GetDirectoryName(legacyA)!, "Input", "Prompt.txt");
        File.AppendAllText(oldPrompt, "Подмена");
        Assert.Equal("job_snapshot_invalid", await Reject(archiveA, archiveB));
        File.WriteAllText(oldPrompt, archivedPrompt);
        var parentSnapshot = (await Compare(parent.Id, archiveA)).Before;
        var form = parentSnapshot.TransferForm!;
        var revision = new FullTransferPredecessor(parent.Id, parent.RequestSha256);
        await Refuse(form with { Predecessor = revision }, head, "invalid_transfer");
        await Refuse(form with { ClientKey = new('f', 32), Predecessor = revision with { RequestSha256 = new('0', 64) } },
            head, "transfer_key_conflict");
        await Refuse(form with { ClientKey = new('f', 32), Predecessor = revision }, old, "revision_conflict");
        Assert.Single(Directory.GetDirectories(jobs));

        using var host = Workspace(session);
        Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
        host.Click(ButtonIn(host.Window.Navigation, "Сценарий и материалы"));
        var editor = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>());
        host.PumpUntil(() => !editor.Busy);
        Click("Зависимости");
        SetField(editor, "ID задания для сравнения · А", parent.Id);
        SetField(editor, "ID задания для сравнения · Б", archiveA);
        Click("Сравнить задания");
        Assert.Equal(prompt, Prompt("Сохранённый промпт · А").Text);
        Assert.Equal(archivedPrompt.Replace("\r\n", "\n"), Prompt("Сохранённый промпт · Б").Text.Replace("\r\n", "\n"));
        var leftPrompt = Prompt("Сохранённый промпт · А").Bounds;
        Assert.True(leftPrompt.Width > 0 && leftPrompt.X + leftPrompt.Width <= Prompt("Сохранённый промпт · Б").Bounds.X);
        Assert.Contains(Elements().OfType<Text>(), row => row.Value == "Motion Context · продолжение дубля take-archive-1");
        Assert.DoesNotContain(Elements().OfType<Button>(), row => row.Value == "Подготовить новую версию · Б");
        Reveal(Prompt("Сохранённый промпт · Б"));
        host.Capture("job-snapshot-archived-motion", new { parent.Id, Archive = archiveA });
        Click("Подготовить новую версию · А");
        Assert.True(editor.HasDraft);
        Assert.Equal(prompt, Prompt("Промпт Ref2VA").Text);
        Click("Отменить");
        Assert.False(editor.HasDraft);
        Assert.Single(Directory.GetDirectories(jobs));
        Click("Подготовить новую версию · А");
        Click("Video 1 · Красный черновик");
        var edited = prompt.Replace("Follow the motion.", "Continue with a pause.", StringComparison.Ordinal);
        Prompt("Промпт Ref2VA").Text = edited;
        Click("Подготовить");
        Assert.False(editor.HasDraft, editor.Status);
        Assert.Contains("без постановки в очередь", editor.Status);
        var childPath = Assert.Single(Directory.GetDirectories(jobs), path => Path.GetFileName(path) != parent.Id);
        var childId = Path.GetFileName(childPath);
        Click("Сравнить задания");
        var comparison = Compare(parent.Id, childId).GetAwaiter().GetResult();
        Assert.Equal(edited, Prompt("Сохранённый промпт · Б").Text);
        Assert.Contains(Elements().OfType<Text>(), row => row.Value == "Опоры движения различаются");
        Assert.Contains(Elements().OfType<Text>(), row => row.Value == $"Подготовлено по заданию {parent.Id}");
        Assert.Equal(revision, comparison.After.TransferForm!.Predecessor);
        Assert.Equal(head.Project.Revision, comparison.After.Revision);
        Assert.Equal(newTake.Id, comparison.After.Motion!.TakeId);
        Assert.NotEqual(comparison.Before.Motion!.Sha256, comparison.After.Motion.Sha256);
        var childReceipt = api.PrepareFullTransferAsync(comparison.After.TransferForm!, head, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(childId, childReceipt.Id);
        Assert.False(childReceipt.Staged);
        Assert.False(childReceipt.Enqueued);
        Assert.False(Directory.Exists(Path.Combine(childPath, "Run")));
        Assert.False(File.Exists(Path.Combine(childPath, "Staged.json")));
        Assert.Empty(Directory.GetFiles(childPath, "*Submission*", SearchOption.AllDirectories));
        Assert.Equal(2, Directory.GetDirectories(jobs).Length);
        Assert.Equal(receipts, CaptureFiles(Path.Combine(jobs, parent.Id)));
        Assert.Equal(archiveFiles, CaptureFiles(Path.Combine(production, "Jobs")));
        Assert.Equal(comfy, CaptureFiles(input));
        files.AssertProject(bytes, history);
        Reveal(Prompt("Сохранённый промпт · Б"));
        host.Capture("job-snapshots-selected-revision", new { Parent = parent.Id, Revision = childId, Take = newTake.Id });

        Task<JobSnapshotComparison> Compare(string before, string after) =>
            api.CompareJobSnapshotsAsync(before, after, head, CancellationToken.None);
        async Task<string> Reject(string before, string after) =>
            (await Assert.ThrowsAsync<BackendCommandException>(() => Compare(before, after))).Code;
        async Task Refuse(FullTransferForm attempt, OpenedBackendProject baseline, string code) =>
            Assert.Equal(code, (await Assert.ThrowsAsync<BackendCommandException>(() =>
                api.PrepareFullTransferAsync(attempt, baseline, CancellationToken.None))).Code);

        string WriteArchive(string id, string context, string text, string? sha)
        {
            var folder = Path.Combine(production, "Jobs", id);
            var inputs = Directory.CreateDirectory(Path.Combine(folder, "Input")).FullName;
            const string latent = "X:\\Historical\\Motion\\Previous.Av.safetensors";
            const string block = "block-archive";
            File.WriteAllText(Path.Combine(inputs, "Prompt.txt"), text);
            File.WriteAllText(Path.Combine(inputs, "Config.json"), JsonSerializer.Serialize(new { profile = "omni-context", previous_latent = latent }));
            File.WriteAllText(Path.Combine(inputs, "Block.snapshot.json"), JsonSerializer.Serialize(new
            {
                project_revision = old.Project.Revision, block = new { id = block, context_take_id = context }
            }));
            var file = Path.Combine(folder, "Studio.job.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                id, project_id = head.Project.Id, block_id = block, source_revision = old.Project.Revision,
                input_files = Directory.GetFiles(inputs).ToDictionary(path => $"X:\\Historical\\Production\\Jobs\\{id}\\Input\\{Path.GetFileName(path)}",
                    path => Hash(File.ReadAllBytes(path)).ToLowerInvariant()),
                source_files = sha is null ? [] : new[] { new { path = latent, sha256 = sha } }
            }));
            return file;
        }

        static string[] CaptureFiles(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => $"{Path.GetRelativePath(directory, path)} {Hash(File.ReadAllBytes(path))}").ToArray();
        IEnumerable<Element> Elements() => RasterWindow<SceneView>.Walk(editor);
        RichTextEditor Prompt(string label) =>
            Assert.Single(Elements().OfType<RichTextEditor>(), row => row.AccessibleName == label);
        void Click(string label)
        {
            var button = ButtonIn(editor, label);
            Reveal(button);
            host.Click(button);
            host.PumpUntil(() => !editor.NeedsFrame);
        }

        void Reveal(Element element)
        {
            for (var parentElement = element.Parent; parentElement is not null; parentElement = parentElement.Parent)
                if (parentElement is ScrollViewer scroll) { scroll.BringIntoView(element.Bounds); host.Update(); }
        }
    }

    [BackendMediaFact]
    public async Task Speech_FromRaster_KeepsVersionsAndRendersVerifiedSubtitle()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        await using var session = await files.OpenAsync(workspace: true);
        var connection = session.Connection;
        var initial = await connection.AcquireWriterAsync(await connection.OpenAsync());
        var window = initial.Project.Montage.Windows.First();
        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        Ready(host, model);
        host.Click(ButtonIn(host.Window.Timeline, "Дорожки"));
        var editor = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<MontageTrackWorkspace>());
        host.PumpUntil(() => !editor.NeedsFrame);
        host.Click(ButtonIn(editor, "+ Реплика или рассказчик"));
        SetField(editor, "Название", "Пробная запись");
        SetField(editor, "Окно сегмента (ID)", window.Id);
        SetField(editor, "Говорящий", "Рассказчик");
        SetField(editor, "Медиа (ID ассета или @mount/путь)", "video-a");
        SetField(editor, "Происхождение новой записи", "Первая запись");
        SetField(editor, "Смещение от клипа или окна, к.", "4");
        SetField(editor, "Начало субтитра от записи, к.", "6");
        SetField(editor, "Длительность субтитра, к.", "12");
        Assert.Single(RasterWindow<SceneView>.Walk(editor).OfType<RichTextEditor>()).Text = "Слышу речь";
        host.Click(ButtonIn(editor, "Сохранить дорожку"));
        host.PumpUntil(() => !editor.NeedsFrame);
        var first = connection.OpenAsync().GetAwaiter().GetResult();
        Assert.Equal(initial.Project.Revision + 1, first.Project.Revision);
        var row = Assert.Single(first.Project.Montage.Properties["sound_events"].EnumerateArray());
        var id = row.GetProperty("id").GetString()!;
        var version1 = Assert.Single(BackendSpeech.Read(row)!.Versions);
        Assert.Equal(Hash(File.ReadAllBytes(files.MediaPath("Красный.mp4"))), version1.Sha256);

        var replacement = new SaveMontageSound(id, window.Id, null, "Пробная запись", "voice", "video-b", 0, 4, 24, 0, 1, false,
            new("narrator", "Рассказчик", null, 0, "Вторая запись", "Слышу речь", 6, 12, false));
        Assert.Equal("revision_conflict", Assert.Throws<BackendCommandException>(() => Save(replacement, initial)).Code);
        Assert.Equal("invalid_speech", Assert.Throws<BackendCommandException>(() =>
            Save(replacement with { Speech = replacement.Speech! with { SubtitleDurationFrames = 19 } }, first)).Code);
        var second = Save(replacement, first);
        var speech = BackendSpeech.Read(Assert.Single(second.Project.Montage.Properties["sound_events"].EnumerateArray()))!;
        Assert.Equal(version1, speech.Versions[0]);
        Assert.Equal(2, speech.SelectedVersion);
        Assert.Equal(Hash(File.ReadAllBytes(files.MediaPath("Синий.mp4"))), speech.Selected.Sha256);
        Assert.Contains(connection.CheckReadinessAsync(second.Project.Revision, second.Fingerprint, CancellationToken.None).GetAwaiter().GetResult().Issues,
            issue => issue.Reason == "subtitle_review" && issue.TrackId == id);
        var confirmed = Save(replacement with { Speech = replacement.Speech! with { Version = 2, ConfirmSubtitle = true } }, second);
        var confirmedBytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        var substituted = files.MediaPath("Синий.mp4");
        var original = File.ReadAllBytes(substituted);
        try
        {
            File.WriteAllBytes(substituted, [..original, 0]);
            Assert.Contains(connection.CheckReadinessAsync(confirmed.Project.Revision, confirmed.Fingerprint, CancellationToken.None).GetAwaiter().GetResult().Issues,
                issue => issue.Reason == "speech_source_changed" && issue.TrackId == id);
        }
        finally { File.WriteAllBytes(substituted, original); }
        files.AssertProject(confirmedBytes, history);

        var review = connection.StartWorkingReviewAsync("speech-proof-preview", confirmed.Project.Revision, confirmed.Fingerprint,
            CancellationToken.None).GetAwaiter().GetResult();
        host.PumpUntil(() => (review = connection.WorkingReviewStatusAsync(review.Id, CancellationToken.None).GetAwaiter().GetResult()).State
            is "completed" or "failed", TimeSpan.FromSeconds(30));
        Assert.Equal("completed", review.State);
        var export = connection.StartExportAsync("speech-proof-export", "draft", confirmed.Project.Revision, confirmed.Fingerprint).GetAwaiter().GetResult();
        var result = session.UntilAsync(export.Id, status => (string?)status["state"] == "completed", files.JobDirectory(export.Id)).GetAwaiter().GetResult();
        Assert.Equal("completed", (string?)result["state"]);
        foreach (var frame in new[] { 9, 10, 21, 22 })
        {
            var preview = Read(review.Id, frame);
            Assert.Equal(preview.PixelSha256, Read(export.Id, frame).PixelSha256);
            var bright = Enumerable.Range(0, preview.Pixels.Length / 4).Count(i =>
                preview.Pixels[i * 4] > 178 && preview.Pixels[i * 4 + 1] > 178 && preview.Pixels[i * 4 + 2] > 178);
            Assert.Equal(frame is 10 or 21, bright > 10);
        }
        files.AssertProject(confirmedBytes, history);

        OpenedBackendProject Save(SaveMontageSound command, OpenedBackendProject head) =>
            connection.EditScenarioAsync(command, head).GetAwaiter().GetResult();
        BackendMediaReply Read(string job, int frame) => connection.FrameAsync(BackendRenderedMontage.Identifier(job, null),
            frame, confirmed.Project.Revision, confirmed.Fingerprint).GetAwaiter().GetResult();
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
            Assert.Equal(pcm.AsSpan(5 * 4000).ToArray(), Audio(reviewKey, start, end - start, 5));
            Assert.Equal(fullPcm.AsSpan(5 * 4000).ToArray(), Audio(exportKey, 0, export.Frames, 24).AsSpan(0, (end - 24) * 4000).ToArray());
            files.AssertProject(bytes, history);

            float[] Audio(string key, int origin, int length, int? reviewOffset = null)
            {
                var scene = new BackendRenderedMontage(key, origin, length, 24, (int)head.Project.Revision, head.Fingerprint).Scene();
                var offset = reviewOffset ?? 0;
                var stream = connection.OpenPlaybackAsync(scene, reviewOffset is null ? "audio" : BackendPlaybackSession.ReviewAudio,
                    offset, CancellationToken.None).GetAwaiter().GetResult();
                using var data = new MemoryStream();
                try
                {
                    while (true)
                    {
                        var packet = connection.ReadPlaybackAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
                        Assert.Equal(offset * 2000 + data.Length / 8, packet.SampleOffset);
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
    public async Task CursorReview_FromRaster_CancelsPreparationAndStartsPcmAtTheRequestedFrame()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync(withTransition: true);
        var starts = new List<ReviewSoundProbe>();
        await using (var session = await files.OpenAsync(workspace: true, reviewSound: source =>
        {
            var sound = new ReviewSoundProbe(source);
            starts.Add(sound);
            return sound;
        }))
        {
            var connection = session.Connection;
            var head = await connection.AcquireWriterAsync(await connection.OpenAsync());
            head = await connection.EditScenarioAsync(new SaveMontageMusic("video-a", 8, 40, 0, .35, false), head);
            var bytes = File.ReadAllBytes(files.ProjectPath);
            var history = files.History();
            using var host = Workspace(session);
            var model = session.App.Services.Resolve<SceneViewModel>()!;
            Ready(host, model);

            PlaceCursor(19);
            Start();
            host.Input(new(InputKind.KeyDown, Key: UiKey.Escape));
            host.PumpUntil(() => !Views().OfType<WorkingReviewWorkspace>().Any() && model.Idle, TimeSpan.FromSeconds(20));
            Assert.Empty(Views().OfType<RenderedReviewPanel>());
            Assert.Empty(starts);
            Assert.Empty(Directory.GetFiles(files.OutputDirectory, "Draft.mp4", SearchOption.AllDirectories));

            PlaceCursor(19);
            Start();
            host.PumpUntil(() => Views().OfType<RenderedReviewPanel>().Any(), TimeSpan.FromSeconds(30));
            var player = Assert.Single(Views().OfType<RenderedReviewPanel>());
            ReviewReady(host, player);
            var request = JsonNode.Parse(File.ReadAllText(Assert.Single(files.Requests())))!;
            Assert.Equal((19, 53), ((int)request["start_frame"]!, (int)request["frames"]!));
            Assert.Contains(Views().OfType<Text>(), item => item.Value.Contains("кадры 19–71"));
            Assert.Empty(starts);

            host.Click(ButtonIn(player, "Старт"));
            host.PumpUntil(() => starts.Count == 1 && starts[0].PlayedSamples > 0, TimeSpan.FromSeconds(15));
            Assert.Contains(MemoryMarshal.Cast<byte, float>(starts[0].First).ToArray(), sample => Math.Abs(sample) > .005f);
            host.Click(ButtonIn(player, "Пауза"));
            Assert.True(starts[0].Disposed);

            SetField(player, "Кадр предпросмотра", "10");
            host.Click(ButtonIn(player, "Перейти"));
            ReviewReady(host, player);
            Assert.Contains(Views().OfType<Text>(), item => item.Value.Contains("0010 / 0052"));
            host.Click(ButtonIn(player, "Старт"));
            host.PumpUntil(() => starts.Count == 2 && starts[1].PlayedSamples > 0, TimeSpan.FromSeconds(15));
            Assert.False(starts[0].First.SequenceEqual(starts[1].First));
            Assert.Equal(0, starts[0].Source.Read(new byte[8]));
            host.Capture("cursor-review-buffered-seek", new { Cursor = 19, Seek = 29 });
            host.Input(new(InputKind.KeyDown, Key: UiKey.Escape));
            Assert.True(starts[1].Disposed);
            Assert.Empty(Views().OfType<RenderedReviewPanel>());
            files.AssertProject(bytes, history);

            IEnumerable<Element> Views() => RasterWindow<SceneView>.Walk(host.Window);
            void PlaceCursor(int frame)
            {
                var strip = Assert.Single(Views().OfType<SceneStrip>());
                var segment = model.Scene!.Segment;
                var x = strip.Bounds.X + (float)(frame - segment.StartFrame + .1) * strip.Bounds.Width / segment.DurationFrames;
                host.Input(new(InputKind.PointerDown, x, strip.Bounds.Y + 8));
                host.Input(new(InputKind.PointerUp, x, strip.Bounds.Y + 8));
                host.PumpUntil(() => model.Idle && model.SplitFrame == frame);
            }
            void Start()
            {
                host.Click(ButtonIn(host.Window.Preview, "Рабочий просмотр"));
                var setup = Assert.Single(Views().OfType<WorkingReviewWorkspace>());
                host.Click(ButtonIn(setup, "Смотреть от курсора"));
            }
        }
        Assert.Empty(Directory.GetDirectories(files.OutputDirectory, "working-*", SearchOption.AllDirectories));
    }

    private sealed class ReviewSoundProbe(NAudio.Wave.IWaveProvider source) : TrueMoon.Tales.Studio.Media.IPreviewSound
    {
        internal readonly byte[] First = new byte[8192];
        internal NAudio.Wave.IWaveProvider Source { get; } = source;
        internal bool Disposed { get; private set; }
        public long PlayedSamples { get; private set; }
        public string? Warning => null;
        public Exception? Error => null;
        public void Play() { PlayedSamples = Source.Read(First) / 8; }
        public void Dispose() { Disposed = true; }
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
    public async Task Screenplay_RasterEditsSceneWithSavedChapter_AndGuardsPlanOrder()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        var texts = Directory.CreateDirectory(Path.Combine(files.Files.Files.Root, "texts")).FullName;
        var chapter = Path.Combine(texts, "Глава.md");
        File.WriteAllText(chapter, "# Глава\nЭйко держит часы.\nСлышен тихий ход.\n");
        var configPath = files.Files.Base.ConfigPath;
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        config["mounts"]!["texts"] = texts;
        File.WriteAllText(configPath, config.ToJsonString());
        await using var session = await files.OpenAsync(workspace: true);
        var backend = session.Connection;
        var head = await backend.AcquireWriterAsync(await backend.OpenAsync());
        var sourceClip = head.Project.Montage.Clips.First();
        head = await backend.EditScenarioAsync(new SaveSegment(null, "Привал QA", "", 72, 48), head);
        var segment = head.Project.Segments.Last();
        foreach (var (title, duration) in new[] { ("Опора", 12), ("Часы", 12), ("Готовый дубль", 24) })
            head = await backend.EditScenarioAsync(new SaveAction(segment.Id, null, title, "", duration), head);
        var actions = head.Project.Segments.Last().Actions;
        head = await backend.EditScenarioAsync(new LinkSource(segment.Id, actions[2].Id, sourceClip.SourceId, "Дубль"), head);
        head = await backend.EditScenarioAsync(new PlaceClip(segment.Id, actions[2].Id, sourceClip.SourceId, sourceClip.TakeId!, 96, 24, 2), head);
        var observed = await backend.ReadAdaptationTextAsync("@texts/Глава.md", 2, 3, CancellationToken.None);
        head = await backend.EditScenarioAsync(new BindAdaptationText(segment.Id, null, observed.Reference, 2, 3, observed.Sha256), head);
        var initialClips = head.Project.Montage.Clips;
        var original = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        File.WriteAllText(chapter, "# Изменённый исходник\n");

        using var host = Workspace(session);
        Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
        host.Click(ButtonIn(host.Window.Navigation, "Сценарий и материалы"));
        var scenario = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>());
        host.PumpUntil(() => !scenario.Busy);
        Click("Привал QA");
        Click("Часы");
        Click("Править сегмент");
        Assert.Equal(observed.Excerpt, Field("Сохранённый фрагмент главы").Text);
        Type("Назначение сцены", "От паузы к тихому облегчению.");
        Save();
        var purposeSaved = backend.OpenAsync().GetAwaiter().GetResult();
        Assert.Equal(head.Project.Revision + 1, purposeSaved.Project.Revision);
        Assert.Equal("От паузы к тихому облегчению.", purposeSaved.Project.Segments.Last().Description);
        Assert.Contains(files.History(), row => row.SequenceEqual(original));

        Click("Править действие");
        Type("Что видим", "Эйко держит часы.");
        Type("Что слышим", "Тихий ход часов под гулом пластины.");
        var fragment = Field("Сохранённый фрагмент главы");
        Assert.True(fragment.Options.IsReadOnly);
        Assert.Equal(observed.Excerpt, fragment.Text);
        Assert.True(Field("Что слышим").Bounds.X + Field("Что слышим").Bounds.Width <= fragment.Bounds.X);
        host.Capture("screenplay-edit-with-chapter", new { segment.Id, observed.Sha256 });
        Save();
        var authored = backend.OpenAsync().GetAwaiter().GetResult();
        var action = authored.Project.Segments.Last().Actions[1];
        Assert.Equal(purposeSaved.Project.Revision + 1, authored.Project.Revision);
        Assert.Equal(("Эйко держит часы.", "Тихий ход часов под гулом пластины."), (action.Description, action.SoundNotes));
        var oldCaller = backend.EditScenarioAsync(new SaveAction(segment.Id, action.Id, action.Title, action.Description, 12), authored)
            .GetAwaiter().GetResult();
        Assert.Equal(authored.Fingerprint, oldCaller.Fingerprint);
        Assert.Equal(action.SoundNotes, Field("Что слышим").Text);

        var beforeOrder = File.ReadAllBytes(files.ProjectPath);
        history = files.History();
        Click("Переставить");
        Click("Выше");
        files.AssertProject(beforeOrder, history);
        Click("Отменить");
        files.AssertProject(beforeOrder, history);
        Click("Переставить");
        Click("Выше");
        Save();
        var ordered = backend.OpenAsync().GetAwaiter().GetResult();
        Assert.Equal(authored.Project.Revision + 1, ordered.Project.Revision);
        Assert.Equal(new[] { actions[1].Id, actions[0].Id, actions[2].Id }, ordered.Project.Segments.Last().Actions.Select(row => row.Id));
        Assert.Equal(initialClips, ordered.Project.Montage.Clips);
        Assert.Equal(observed, Assert.Single(BackendAdaptation.Read(ordered.Project).Texts).Fragment);
        var storyboard = StoryboardShot.Read(ordered.Project).Where(row => row.SegmentId == segment.Id).ToArray();
        Assert.Equal(new[] { ("Часы", 72), ("Опора", 84), ("Готовый дубль", 96) },
            storyboard.Select(row => (row.Title, row.Position!.Frame)));
        Assert.Equal(history.Length + 1, files.History().Length);
        Assert.Contains(files.History(), row => row.SequenceEqual(beforeOrder));
        host.Capture("screenplay-saved-order", new { ordered.Project.Revision });

        var saved = File.ReadAllBytes(files.ProjectPath);
        history = files.History();
        Click("Переставить");
        Click("Ниже");
        Click("Ниже");
        Click("Сохранить");
        host.PumpUntil(() => !scenario.Busy);
        Assert.Contains("не помещаются", scenario.Status);
        Assert.True(scenario.HasDraft);
        files.AssertProject(saved, history);
        Click("Отменить");
        Click("Править действие");
        Type("Что слышим", "Черновик для другого дубля.");
        var intervening = backend.EditScenarioAsync(new SaveSegment(segment.Id, "Привал QA", "Уточнённая цель.", 0, 0), ordered)
            .GetAwaiter().GetResult();
        saved = File.ReadAllBytes(files.ProjectPath);
        history = files.History();
        Click("Сохранить");
        host.PumpUntil(() => !scenario.Busy);
        Assert.Contains("Проект изменился", scenario.Status);
        Assert.True(scenario.HasDraft);
        Assert.Equal("Черновик для другого дубля.", Field("Что слышим").Text);
        Assert.False(ButtonIn(scenario, "Сохранить").IsEnabled);
        files.AssertProject(saved, history);
        Assert.Equal(intervening.Fingerprint, backend.OpenAsync().GetAwaiter().GetResult().Fingerprint);
        Assert.Empty(files.Requests());

        RichTextEditor Field(string label) => Assert.Single(RasterWindow<SceneView>.Walk(scenario).OfType<RichTextEditor>(),
            row => row.AccessibleName == label);
        void Click(string label) => FocusAndClick(ButtonIn(scenario, label));
        void FocusAndClick(Element target)
        {
            for (var node = target.Parent; node is not null; node = node.Parent)
                if (node is ScrollViewer scroll) { scroll.BringIntoView(target.Bounds); host.Update(); }
            host.Click(target);
        }
        void Type(string label, string text)
        {
            var field = Field(label);
            FocusAndClick(field);
            host.Input(new(InputKind.KeyDown, Key: UiKey.A, Control: true));
            host.Input(new(InputKind.Text, Text: text));
            Assert.Equal(text, field.Text);
        }
        void Save()
        {
            Click("Сохранить");
            host.PumpUntil(() => !scenario.Busy);
            Assert.False(scenario.HasDraft, scenario.Status);
        }
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
    public async Task Storyboard_SceneJoins_CompareClockAndMeasurementBoundaries()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync(scenarioOnly: true);
        await BackendTrimFixture.RunAsync(Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!,
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=lime:s=160x120", "-frames:v", "1", "-threads", "1",
             "-n", files.MediaPath("Привал.png")], 2048);
        await using var session = await files.OpenAsync(workspace: true);
        var api = session.Connection;
        var snapshot = await api.AcquireWriterAsync(await api.OpenAsync());
        async Task<TrueMoon.Tales.Studio.Contracts.Projects.ScenarioSegmentContract> AddScene(string title, int start, int length,
            params (string Title, string Description, int Frames)[] actions)
        {
            snapshot = await api.EditScenarioAsync(new SaveSegment(null, title, "", start, length), snapshot);
            var id = snapshot.Project.Segments.Last().Id;
            foreach (var action in actions)
                snapshot = await api.EditScenarioAsync(new SaveAction(id, null, action.Title, action.Description, action.Frames), snapshot);
            return snapshot.Project.Segments.Single(row => row.Id == id);
        }
        var camp = await AddScene("Разграбленный лагерь", 0, 288,
            ("Пустой кофр", "", 144), ("Часы в подкладке", "На ладони часы с замершими стрелками.", 144));
        var measurement = await AddScene("След аномалии", 288, 384,
            ("Нить отклоняется", "Поток прошёл через поляну.", 192), ("На северо-запад", "Эйко обожгла запястье.", 192));
        var rest = await AddScene("Тихий привал у заставы", 672, 288, ("Привал у заставы", "Сумерки.", 288));
        snapshot = await api.EditScenarioAsync(new SaveAnimaticStill(rest.Id, rest.Actions.Single().Id, "@videos/Привал.png"), snapshot);
        var pairs = BackendSceneJoins.Pairs(snapshot.Project).ToArray();
        Assert.Equal((camp.Id, measurement.Id, rest.Id), (pairs[0].Left.SegmentId, pairs[0].Right.SegmentId, pairs[1].Right.SegmentId));

        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        host.PumpUntil(() => model.ProjectReady && !model.NeedsFrame || model.HasError);
        Assert.False(model.HasError, model.Status);
        var originalHistory = files.History();
        var originalBytes = File.ReadAllBytes(files.ProjectPath);
        StoryboardWorkspace OpenJoins()
        {
            host.Click(ButtonIn(host.Window.Navigation, "Раскадровка"));
            var board = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<StoryboardWorkspace>());
            host.PumpUntil(() => !board.NeedsFrame);
            host.Click(ButtonIn(board, "Стыки сцен"));
            host.PumpUntil(() => !board.NeedsFrame);
            return board;
        }
        var board = OpenJoins();
        void Click(string label)
        {
            var button = ButtonIn(board, label);
            Assert.True(button.IsEffectivelyEnabled);
            Reveal(button);
            host.Click(button);
            host.PumpUntil(() => !board.NeedsFrame);
        }
        void Reveal(Element element)
        {
            for (var node = element.Parent; node is not null; node = node.Parent)
                if (node is ScrollViewer scroll) { scroll.BringIntoView(element.Bounds); host.Update(); break; }
        }
        RichTextEditor Field(string label) => Assert.Single(RasterWindow<SceneView>.Walk(board).OfType<RichTextEditor>(),
            editor => editor.AccessibleName == label);
        void Label(string value) => Assert.Contains(RasterWindow<SceneView>.Walk(board).OfType<Text>(),
            text => text.IsVisible && text.Value == value);
        void Fill(string label, string value)
        {
            var field = Field(label);
            Assert.False(field.Options.IsReadOnly);
            Reveal(field);
            host.Click(field);
            host.Input(new(InputKind.KeyDown, Key: UiKey.A, Control: true));
            host.Input(new(InputKind.Text, Text: value));
            Assert.Equal(value, field.Text);
        }
        const string clock = "0287 / 0288 · След аномалии";
        const string retreat = "0671 / 0672 · Тихий привал у заставы";
        const string stopped = "Часы остановились, лежат на ладони Эйко.";
        const string ticking = "Часы идут, цепочка закреплена на поясе Эйко.";
        const string departure = "Поляна. Эйко обожгла запястье, перчатка повреждена.";
        const string arrival = "Застава в сумерках. Лайтер уже помог Эйко.";
        const string bridge = "Бьёрнс велит уходить. У заставы Лайтер снимает перчатку и частично исцеляет ожог.";

        Click(clock);
        Assert.Contains("Часы в подкладке", Field("Кадр · Выход").Text);
        Assert.Contains("Нить отклоняется", Field("Кадр · Вход").Text);
        Label("Кадр 287 · Карточка");
        Label("Кадр 288 · Карточка");
        Label("Оба состояния не заполнены");
        files.AssertProject(originalBytes, originalHistory);
        Click("Уточнить стык");
        Fill("Состояние · Выход", stopped);
        Fill("Состояние · Вход", ticking);
        Click("Сохранить стык");
        Label("Записи различаются · нужна связка или сверка");
        var clockSaved = api.OpenAsync().GetAwaiter().GetResult();
        Assert.Equal(snapshot.Project.Revision + 1, clockSaved.Project.Revision);
        Assert.Equal(originalHistory.Length + 1, files.History().Length);
        Assert.Equal((stopped, ticking, ""), Notes(clockSaved, pairs[0]));

        Click(retreat);
        Assert.Contains("На северо-запад", Field("Кадр · Выход").Text);
        Label("Кадр 671 · Карточка");
        Label("Кадр 672 · Стоп-кадр");
        var picture = Assert.Single(RasterWindow<SceneView>.Walk(board).OfType<Image>(), image => image.AccessibleName == "Вход · кадр 672");
        Assert.NotNull(picture.Source);
        Assert.True(picture.Bounds.X > Field("Кадр · Выход").Bounds.X);
        host.Capture("scene-join-measurement", new { left = 671, right = 672, ClockRevision = clockSaved.Project.Revision });
        host.Resize(840, 660, 1.5f);
        Assert.True(picture.Bounds.Width > 0 && picture.Bounds.X + picture.Bounds.Width <= board.Bounds.X + board.Bounds.Width);
        host.Capture("scene-join-compact", new { left = 671, right = 672 });
        host.Resize(1280, 820, 1);

        Click("Уточнить стык");
        Fill("Состояние · Выход", departure);
        Fill("Состояние · Вход", arrival);
        Fill("Что связывает сцены", bridge);
        host.Input(new(InputKind.KeyDown, Key: UiKey.Escape));
        Assert.False(board.IsDisposed);
        var changed = api.EditScenarioAsync(new SaveSegment(rest.Id, rest.Name, "Сумерки", 0, 0), clockSaved).GetAwaiter().GetResult();
        var conflictBytes = File.ReadAllBytes(files.ProjectPath);
        var conflictHistory = files.History();
        Click("Сохранить стык");
        Label("Проект изменился. Обновите стыки.");
        Assert.False(ButtonIn(board, "Сохранить стык").IsEnabled);
        Assert.Null(picture.Source);
        Assert.Equal(bridge, Field("Что связывает сцены").Text);
        files.AssertProject(conflictBytes, conflictHistory);
        Click("Обновить");
        Assert.NotNull(picture.Source);
        Assert.Equal(departure, Field("Состояние · Выход").Text);
        Assert.Equal(arrival, Field("Состояние · Вход").Text);
        Assert.Equal(bridge, Field("Что связывает сцены").Text);
        Click("Сохранить стык");
        var saved = api.OpenAsync().GetAwaiter().GetResult();
        Assert.Equal(changed.Project.Revision + 1, saved.Project.Revision);
        Assert.Equal(conflictHistory.Length + 1, files.History().Length);
        Label("Переход описан");
        Assert.Equal((departure, arrival, bridge), Notes(saved, pairs[1]));
        Assert.Equal((stopped, ticking, ""), Notes(saved, pairs[0]));
        Assert.Equal(snapshot.Project.Montage.Windows, saved.Project.Montage.Windows);
        Assert.Equal(snapshot.Project.Montage.Clips, saved.Project.Montage.Clips);

        Click(clock);
        Assert.Equal(stopped, Field("Состояние · Выход").Text);
        Click("Уточнить стык");
        Fill("Состояние · Выход", "Отменить это изменение");
        Click("Отменить правку");
        Assert.Equal(stopped, Field("Состояние · Выход").Text);
        var savedHistory = files.History();
        var savedBytes = File.ReadAllBytes(files.ProjectPath);
        Click("К монтажу");
        host.PumpUntil(() => model.SavedFingerprint == saved.Fingerprint && !model.NeedsFrame);
        board = OpenJoins();
        Click(retreat);
        Assert.Equal(bridge, Field("Что связывает сцены").Text);
        Label("Переход описан");
        files.AssertProject(savedBytes, savedHistory);
        Assert.Empty(files.Requests());

        static (string, string, string) Notes(OpenedBackendProject project, SceneJoinWindowPair pair)
        {
            var row = BackendSceneJoins.Read(project.Project)[(pair.Left.Id, pair.Right.Id)];
            return (row.ExitState, row.EntryState, row.Bridge);
        }
    }

    [BackendMediaFact]
    public async Task FullTransfer_FromRaster_StagesRunsStopsRecoversAndRegistersAtMostOnce()
    {
        TrueMoon.Alloy.Rendering.Skia.SkiaNativeLibrary.Initialize();
        using var files = new ExportFixture();
        await files.PrepareAsync(fullTransfer: true);
        const string video = "Черновик.mp4";
        await BackendTrimFixture.RunAsync(Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!,
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=160x120:r=24", "-frames:v", "56",
             "-threads", "1", "-n", files.MediaPath(video)], 2048);
        foreach (var name in new[] { "1.png", "2.png" })
            await BackendTrimFixture.RunAsync(Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!,
                ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=lime:s=160x120", "-frames:v", "1",
                 "-threads", "1", "-n", files.MediaPath(name)], 2048);
        var exe = await files.PrepareTransferWorkerFixtureAsync();
        await using var session = await files.OpenAsync(workspace: true, executable: exe);
        var api = session.Connection;
        var previous = await api.AcquireWriterAsync(await api.OpenAsync());
        var source = previous.Project.Sources.First();
        var baseline = await api.EditScenarioAsync(new ImportVideo(source.Id, "", "PV · черновик", "@videos/" + video), previous);
        var draft = baseline.Project.Sources.First().Takes.Last();
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        var jobs = Path.Combine(Path.GetDirectoryName(files.ProjectPath)!, "Production", "FullTransfer.v1");
        var prompt = "subject_definitions: <Picture 1> is the room. <Picture 2> is the opening. <Video 1> supplies motion.\n" +
            "summary: Follow the selected motion.\nretention_analysis: Keep Picture roles.\ndetailed_description: One continuous shot.\n" +
            "overall_soundscape: N/A\n\nnon_diegetic_music: N/A";
        var request = new FullTransferForm(new('a', 32), source.Id, draft.Id, ulong.MaxValue, prompt,
            [new("@videos/1.png", "Комната"), new("@videos/2.png", "Первый кадр")], 2, "Ритм", "Пауза и поворот.");
        await Refused(request, previous, "revision_conflict");
        await Refused(request with { DraftTakeId = source.Takes[0].Id }, baseline, "transfer_timing");
        await Refused(request with { Images = [new("@videos/../Production/1.png", "Вне корня")], GuidePicture = 1 }, baseline, "access_denied");
        Assert.False(Directory.Exists(jobs));

        using var firstHost = Workspace(session);
        var host = firstHost;
        Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
        host.Click(ButtonIn(host.Window.Navigation, "Сценарий и материалы"));
        var scenario = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>());
        host.PumpUntil(() => !scenario.NeedsFrame);
        Click("Библиотека");
        Click("PV · черновик");
        Click("Подготовить Full Transfer");
        Assert.True(scenario.HasDraft);
        Click("Добавить Picture");
        Fill("Picture 1 · файл из @mount", "@videos/1.png");
        Fill("Picture 1 · роль", "Комната");
        Fill("Picture 2 · файл из @mount", "@videos/2.png");
        Fill("Picture 2 · роль", "Первый кадр");
        Fill("Seed", ulong.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Fill("Picture для Guide 0", "2");
        Fill("Роль Video 1", "Ритм");
        Fill("Фазы движения Video 1", "Пауза и поворот.");
        Click("Подготовить");
        Assert.True(scenario.HasDraft);
        Assert.Contains("Проверьте шесть разделов", scenario.Status);
        Assert.Empty(Directory.GetDirectories(jobs));
        Fill("Промпт Ref2VA", prompt);
        Click("Подготовить");
        Assert.False(scenario.HasDraft, scenario.Status);
        Assert.Contains("без постановки в очередь", scenario.Status);
        var job = Assert.Single(Directory.GetDirectories(jobs));
        var receipt = JsonSerializer.Deserialize<FullTransferReceipt>(File.ReadAllBytes(Path.Combine(job, "Prepared.json")), BackendAdaptation.Json)!;
        var recorded = JsonSerializer.Deserialize<FullTransferRequest>(File.ReadAllBytes(Path.Combine(job, "Request.json")), BackendAdaptation.Json)!;
        Assert.Equal((56, 24, 1280, 736, false, false), (receipt.Frames, receipt.Fps, receipt.Width, receipt.Height, receipt.Staged, receipt.Enqueued));
        Assert.Equal(Hash(File.ReadAllBytes(files.MediaPath(video))), receipt.DraftSha256);
        Assert.Equal((baseline.Project.Revision, baseline.Fingerprint, draft.Id, ulong.MaxValue),
            (recorded.Revision, recorded.Fingerprint, recorded.Draft.TakeId, recorded.Form.Seed));
        var preview = Path.Combine(job, "Preview");
        using var graph = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(preview, "Prepared.api.json")));
        Assert.Equal("1002", graph.RootElement.GetProperty("170").GetProperty("inputs").GetProperty("image")[0].GetString());
        Assert.Equal("172", graph.RootElement.GetProperty("136").GetProperty("inputs").GetProperty("ref_videos.ref_video_0")[0].GetString());
        Assert.Equal(ulong.MaxValue, graph.RootElement.GetProperty("129").GetProperty("inputs").GetProperty("noise_seed").GetUInt64());
        Assert.Equal(receipt.BuildSha256, Hash(File.ReadAllBytes(Path.Combine(preview, "Build.json"))));
        Assert.Equal(receipt, api.PrepareFullTransferAsync(recorded.Form, baseline, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal("transfer_key_conflict", Assert.Throws<BackendCommandException>(() => api.PrepareFullTransferAsync(
            recorded.Form with { MotionNotes = "Другая попытка." }, baseline, CancellationToken.None).GetAwaiter().GetResult()).Code);
        var config = Path.Combine(job, "Prepare.config.json");
        var originalConfig = File.ReadAllBytes(config);
        File.AppendAllText(config, " ");
        try
        {
            Assert.Equal("transfer_damaged", Assert.Throws<BackendCommandException>(() =>
                api.PrepareFullTransferAsync(recorded.Form, baseline, CancellationToken.None).GetAwaiter().GetResult()).Code);
        }
        finally { File.WriteAllBytes(config, originalConfig); }
        Assert.Equal("keep", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(files.ComfyRoot, "input")))));
        Assert.Empty(Directory.GetFiles(job, "*Submission*", SearchOption.AllDirectories));
        files.AssertProject(bytes, history);
        host.Capture("full-transfer-prepared", new { receipt.Id, receipt.Frames, receipt.DraftSha256 });

        var buildPath = Path.Combine(preview, "Build.json");
        var previewBytes = File.ReadAllBytes(buildPath);
        using var build = JsonDocument.Parse(previewBytes);
        var targets = build.RootElement.GetProperty("source_input_hashes").EnumerateArray().Select(row =>
            (Source: row.GetProperty("Source").GetString()!, Target: row.GetProperty("Target").GetString()!,
             Sha: row.GetProperty("SHA256").GetString()!)).ToArray();
        var marker = Path.Combine(job, "Staged.json");
        Click("Открыть задание");
        Assert.Contains("Задание Full Transfer открыто", scenario.Status);
        Assert.False(File.Exists(marker));
        Assert.Equal("transfer_not_staged", RejectRun(receipt, baseline, "launch"));
        Assert.False(Directory.Exists(Path.Combine(job, "Run")));
        Assert.Single(Directory.GetFiles(Path.Combine(files.ComfyRoot, "input")));
        Assert.Equal("revision_conflict", RejectJob(receipt.RequestSha256, previous));
        Assert.Equal("transfer_key_conflict", RejectJob(new('0', 64), baseline));
        var originalImage = File.ReadAllBytes(targets[1].Source);
        File.AppendAllText(targets[1].Source, "substitute");
        try { Assert.Equal("transfer_input_changed", RejectJob(receipt.RequestSha256, baseline)); }
        finally { File.WriteAllBytes(targets[1].Source, originalImage); }
        Assert.All(targets, row => Assert.False(File.Exists(row.Target)));

        File.Copy(targets[0].Source, targets[0].Target);
        File.WriteAllText(targets[^1].Target, "foreign-content");
        Click("Скопировать входы Full Transfer");
        Assert.Contains("другое содержимое", scenario.Status);
        Assert.Equal("foreign-content", File.ReadAllText(targets[^1].Target));
        Assert.False(File.Exists(targets[1].Target));
        Assert.False(File.Exists(marker));
        File.Delete(targets[^1].Target);
        Click("Скопировать входы Full Transfer");
        Assert.Contains("без постановки в очередь", scenario.Status);
        var staged = receipt with { State = "staged", Staged = true };
        Assert.Equal(staged, api.FullTransferJobAsync(receipt.Id, null, baseline, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(staged, api.FullTransferJobAsync(receipt.Id, receipt.RequestSha256, baseline, CancellationToken.None).GetAwaiter().GetResult());
        Assert.All(targets, row => Assert.Equal(row.Sha, Hash(File.ReadAllBytes(row.Target)), ignoreCase: true));
        var markerBytes = File.ReadAllBytes(marker);
        File.Delete(targets[^1].Target);
        Click("Скопировать входы Full Transfer");
        Assert.Equal(targets[^1].Sha, Hash(File.ReadAllBytes(targets[^1].Target)), ignoreCase: true);
        Assert.Equal(markerBytes, File.ReadAllBytes(marker));
        Assert.Equal(previewBytes, File.ReadAllBytes(buildPath));
        Assert.Equal(receipt, api.PrepareFullTransferAsync(recorded.Form, baseline, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Empty(Directory.GetFiles(job, "*Submission*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(files.ComfyRoot, "input"), "*.pending"));
        files.AssertProject(bytes, history);
        host.Capture("full-transfer-inputs-staged", new { receipt.Id, inputCount = targets.Length });

        File.Delete(targets[0].Target);
        Assert.Equal("transfer_not_staged", RejectRun(receipt, baseline, "launch"));
        Assert.False(File.Exists(targets[0].Target));
        Click("Скопировать входы Full Transfer");
        var apiGraph = Path.Combine(preview, "Prepared.api.json");
        var originalGraph = File.ReadAllBytes(apiGraph);
        File.AppendAllText(apiGraph, " ");
        try { Assert.Equal("transfer_damaged", RejectRun(receipt, baseline, "launch")); }
        finally { File.WriteAllBytes(apiGraph, originalGraph); }
        Assert.False(Directory.Exists(Path.Combine(job, "Run")));

        var cancelled = StageOther('b', 1);
        var badEnvironment = StageOther('c', 2);
        var interrupted = StageOther('d', 1);
        var cancelledDir = Path.Combine(jobs, cancelled.Id, "Run");
        Assert.Equal("running", Run(cancelled, baseline, "launch").State);
        Eventually(() => File.Exists(Path.Combine(cancelledDir, "FixtureDriver.pid")), cancelledDir).GetAwaiter().GetResult();
        using var stoppedDriver = OwnerProcess(cancelledDir, "FixtureDriver.pid");
        using var stoppedServer = OwnerProcess(cancelledDir, "server.pid");
        Assert.Equal("transfer_run_busy", RejectRun(receipt, baseline, "launch"));
        Fill("ID задания Full Transfer", cancelled.Id);
        Click("Открыть задание");
        Click("Остановить Full Transfer");
        Assert.Contains("Запуск остановлен", scenario.Status);
        Task.WhenAll(stoppedDriver.WaitForExitAsync(), stoppedServer.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
        Assert.True(stoppedDriver.HasExited && stoppedServer.HasExited);
        Assert.Equal("transfer_already_launched", RejectRun(cancelled, baseline, "launch"));
        Assert.False(File.Exists(Path.Combine(cancelledDir, "Completed.json")));

        var model = Directory.GetFiles(Path.Combine(files.ComfyRoot, "models"), "*.safetensors", SearchOption.AllDirectories).First();
        var modelStamp = File.GetLastWriteTimeUtc(model);
        var badRun = Path.Combine(jobs, badEnvironment.Id, "Run");
        File.SetLastWriteTimeUtc(model, modelStamp.AddMinutes(5));
        try
        {
            Run(badEnvironment, baseline, "launch");
            Eventually(() => Run(badEnvironment, baseline, "status").State == "failed", badRun).GetAwaiter().GetResult();
            Assert.Contains("Model metadata changed", File.ReadAllText(Path.Combine(badRun, "Runner.failure.log")));
            Assert.False(File.Exists(Path.Combine(badRun, "server.pid")));
            Assert.False(File.Exists(Path.Combine(badRun, "Submission.claim")));
        }
        finally { File.SetLastWriteTimeUtc(model, modelStamp); }

        Fill("ID задания Full Transfer", receipt.Id);
        Click("Открыть задание");
        Click("Запустить Full Transfer на GPU");
        var runDir = Path.Combine(job, "Run");
        Eventually(() => Run(receipt, baseline, "status").State == "completed", runDir).GetAwaiter().GetResult();
        Click("Обновить статус Full Transfer");
        Assert.Contains("MP4 проверен", scenario.Status);
        var resultPath = Path.Combine(runDir, "output", "Final.mp4");
        var outputHash = Hash(File.ReadAllBytes(resultPath));
        var completed = Run(receipt, baseline, "status");
        Assert.Equal(outputHash, completed.OutputSha256, ignoreCase: true);
        Assert.Equal("transfer_already_launched", RejectRun(receipt, baseline, "launch"));
        Assert.Single(Directory.GetFiles(runDir, "Submission.claim"));
        Assert.Equal(markerBytes, File.ReadAllBytes(marker));
        Assert.Equal(previewBytes, File.ReadAllBytes(buildPath));
        files.AssertProject(bytes, history);
        host.Capture("full-transfer-run-completed", new { receipt.Id, outputHash });

        var otherRun = Path.Combine(jobs, interrupted.Id, "Run");
        Run(interrupted, baseline, "launch");
        Eventually(() => File.Exists(Path.Combine(otherRun, "FixtureDriver.pid")), otherRun).GetAwaiter().GetResult();
        using var interruptedDriver = OwnerProcess(otherRun, "FixtureDriver.pid");
        using var interruptedServer = OwnerProcess(otherRun, "server.pid");
        using (var backend = System.Diagnostics.Process.GetProcessById(baseline.Identity.ProcessId))
        {
            Assert.Equal(exe, backend.MainModule!.FileName, ignoreCase: true);
            backend.Kill();
            backend.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
        }
        Task.WhenAll(interruptedDriver.WaitForExitAsync(), interruptedServer.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
        var reopened = files.OpenAsync(workspace: true, executable: exe).GetAwaiter().GetResult();
        try { VerifyRecoveryAndCollect(reopened); }
        finally { reopened.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        void VerifyRecoveryAndCollect(ExportSession recovered)
        {
            var connection = recovered.Connection;
            var current = connection.AcquireWriterAsync(connection.OpenAsync().GetAwaiter().GetResult()).GetAwaiter().GetResult();
            Assert.Equal("interrupted", connection.FullTransferRunAsync(interrupted.Id, interrupted.RequestSha256, "status",
                current, CancellationToken.None).GetAwaiter().GetResult().State);
            Assert.Equal("transfer_already_launched", Assert.Throws<BackendCommandException>(() =>
                connection.FullTransferRunAsync(interrupted.Id, interrupted.RequestSha256, "launch", current, CancellationToken.None).GetAwaiter().GetResult()).Code);
            Assert.Single(Directory.GetFiles(otherRun, "Submission.claim"));
            Assert.Equal(completed, connection.FullTransferRunAsync(receipt.Id, receipt.RequestSha256, "status", current, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal("revision_conflict", Assert.Throws<BackendCommandException>(() =>
                connection.CollectFullTransferAsync(receipt.Id, receipt.RequestSha256, previous, CancellationToken.None).GetAwaiter().GetResult()).Code);
            var originalMp4 = File.ReadAllBytes(resultPath);
            try
            {
                originalMp4[^1] ^= 1;
                File.WriteAllBytes(resultPath, originalMp4);
                Assert.Equal("transfer_damaged", Assert.Throws<BackendCommandException>(() =>
                    connection.CollectFullTransferAsync(receipt.Id, receipt.RequestSha256, current, CancellationToken.None).GetAwaiter().GetResult()).Code);
            }
            finally
            {
                originalMp4[^1] ^= 1;
                File.WriteAllBytes(resultPath, originalMp4);
            }
            files.AssertProject(bytes, history);
            using var collectHost = Workspace(recovered);
            host = collectHost;
            Ready(host, recovered.App.Services.Resolve<SceneViewModel>()!);
            host.Click(ButtonIn(host.Window.Navigation, "Сценарий и материалы"));
            scenario = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>());
            host.PumpUntil(() => !scenario.NeedsFrame);
            Click("Библиотека");
            Click("PV · черновик");
            Fill("ID задания Full Transfer", receipt.Id);
            Click("Открыть задание");
            Click("Сохранить финальный дубль");
            Assert.Contains("Финальный дубль сохранён", scenario.Status);
            var written = connection.OpenAsync().GetAwaiter().GetResult();
            Assert.Equal(current.Project.Revision + 1, written.Project.Revision);
            Assert.Equal(history.Length + 1, files.History().Length);
            var newTake = Assert.Single(written.Project.Sources.Single(row => row.Id == source.Id).Takes,
                row => row.Id == BackendFullTransfer.FinalTakeId(receipt.Id));
            var pair = BackendTakePairs.Read(newTake)!;
            Assert.Equal(recorded.Draft, pair.Draft);
            Assert.Equal(outputHash, pair.Final.Sha256);
            Assert.Equal(current.Project.Montage.Clips.Select(row => (row.Id, row.TakeId)), written.Project.Montage.Clips.Select(row => (row.Id, row.TakeId)));
            var repeated = connection.CollectFullTransferAsync(receipt.Id, receipt.RequestSha256, written, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(written.Fingerprint, repeated.Fingerprint);
            Assert.Equal(history.Length + 1, files.History().Length);
            host.Capture("full-transfer-final-take", new { receipt.Id, TakeId = newTake.Id, outputHash });
        }

        FullTransferRunState Run(FullTransferReceipt row, OpenedBackendProject head, string op) =>
            api.FullTransferRunAsync(row.Id, row.RequestSha256, op, head, CancellationToken.None).GetAwaiter().GetResult();
        string RejectRun(FullTransferReceipt row, OpenedBackendProject head, string op) =>
            Assert.Throws<BackendCommandException>(() => Run(row, head, op)).Code;
        FullTransferReceipt StageOther(char key, ulong seed)
        {
            var second = api.PrepareFullTransferAsync(recorded.Form with { ClientKey = new(key, 32), Seed = seed },
                baseline, CancellationToken.None).GetAwaiter().GetResult();
            return api.FullTransferJobAsync(second.Id, second.RequestSha256, baseline, CancellationToken.None).GetAwaiter().GetResult();
        }
        static async Task Eventually(Func<bool> condition, string directory)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            while (!condition())
            {
                if (deadline.IsCancellationRequested)
                {
                    var logs = Directory.GetFiles(directory, "*.log").Select(path => Path.GetFileName(path) + "\n" + File.ReadAllText(path));
                    Assert.Fail(string.Join("\n", logs.Prepend(directory)));
                }
                await Task.Delay(50);
            }
        }
        static System.Diagnostics.Process OwnerProcess(string directory, string file)
        {
            var process = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(directory, file)),
                System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(Path.GetFullPath(Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON")!), process.MainModule!.FileName, ignoreCase: true);
            return process;
        }

        string RejectJob(string sha, OpenedBackendProject head) => Assert.Throws<BackendCommandException>(() =>
            api.FullTransferJobAsync(receipt.Id, sha, head, CancellationToken.None).GetAwaiter().GetResult()).Code;
        async Task Refused(FullTransferForm form, OpenedBackendProject head, string code) =>
            Assert.Equal(code, (await Assert.ThrowsAsync<BackendCommandException>(() => api.PrepareFullTransferAsync(form, head, CancellationToken.None))).Code);
        void Click(string label)
        {
            var button = ButtonIn(scenario, label);
            Assert.True(button.IsVisible && button.IsEffectivelyEnabled);
            Reveal(button);
            host.Click(button);
            host.PumpUntil(() => !scenario.NeedsFrame, TimeSpan.FromSeconds(30));
        }
        void Fill(string label, string value)
        {
            var field = Assert.Single(RasterWindow<SceneView>.Walk(scenario), row => row.AccessibleName == label && row is TextBox or RichTextEditor);
            Reveal(field);
            host.Click(field);
            host.Input(new(InputKind.KeyDown, Key: UiKey.A, Control: true));
            var lines = value.ReplaceLineEndings("\n").Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0) host.Input(new(InputKind.KeyDown, Key: UiKey.Enter));
                if (lines[index].Length > 0) host.Input(new(InputKind.Text, Text: lines[index]));
            }
            Assert.Equal(value.ReplaceLineEndings("\n"), (field switch
            {
                TextBox box => box.Value,
                RichTextEditor editor => editor.Text,
                _ => throw new InvalidOperationException()
            }).ReplaceLineEndings("\n"));
        }
        void Reveal(Element control)
        {
            for (var node = control.Parent; node is not null; node = node.Parent)
                if (node is ScrollViewer scroll) { scroll.BringIntoView(control.Bounds); host.Update(); break; }
        }
    }

    [BackendMediaFact]
    public async Task TakePairs_FromRaster_RecordFullTransferAndRejectReplacedInput()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        await BackendTrimFixture.RunAsync(Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!,
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=green:s=80x60:r=24:d=1", "-threads", "1", "-n", files.MediaPath("Короткий.mp4")], 2048);
        await using var session = await files.OpenAsync(workspace: true);
        var api = session.Connection;
        var baseline = await api.AcquireWriterAsync(await api.OpenAsync());
        var source = baseline.Project.Sources.First();
        var draft = source.Takes.First();
        baseline = await api.EditScenarioAsync(new ImportVideo(source.Id, "", "Полный дубль", "@videos/Синий.mp4"), baseline);
        var final = baseline.Project.Sources.First().Takes.Last();
        baseline = await api.EditScenarioAsync(new ImportVideo(source.Id, "", "Короткий дубль", "@videos/Короткий.mp4"), baseline);
        var shortTake = baseline.Project.Sources.First().Takes.Last();
        var link = new LinkDraftFinalTake(source.Id, draft.Id, final.Id, BackendTakePairs.FullTransfer);
        byte[] before = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        await Reject(link with { FinalTakeId = shortTake.Id }, baseline, "take_pair_timing");
        await Reject(link with { DraftTakeId = final.Id }, baseline, "invalid_take_pair");
        files.AssertProject(before, history);

        using var host = Workspace(session);
        Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
        host.Click(ButtonIn(host.Window.Navigation, "Сценарий и материалы"));
        var scenario = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>());
        host.PumpUntil(() => !scenario.NeedsFrame);
        Click("Библиотека");
        Click("Полный дубль");
        Click("Связать с черновиком");
        Click(BackendScenarioEdit.TakeName(draft));
        Click("Full Transfer");
        Click("Сохранить");
        Assert.False(scenario.HasDraft, scenario.Status);
        var saved = api.OpenAsync().GetAwaiter().GetResult();
        var pair = BackendTakePairs.Read(saved.Project.Sources.First().Takes.Single(take => take.Id == final.Id))!;
        Assert.Equal(("full_transfer", draft.Id, final.Id, 48, 48), (pair.Method, pair.Draft.TakeId, pair.Final.TakeId, pair.Draft.Frames, pair.Final.Frames));
        var originalDraft = File.ReadAllBytes(files.MediaPath("Красный.mp4"));
        Assert.Equal(Hash(originalDraft), pair.Draft.Sha256);
        Assert.Equal(Hash(File.ReadAllBytes(files.MediaPath("Синий.mp4"))), pair.Final.Sha256);
        Assert.Equal(baseline.Project.Revision + 1, saved.Project.Revision);
        Assert.Equal(history.Length + 1, files.History().Length);
        Assert.Contains(files.History(), row => row.SequenceEqual(before));
        Assert.Equal(baseline.Project.Montage.Clips, saved.Project.Montage.Clips);
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var savedHistory = files.History();
        Assert.Equal(saved.Fingerprint, api.EditScenarioAsync(link, saved).GetAwaiter().GetResult().Fingerprint);
        Assert.Equal("take_pair_recorded", Assert.Throws<BackendCommandException>(() =>
            api.EditScenarioAsync(link with { Method = BackendTakePairs.Direct }, saved).GetAwaiter().GetResult()).Code);
        Assert.Equal("revision_conflict", Assert.Throws<BackendCommandException>(() =>
            api.EditScenarioAsync(link with { FinalTakeId = shortTake.Id, Method = BackendTakePairs.Direct }, baseline).GetAwaiter().GetResult()).Code);

        var index = Assert.Single(RasterWindow<SceneView>.Walk(scenario).OfType<TextBox>(), row => row.AccessibleName == "Кадр дубля");
        Reveal(index);
        host.Click(index);
        host.Input(new(InputKind.KeyDown, Key: UiKey.A, Control: true));
        host.Input(new(InputKind.Text, Text: "23"));
        Click("Сравнить с черновиком");
        var images = RasterWindow<SceneView>.Walk(scenario).OfType<Image>().Where(row => row.Source is not null).ToArray();
        Assert.Equal(2, images.Length);
        Assert.All(images, row => Assert.EndsWith("23/47", row.AccessibleName));
        try
        {
            File.Copy(files.MediaPath("Синий.mp4"), files.MediaPath("Красный.mp4"), overwrite: true);
            Click("Сравнить с черновиком");
            Assert.Contains("Видео отличается от записанной версии", scenario.Status);
            Assert.All(images, row => Assert.Null(row.Source));
        }
        finally { File.WriteAllBytes(files.MediaPath("Красный.mp4"), originalDraft); }
        files.AssertProject(bytes, savedHistory);
        var direct = api.EditScenarioAsync(link with { FinalTakeId = shortTake.Id, Method = BackendTakePairs.Direct }, saved).GetAwaiter().GetResult();
        var directSource = direct.Project.Sources.First();
        var alternative = Assert.IsType<DraftFinalPair>(BackendTakePairs.Read(directSource.Takes.Single(take => take.Id == shortTake.Id)));
        Assert.Equal(("direct", draft.Id, 48, 24), (alternative.Method, alternative.Draft.TakeId, alternative.Draft.Frames, alternative.Final.Frames));
        Assert.Equal(pair, BackendTakePairs.Read(directSource.Takes.Single(take => take.Id == final.Id)));
        Assert.Empty(files.Requests());

        async Task Reject(ScenarioCommand command, OpenedBackendProject head, string code) =>
            Assert.Equal(code, (await Assert.ThrowsAsync<BackendCommandException>(() => api.EditScenarioAsync(command, head))).Code);
        void Click(string label)
        {
            var button = ButtonIn(scenario, label);
            Assert.True(button.IsVisible && button.IsEffectivelyEnabled);
            Reveal(button);
            host.Click(button);
            host.PumpUntil(() => !scenario.NeedsFrame);
        }
        void Reveal(Element element)
        {
            for (var node = element.Parent; node is not null; node = node.Parent)
                if (node is ScrollViewer scroll) { scroll.BringIntoView(element.Bounds); host.Update(); break; }
        }
    }

    [BackendMediaFact]
    public async Task ReferenceVersions_FromRaster_ReassignSceneAndKeepObservedTakeInput()
    {
        TrueMoon.Alloy.Rendering.Skia.SkiaNativeLibrary.Initialize();
        using var files = new ExportFixture();
        await files.PrepareAsync();
        WritePng("Watch-1.png", SkiaSharp.SKColors.Red);
        WritePng("Watch-2.png", SkiaSharp.SKColors.Lime);
        await using var session = await files.OpenAsync(workspace: true);
        var api = session.Connection;
        var opened = await api.AcquireWriterAsync(await api.OpenAsync());
        var legacy = opened.Project.Sources.First();
        Assert.Empty(BackendReferenceLibrary.Read(opened.Project).Entries);
        Assert.Empty(BackendReferenceLibrary.Inputs(legacy.Takes[0]));
        var baseline = await api.EditScenarioAsync(new SaveSegment(null, "Привал с часами", "", 800, 160), opened);
        var segment = baseline.Project.Segments.Last();
        baseline = await api.EditScenarioAsync(new SaveAction(segment.Id, null, "Ладонь", "", 12), baseline);
        var action = baseline.Project.Segments.Last().Actions.Single();
        baseline = await api.EditScenarioAsync(new LinkSource(segment.Id, action.Id, legacy.Id, null), baseline);
        baseline = await api.EditScenarioAsync(new PlaceClip(segment.Id, action.Id, legacy.Id, legacy.Takes[0].Id, 800, 12, 0), baseline);
        var create = new AddReferenceVersion(null, "Часы Эйко", "prop", "До завода", "@videos/Watch-1.png");
        var first = await api.EditScenarioAsync(create, baseline);
        var entry = Assert.Single(BackendReferenceLibrary.Read(first.Project).Entries);
        Assert.Equal(Hash(File.ReadAllBytes(files.MediaPath("Watch-1.png"))), entry.Versions[0].Sha256);
        Assert.Equal(first.Fingerprint, (await api.EditScenarioAsync(create, first)).Fingerprint);
        var second = await api.EditScenarioAsync(new AddReferenceVersion(entry.Id, null, null, "После завода", "@videos/Watch-2.png"), first);
        var assign = new AssignSceneReference(segment.Id, null, entry.Id, 1);
        var assigned = await api.EditScenarioAsync(assign, second);
        var pin = new RecordTakeReference(legacy.Id, legacy.Takes[0].Id, entry.Id, 1);
        var originalImage = File.ReadAllBytes(files.MediaPath("Watch-1.png"));
        var beforePin = File.ReadAllBytes(files.ProjectPath);
        var beforePinHistory = files.History();
        WritePng("Watch-1.png", SkiaSharp.SKColors.Blue);
        try
        {
            await Reject(pin, assigned, "reference_changed");
            await Reject(new AddReferenceVersion(entry.Id, null, null, "Заменённый файл", "@videos/Watch-1.png"),
                assigned, "reference_version_exists");
            files.AssertProject(beforePin, beforePinHistory);
        }
        finally { File.WriteAllBytes(files.MediaPath("Watch-1.png"), originalImage); }
        var pinned = await api.EditScenarioAsync(pin, assigned);
        Assert.Equal(pinned.Fingerprint, (await api.EditScenarioAsync(pin, pinned)).Fingerprint);
        var before = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        await Reject(assign with { Version = 2 }, assigned, "revision_conflict");
        await Reject(pin with { Version = 2 }, pinned, "reference_input_recorded");
        await Reject(assign with { Version = 9 }, pinned, "invalid_reference_target");
        await Reject(new AddReferenceVersion(entry.Id, null, null, "Та же опора", "@videos/Watch-1.png"), pinned, "reference_version_exists");
        files.AssertProject(before, history);

        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        Ready(host, model);
        host.Click(ButtonIn(host.Window.Navigation, "Сценарий и материалы"));
        var scenario = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<ScenarioWorkspace>());
        host.PumpUntil(() => !scenario.NeedsFrame);
        Click(scenario, segment.Name);
        Click(scenario, "Эталоны");
        Click(scenario, "v2 · После завода");
        Click(scenario, "Назначить сцене");
        Click(scenario, "Сохранить");
        Assert.False(scenario.HasDraft, scenario.Status);
        var changed = api.OpenAsync().GetAwaiter().GetResult();
        Assert.Equal(pinned.Project.Revision + 1, changed.Project.Revision);
        Assert.Equal(history.Length + 1, files.History().Length);
        Assert.Contains(files.History(), item => item.SequenceEqual(before));
        Assert.Equal(2, Assert.Single(BackendReferenceLibrary.Read(changed.Project).For(segment.Id, action.Id)).Version.Number);
        Assert.Equal(1, Assert.Single(BackendReferenceLibrary.Inputs(changed.Project.Sources.First().Takes[0])).Version.Number);
        Assert.Equal(pinned.Project.Montage.Clips, changed.Project.Montage.Clips);
        Assert.Equal(ProjectTakeJson(pinned), ProjectTakeJson(changed));

        Click(scenario, "К монтажу");
        host.PumpUntil(() => !model.NeedsFrame);
        host.Click(ButtonIn(host.Window.Navigation, "Раскадровка"));
        var board = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<StoryboardWorkspace>());
        host.PumpUntil(() => !board.NeedsFrame);
        Click(board, "0800 · Ладонь");
        var labels = RasterWindow<SceneView>.Walk(board).OfType<Text>().Where(row => row.IsVisible).Select(row => row.Value).ToArray();
        Assert.Contains("Сцена · Часы Эйко · v2", labels);
        Assert.Contains("Дубль · Часы Эйко · v1", labels);
        host.Capture("scene-reference-and-original-take-input", new { changed.Project.Revision });
        Assert.Empty(files.Requests());

        void WritePng(string name, SkiaSharp.SKColor color)
        {
            using var bitmap = new SkiaSharp.SKBitmap(40, 24);
            bitmap.Erase(color);
            using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(files.MediaPath(name), png.ToArray());
        }
        async Task Reject(ScenarioCommand command, OpenedBackendProject head, string code) =>
            Assert.Equal(code, (await Assert.ThrowsAsync<BackendCommandException>(() => api.EditScenarioAsync(command, head))).Code);
        string ProjectTakeJson(OpenedBackendProject project) => JsonSerializer.Serialize(project.Project.Sources.First().Takes[0]);
        void Click(Element root, string label)
        {
            var button = ButtonIn(root, label);
            Assert.True(button.IsVisible && button.IsEffectivelyEnabled);
            for (var node = button.Parent; node is not null; node = node.Parent)
                if (node is ScrollViewer scroll) { scroll.BringIntoView(button.Bounds); host.Update(); break; }
            host.Click(button);
            host.PumpUntil(() => !((IUiFrameParticipant)root).NeedsFrame);
        }
    }

    [BackendMediaFact]
    public async Task Storyboard_PlanJoins_CompareLastTakeFrameWithStillInsideScene()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        const string stillFile = "Опора часов.png";
        await BackendTrimFixture.RunAsync(Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!,
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=lime:s=160x120", "-frames:v", "1",
             "-threads", "1", "-n", files.MediaPath(stillFile)], 2048);
        var stillSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(files.MediaPath(stillFile))));
        await using var session = await files.OpenAsync(workspace: true);
        var api = session.Connection;
        var snapshot = await api.AcquireWriterAsync(await api.OpenAsync());
        var source = snapshot.Project.Sources.First();
        var take = source.Takes.First(row => row.VideoAssetId is not null);
        snapshot = await api.EditScenarioAsync(new SaveSegment(null, "Тихий привал", "", 800, 160), snapshot);
        var segment = snapshot.Project.Segments.Last();
        snapshot = await api.EditScenarioAsync(new SaveAction(segment.Id, null, "Духи у ладони", "", 12), snapshot);
        var action = snapshot.Project.Segments.Last().Actions.Single();
        snapshot = await api.EditScenarioAsync(new LinkSource(segment.Id, action.Id, source.Id, "Дубль привала"), snapshot);
        snapshot = await api.EditScenarioAsync(new PlaceClip(segment.Id, action.Id, source.Id, take.Id, 876, 12, 4), snapshot);
        var clip = snapshot.Project.Montage.Clips.Last();
        snapshot = await api.EditScenarioAsync(new SaveAction(segment.Id, null, "Ход часов", "", 48), snapshot);
        var still = snapshot.Project.Segments.Last().Actions.Last();
        snapshot = await api.EditScenarioAsync(new SaveAnimaticStill(segment.Id, still.Id, "@videos/" + stillFile), snapshot);
        var originalBytes = File.ReadAllBytes(files.ProjectPath);
        var originalHistory = files.History();

        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        Ready(host, model);
        host.Click(ButtonIn(host.Window.Navigation, "Раскадровка"));
        var board = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<StoryboardWorkspace>());
        host.PumpUntil(() => !board.NeedsFrame);
        Click("Стыки планов");
        Click("0887 / 0888 · Ход часов");
        Label("Кадры 887 / 888 · Без паузы");
        Label("Кадр 887 · Видео");
        Label("Исходный кадр 15");
        Label("Кадр 888 · Стоп-кадр");
        Label($"SHA {stillSha[..8]}");
        var left = Picture("Выход · кадр 887");
        var right = Picture("Вход · кадр 888");
        Assert.NotNull(left.Source);
        Assert.NotNull(right.Source);
        Assert.True(left.Bounds.X < right.Bounds.X);
        Assert.False(ButtonIn(board, "Уточнить стык").IsVisible);
        Assert.True(ButtonIn(board, "Замечание к выходу").IsEffectivelyEnabled);
        Assert.True(ButtonIn(board, "Замечание ко входу").IsEffectivelyEnabled);
        using (var raster = SkiaSharp.SKBitmap.Decode(host.Capture("plan-join-887-888", new { exit = 887, entry = 888 })))
        {
            var videoPixel = Center(raster, left);
            var stillPixel = Center(raster, right);
            Assert.True(videoPixel.Red > 200 && videoPixel.Green < 30, videoPixel.ToString());
            Assert.True(stillPixel.Green > 200 && stillPixel.Red < 30, stillPixel.ToString());
        }
        host.Resize(840, 660, 1.5f);
        Assert.True(right.Bounds.Width > 0 && right.Bounds.X + right.Bounds.Width <= board.Bounds.X + board.Bounds.Width);
        var back = ButtonIn(board, "К монтажу");
        Assert.True(back.Bounds.X + back.Bounds.Width <= back.Parent!.Bounds.X + back.Parent.Bounds.Width);
        host.Capture("plan-join-compact", new { exit = 887, entry = 888 });
        host.Resize(1280, 820, 1);
        files.AssertProject(originalBytes, originalHistory);

        var changed = api.MoveAsync(clip.Id, 880, snapshot.Project.Revision, snapshot.Fingerprint).GetAwaiter().GetResult();
        var movedBytes = File.ReadAllBytes(files.ProjectPath);
        var movedHistory = files.History();
        Click("0887 / 0888 · Ход часов");
        Label("Проект изменился. Обновите стыки.");
        Assert.Null(left.Source);
        Assert.Null(right.Source);
        Assert.False(ButtonIn(board, "Замечание к выходу").IsVisible);
        Click("Обновить");
        Label("Кадр 891 · Видео");
        Label("Исходный кадр 15");
        Label("Кадр 892 · Стоп-кадр");
        Label($"SHA {stillSha[..8]}");
        Assert.NotNull(Picture("Выход · кадр 891").Source);
        Assert.NotNull(Picture("Вход · кадр 892").Source);
        Assert.Equal(changed.Fingerprint, board.ProjectFingerprint);
        Assert.Equal(originalHistory.Length + 1, movedHistory.Length);
        files.AssertProject(movedBytes, movedHistory);
        Assert.Empty(files.Requests());

        void Click(string label)
        {
            var button = ButtonIn(board, label);
            Assert.True(button.IsVisible && button.IsEffectivelyEnabled);
            for (var node = button.Parent; node is not null; node = node.Parent)
                if (node is ScrollViewer scroll) { scroll.BringIntoView(button.Bounds); host.Update(); break; }
            host.Click(button);
            host.PumpUntil(() => !board.NeedsFrame);
        }
        void Label(string value) => Assert.Contains(RasterWindow<SceneView>.Walk(board).OfType<Text>(),
            row => row.IsVisible && row.Value == value);
        Image Picture(string name) => Assert.Single(RasterWindow<SceneView>.Walk(board).OfType<Image>(), row => row.AccessibleName == name);
        static SkiaSharp.SKColor Center(SkiaSharp.SKBitmap raster, Image image) =>
            raster.GetPixel((int)(image.Bounds.X + image.Bounds.Width / 2), (int)(image.Bounds.Y + image.Bounds.Height / 2));
    }

    [BackendMediaFact]
    public async Task Storyboard_FrameNotes_FollowMovedFrames_AndKeepChangedTargetsForReview()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        foreach (var (color, name) in new[] { ("lime", "Опора часов.png"), ("red", "Новая опора.png") })
            await BackendTrimFixture.RunAsync(Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!,
                ["-nostdin", "-v", "error", "-f", "lavfi", "-i", $"color=c={color}:s=160x120", "-frames:v", "1",
                 "-threads", "1", "-n", files.MediaPath(name)], 2048);
        await using var session = await files.OpenAsync(workspace: true);
        var api = session.Connection;
        var snapshot = await api.AcquireWriterAsync(await api.OpenAsync());
        var source = snapshot.Project.Sources.First();
        var take = source.Takes.First(row => row.VideoAssetId is not null);
        snapshot = await api.EditScenarioAsync(new SaveSegment(null, "Тихий привал", "", 800, 160), snapshot);
        var segment = snapshot.Project.Segments.Last();
        snapshot = await api.EditScenarioAsync(new SaveAction(segment.Id, null, "Духи у ладони", "", 12), snapshot);
        var action = snapshot.Project.Segments.Last().Actions.Single();
        snapshot = await api.EditScenarioAsync(new LinkSource(segment.Id, action.Id, source.Id, "Дубль привала"), snapshot);
        snapshot = await api.EditScenarioAsync(new PlaceClip(segment.Id, action.Id, source.Id, take.Id, 876, 12, 4), snapshot);
        var clip = snapshot.Project.Montage.Clips.Last();
        snapshot = await api.EditScenarioAsync(new SaveAction(segment.Id, null, "Ход часов", "", 48), snapshot);
        var still = snapshot.Project.Segments.Last().Actions.Last();
        snapshot = await api.EditScenarioAsync(new SaveAnimaticStill(segment.Id, still.Id, "@videos/Опора часов.png"), snapshot);
        var initialHistory = files.History();

        using var host = Workspace(session);
        var model = session.App.Services.Resolve<SceneViewModel>()!;
        Ready(host, model);
        StoryboardWorkspace OpenBoard()
        {
            host.Click(ButtonIn(host.Window.Navigation, "Раскадровка"));
            var opened = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<StoryboardWorkspace>());
            host.PumpUntil(() => !opened.NeedsFrame);
            return opened;
        }
        var board = OpenBoard();
        void Reveal(Element element)
        {
            for (var node = element.Parent; node is not null; node = node.Parent)
                if (node is ScrollViewer scroll) { scroll.BringIntoView(element.Bounds); host.Update(); break; }
        }
        void Click(string label)
        {
            var button = Assert.Single(RasterWindow<SceneView>.Walk(board).OfType<Button>(), item => item.IsVisible && item.Value == label);
            Assert.True(button.IsEffectivelyEnabled);
            Reveal(button);
            host.Click(button);
            host.PumpUntil(() => !board.NeedsFrame);
        }
        void Fill(Element field, string value)
        {
            Reveal(field);
            host.Click(field);
            host.Input(new(InputKind.KeyDown, Key: UiKey.A, Control: true));
            host.Input(new(InputKind.Text, Text: value));
        }
        RichTextEditor Note() => Assert.Single(RasterWindow<SceneView>.Walk(board).OfType<RichTextEditor>(),
            editor => editor.AccessibleName == "Текст замечания");
        void Save(string note)
        {
            Click("Замечание к кадру");
            Fill(Note(), note);
            Assert.Equal(note, Note().Text);
            Click("Сохранить замечание");
        }
        void HasFrame(int frame)
        {
            Assert.Contains(RasterWindow<SceneView>.Walk(board).OfType<Image>(), image =>
                image.Source is not null && image.AccessibleName == $"Закреплённый кадр · {frame}");
        }
        void NeedsReview(string reason, string note)
        {
            Assert.Contains(RasterWindow<SceneView>.Walk(board).OfType<Text>(), text => text.Value == "Нужна перепроверка · " + reason);
            Assert.DoesNotContain(RasterWindow<SceneView>.Walk(board).OfType<Image>(),
                image => image.AccessibleName.StartsWith("Закреплённый кадр") && image.Source is not null);
            Assert.Equal(note, Note().Text);
        }
        const string videoNote = "Проверить положение руки Эйко.";
        const string stillNote = "Уточнить видимость царапины на крышке.";
        Click("0876 · Духи у ладони");
        Fill(Assert.Single(RasterWindow<SceneView>.Walk(board).OfType<TextBox>(), row => row.AccessibleName == "Кадр плана"), "887");
        Click("Показать кадр");
        Save(videoNote);
        HasFrame(887);
        Click("Планы");
        Click("0888 · Ход часов");
        Click("Замечание к кадру");
        Fill(Note(), stillNote);
        var afterVideoNote = api.OpenAsync().GetAwaiter().GetResult();
        var concurrent = api.EditScenarioAsync(new SaveSegment(segment.Id, segment.Name, "Правка при открытом замечании", 0, 0), afterVideoNote)
            .GetAwaiter().GetResult();
        var concurrentHistory = files.History();
        var concurrentBytes = File.ReadAllBytes(files.ProjectPath);
        Click("Сохранить замечание");
        Assert.Contains(RasterWindow<SceneView>.Walk(board).OfType<Text>(), row => row.Value == "Проект изменился. Обновите замечания.");
        Assert.False(ButtonIn(board, "Сохранить замечание").IsEnabled);
        Assert.Equal(stillNote, Note().Text);
        files.AssertProject(concurrentBytes, concurrentHistory);
        Click("Обновить");
        Assert.Equal(stillNote, Note().Text);
        Click("Сохранить замечание");
        HasFrame(888);
        var saved = api.OpenAsync().GetAwaiter().GetResult();
        Assert.Equal(concurrent.Project.Revision + 1, saved.Project.Revision);
        Assert.Equal(snapshot.Project.Revision + 3, saved.Project.Revision);
        Assert.Equal(initialHistory.Length + 3, files.History().Length);
        var notes = BackendFrameNotes.Read(saved.Project);
        Assert.Equal(2, notes.Length);
        Assert.Equal((clip.Id, 15, 887, 11), (notes[0].Anchor.ClipId, notes[0].Anchor.SourceFrame, notes[0].Anchor.OriginalFrame, notes[0].Anchor.Offset));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(files.MediaPath("Опора часов.png")))), notes[1].Anchor.Sha256);

        var moved = api.MoveAsync(clip.Id, 880, saved.Project.Revision, saved.Fingerprint).GetAwaiter().GetResult();
        Click("Обновить");
        HasFrame(892);
        Click("0891 · " + videoNote);
        HasFrame(891);
        host.Capture("frame-note-moved", new { from = 887, to = 891, native = 15 });
        Click("К кадру на шкале");
        host.PumpUntil(() => model.Scene?.Clip.Id == clip.Id && model.Idle || model.HasError);
        Assert.False(model.HasError, model.Status);
        Assert.Equal((891d, 15), (model.TimelineCursor, model.FrameIndex));

        board = OpenBoard();
        Click("Замечания");
        var trimmed = api.TrimAsync(clip.Id, 4, 15, 880, moved.Project.Revision, moved.Fingerprint).GetAwaiter().GetResult();
        Click("Обновить");
        Click("0887 · " + videoNote);
        NeedsReview("Кадр вышел из плана или материал был заменён.", videoNote);
        Assert.False(ButtonIn(board, "К кадру на шкале").IsVisible);
        Click("0891 · " + stillNote);
        HasFrame(891);
        File.Copy(files.MediaPath("Новая опора.png"), files.MediaPath("Опора часов.png"), overwrite: true);
        Click("К кадру на шкале");
        NeedsReview("Версия кадра изменилась.", stillNote);
        host.Capture("frame-note-replaced", new { original = 888, timeline = 891 });
        var final = api.OpenAsync().GetAwaiter().GetResult();
        Assert.Equal(trimmed.Fingerprint, final.Fingerprint);
        Assert.Equal(notes, BackendFrameNotes.Read(final.Project));
        Assert.Equal(initialHistory.Length + 5, files.History().Length);
        Assert.Empty(files.Requests());
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
        internal string ComfyRoot => Path.Combine(Root, "comfy-fixture");
        internal string MediaPath(string name) => Path.Combine(Media, name);
        private static string Ffmpeg => Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!;
        private static string Ffprobe => Environment.GetEnvironmentVariable("STUDIO_TEST_FFPROBE")!;
        internal string[] Requests() => Directory.GetFiles(Exports, "Request.json", SearchOption.AllDirectories);
        internal string Checkpoint() => Directory.GetFiles(Exports, "ClientKey", SearchOption.AllDirectories).Single();
        internal string JobDirectory(string id) => Directory.GetDirectories(Exports, id, SearchOption.AllDirectories).Single();
        internal byte[][] History() => Files.Files.HistoryFiles.Order().Select(File.ReadAllBytes).ToArray();

        internal async Task PrepareAsync(bool withTransition = false, bool scenarioOnly = false, bool fullTransfer = false)
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
            if (fullTransfer)
            {
                config["comfy_root"] = ComfyRoot;
                foreach (var model in new[] { "diffusion_models/fasth3_omniref_qad_nvfp4_comfy.safetensors",
                    "text_encoders/qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors", "vae/minimax_h3_video_vae_fp16.safetensors",
                    "vae/minimax_h3_audio_vae_fp32.safetensors", "input/Keep.txt" })
                {
                    var path = model.StartsWith("input/", StringComparison.Ordinal) ? Path.Combine(ComfyRoot, model) : Path.Combine(ComfyRoot, "models", model);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "keep");
                }
            }
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

        internal async Task<string> PrepareTransferWorkerFixtureAsync()
        {
            var source = BackendWriteFixture.Executable();
            var directory = Path.GetDirectoryName(source)!;
            var isolated = Path.Combine(Root, "app");
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(isolated, Path.GetRelativePath(directory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var helpers = Path.Combine(isolated, "FullTransferWorker");
            File.WriteAllText(Path.Combine(ComfyRoot, "main.py"), "# Isolated Comfy environment fixture.\n");
            await BackendTrimFixture.RunAsync(Ffmpeg,
                ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=purple:s=1280x736:r=24",
                 "-frames:v", "56", "-threads", "1", "-n", Path.Combine(ComfyRoot, "Fixture.mp4")], 2048);
            await BackendTrimFixture.RunAsync(Ffmpeg,
                ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=purple:s=1280x736",
                 "-frames:v", "1", "-threads", "1", "-n", Path.Combine(ComfyRoot, "Fixture.png")], 2048);
            // Keep the packaged orchestration, ownership, preflight hashes and collector.
            // Only the native Comfy schema/HTTP/execution processes are simulated in this copy.
            File.WriteAllText(Path.Combine(helpers, "validate.py"), """
                import os
                from pathlib import Path
                import sys
                sys.path.insert(0, str(Path(__file__).resolve().parent))
                from common import verify_job
                assert os.environ["CUDA_VISIBLE_DEVICES"] == ""
                verify_job(Path(os.environ["TMT_OMNI2_JOB"]))
                """);
            File.WriteAllText(Path.Combine(helpers, "server.py"), """
                import os
                from http.server import BaseHTTPRequestHandler, HTTPServer

                class Handler(BaseHTTPRequestHandler):
                    def do_GET(self):
                        if self.headers.get("X-Studio-Owner") != os.environ["TMT_OMNI2_OWNER"]:
                            self.send_error(403)
                            return
                        self.send_response(200)
                        self.end_headers()
                        self.wfile.write(b"{}")

                HTTPServer(("127.0.0.1", int(os.environ["TMT_OMNI2_PORT"])), Handler).serve_forever()
                """);
            File.WriteAllText(Path.Combine(helpers, "driver.py"), """
                import os
                from pathlib import Path
                import shutil
                import sys
                import time
                sys.path.insert(0, str(Path(__file__).resolve().parent))
                from common import atomic_write, read, verify_job

                job = Path(os.environ["TMT_OMNI2_JOB"])
                verify_job(job)
                (job / "Submission.claim").open("x").close()
                atomic_write(job / "Submission.json", {"submissions": 1})
                (job / "FixtureDriver.pending").write_text(str(os.getpid()), encoding="ascii")
                (job / "FixtureDriver.pending").rename(job / "FixtureDriver.pid")
                if read(job / "Job.json")["seed"] == 1:
                    time.sleep(60)
                    raise RuntimeError("Ownership test did not stop the driver")
                output = job / "output"
                output.mkdir()
                shutil.copyfile("Fixture.mp4", output / "Final.mp4")
                shutil.copyfile("Fixture.png", output / "Last.png")
                atomic_write(job / "History.json", {
                    "status": {"status_str": "success", "messages": [
                        ["execution_start", {"timestamp": 0}], ["execution_success", {"timestamp": 1000}]]},
                    "outputs": {"92": {"images": [{"filename": "Final.mp4", "subfolder": ""}]},
                                "202": {"images": [{"filename": "Last.png", "subfolder": ""}]}}})
                atomic_write(job / "Runtime.json", {"h3_forwards": [{"output_finite": [True]}], "sage_success": 1, "sdpa_calls": 0})
                atomic_write(job / "Telemetry.summary.json", {"process_rss_peak_bytes": 1024, "device_gpu_peak_MiB": 0})
                """);
            return Path.Combine(isolated, Path.GetFileName(source));
        }

        internal async Task<ExportSession> OpenAsync(bool workspace = false, string? executable = null,
            Func<NAudio.Wave.IWaveProvider, TrueMoon.Tales.Studio.Media.IPreviewSound>? reviewSound = null)
        {
            var options = BackendOptions.Create(Files.Files.ProjectDirectory, Files.Base.ConfigPath) with
            { WriteEnabled = true, Action = "serve", ExecutablePath = executable ?? BackendWriteFixture.Executable() };
            var app = TrueMoon.App.Builder(_ => { }).Setup(context =>
            {
                context.UseProjectBackend(options, false);
                if (workspace) context.UseBackendScene();
                if (reviewSound is not null)
                    context.Services(services => services.Singleton<TrueMoon.Tales.Studio.Media.IPreviewMedia>(resolved =>
                        new BackendPreviewMedia(resolved.Resolve<BackendConnection>()!, reviewSoundFactory: reviewSound)));
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
