using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SkiaSharp;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed partial class BackendNativeCliTests
{
    [BackendMediaFact(requirePython: false)]
    public async Task NativeVideoRegistration_RegistersNewSourceAndAppendsTakeWithReadableFrames()
    {
        await using var fixture = new BackendMediaFixture();
        await fixture.StartAsync(distinctSecond: true);
        var backend = fixture.Backend;
        var files = backend.Files;
        var head = fixture.Baseline;
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(files.Options)));
        var firstBytes = File.ReadAllBytes(fixture.VideoPath);
        var secondBytes = File.ReadAllBytes(fixture.SecondVideoPath);
        var projectBytes = File.ReadAllBytes(fixture.Files.ProjectPath);
        const string alias = "Old/Media/Кадр с пробелом.mp4";
        const string named = "@videos/new/Другой материал.mp4";
        var form = ScenarioInput(files, "video.json", JsonSerializer.Serialize(new { title = "Новый план", take_name = "Основной дубль", reference = alias }));
        var args = VideoRegistrationArgs(form, "video-register-00001", head);
        var replies = await Task.WhenAll(RunScenarioAsync(backend, args), RunScenarioAsync(backend, args));
        var registration = ScenarioSuccess(replies[0]);
        Assert.True(JsonElement.DeepEquals(registration, ScenarioSuccess(replies[1])));
        Assert.True(registration.GetProperty("changed").GetBoolean());
        Assert.Equal(head.Project.Revision + 1, registration.GetProperty("project_revision").GetInt64());
        var sourceId = registration.GetProperty("source_id").GetString()!;
        var takeId = registration.GetProperty("take_id").GetString();
        var assetId = registration.GetProperty("asset_id").GetString()!;
        var created = await backend.Connection.OpenAsync();
        var source = Assert.Single(created.Project.Sources, row => row.Id == sourceId);
        Assert.Equal(head.Project.Sources.Length + 1, created.Project.Sources.Length);
        Assert.Equal("Новый план", source.Title);
        var take = Assert.Single(source.Takes);
        Assert.Equal(takeId, take.Id);
        Assert.Equal(assetId, take.VideoAssetId);
        Assert.Equal("Основной дубль", BackendScenarioEdit.TakeDisplayName(take));
        Assert.Equal(alias, BackendScenarioEdit.TryReference(created.Project, assetId));
        Assert.Equal(head.Project.Montage.Clips, created.Project.Montage.Clips);
        Assert.Equal(2, fixture.Files.HistoryFiles.Length);
        Assert.Contains(fixture.Files.HistoryFiles, history => File.ReadAllBytes(history).SequenceEqual(projectBytes));
        Assert.False(Directory.Exists(fixture.LegacyRoot));

        File.WriteAllText(form, JsonSerializer.Serialize(new { source_id = sourceId, take_name = "Ещё один дубль", reference = named }));
        var appended = ScenarioSuccess(await RunScenarioAsync(backend, VideoRegistrationArgs(form, "video-append-000001", created)));
        var secondTake = appended.GetProperty("take_id").GetString();
        var secondAsset = appended.GetProperty("asset_id").GetString()!;
        var current = await backend.Connection.OpenAsync();
        Assert.Equal(sourceId, appended.GetProperty("source_id").GetString());
        Assert.Equal(created.Project.Revision + 1, current.Project.Revision);
        Assert.NotEqual(takeId, secondTake);
        Assert.NotEqual(assetId, secondAsset);
        Assert.Equal(created.Project.Sources.Length, current.Project.Sources.Length);
        var library = Assert.Single(current.Project.Sources, row => row.Id == sourceId);
        Assert.Equal(new[] { takeId, secondTake }, library.Takes.Select(row => row.Id));
        Assert.Equal(secondAsset, library.Takes[1].VideoAssetId);
        Assert.Equal("Ещё один дубль", BackendScenarioEdit.TakeDisplayName(library.Takes[1]));
        Assert.Equal(named, BackendScenarioEdit.TryReference(current.Project, secondAsset));

        var revision = current.Project.Revision.ToString(CultureInfo.InvariantCulture);
        var metadata = ScenarioSuccess(await RunScenarioAsync(backend, ["backend-media", "--asset", assetId,
            "--revision", revision, "--sha256", current.Fingerprint])).GetProperty("media");
        Assert.Equal(assetId, metadata.GetProperty("asset_id").GetString());
        Assert.Equal(90, metadata.GetProperty("frame_count").GetInt32());
        const int frameIndex = 20;
        var frame = ScenarioSuccess(await RunScenarioAsync(backend, ["backend-frame", "--asset", secondAsset,
            "--revision", revision, "--sha256", current.Fingerprint, "--frame", "20", "--output", fixture.PngPath]));
        Assert.Equal(secondAsset, frame.GetProperty("media").GetProperty("asset_id").GetString());
        var pngBytes = File.ReadAllBytes(fixture.PngPath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(pngBytes)), frame.GetProperty("png_sha256").GetString());
        var referenceFrame = await fixture.ReferenceFrameAsync(fixture.SecondVideoPath, frameIndex);
        using var encoded = new MemoryStream(pngBytes, writable: false);
        using var png = SKBitmap.Decode(encoded, new SKImageInfo(480, 270, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Assert.NotNull(png);
        Assert.Equal(480, png.Width);
        Assert.Equal(270, png.Height);
        Assert.Equal(referenceFrame, png.GetPixelSpan().ToArray());
        Assert.Equal(firstBytes, File.ReadAllBytes(fixture.VideoPath));
        Assert.Equal(secondBytes, File.ReadAllBytes(fixture.SecondVideoPath));
        Assert.Equal(head.Project.Montage.Clips, current.Project.Montage.Clips);
        Assert.Equal(3, fixture.Files.HistoryFiles.Length);
        Assert.Equal(current.Fingerprint, (await backend.Connection.OpenAsync()).Fingerprint);
    }

    [BackendMediaFact(requirePython: false)]
    public async Task NativeVideoRegistration_RejectsStaleProjectAndRefusedMediaWithoutWrites()
    {
        await using var fixture = new BackendMediaFixture();
        await fixture.StartAsync();
        var backend = fixture.Backend;
        var head = fixture.Baseline;
        const string key = "video-refusal-00001";
        const string good = "@videos/new/Кадр с пробелом.mp4";
        var form = ScenarioInput(backend.Files, "video.json", Form(good));
        var stale = head with { Project = head.Project with { Revision = head.Project.Revision - 1 } };
        AssertError(await RunScenarioAsync(backend, VideoRegistrationArgs(form, key, stale)), 3, "revision_conflict", reconcile: true);
        AssertError(await RunScenarioAsync(backend, VideoRegistrationArgs(form, key, head with { Fingerprint = new string('0', 64) })),
            3, "revision_conflict", reconcile: true);
        var corrupt = Path.Combine(Path.GetDirectoryName(fixture.VideoPath)!, "corrupt.mp4");
        File.WriteAllText(corrupt, "Синтетический файл без видеопотока.");
        foreach (var (reference, error) in new[]
        {
            ("@videos/new/corrupt.mp4", "invalid_media"),
            ("@videos/new/missing.mp4", "media_not_found"),
            (Path.Combine(fixture.LegacyRoot, "unmapped.mp4"), "access_denied")
        })
        {
            File.WriteAllText(form, Form(reference));
            AssertError(await RunScenarioAsync(backend, VideoRegistrationArgs(form, key, head)), 1, error, reconcile: true);
        }
        File.WriteAllText(form, Form(good, "missing-source"));
        AssertError(await RunScenarioAsync(backend, VideoRegistrationArgs(form, key, head)), 1, "invalid_scenario_edit", reconcile: true);
        fixture.AssertUnchanged();
        File.WriteAllText(form, Form(good));
        Assert.True(ScenarioSuccess(await RunScenarioAsync(backend, VideoRegistrationArgs(form, key, head))).GetProperty("changed").GetBoolean());
        Assert.Equal(2, fixture.Files.HistoryFiles.Length);

        static string Form(string reference, string? sourceId = null) => JsonSerializer.Serialize(new
        { source_id = sourceId, title = "Новый источник", take_name = "Проверочный дубль", reference });
    }

    [BackendMediaFact(requirePython: false)]
    public async Task NativeVideoRegistration_ReplaysLostReceiptAfterRestartWithoutProbingAgain()
    {
        const string key = "video-lost-reply-001";
        const string reference = "Old/Media/Кадр с пробелом.mp4";
        using var files = new BackendWriteFixture();
        var form = ScenarioInput(files, "video.json", JsonSerializer.Serialize(new { title = "Утраченный ответ", take_name = "Дубль", reference }));
        string[] args;
        JsonElement receipt;
        string fingerprint;
        string videoPath;
        await using (var fixture = new BackendMediaFixture(files))
        {
            await fixture.StartAsync();
            videoPath = fixture.VideoPath;
            var head = fixture.Baseline;
            args = VideoRegistrationArgs(form, key, head);
            var requests = 0;
            using var transport = fixture.Backend.Client();
            await WithStubAsync(async context =>
            {
                Assert.Equal(BackendHttpServer.ScenarioVideoRoute, context.Request.Path.Value);
                Assert.Equal(HttpMethods.Post, context.Request.Method);
                Interlocked.Increment(ref requests);
                using var request = await JsonDocument.ParseAsync(context.Request.Body);
                using var body = new StringContent(request.RootElement.GetRawText(), Encoding.UTF8, "application/json");
                using var saved = await transport.PostAsync(BackendHttpServer.ScenarioVideoRoute, body, context.RequestAborted);
                saved.EnsureSuccessStatusCode();
                context.Abort();
            }, async address =>
            {
                AssertError(await RunAsync(address, Fingerprint, args), 1, "backend_unavailable", reconcile: true);
                Assert.Equal(1, Volatile.Read(ref requests));
            });
            var saved = await fixture.Backend.Connection.OpenAsync();
            Assert.Equal(head.Project.Revision + 1, saved.Project.Revision);
            fingerprint = saved.Fingerprint;
            var source = Assert.Single(saved.Project.Sources, row => row.Title == "Утраченный ответ");
            var take = Assert.Single(source.Takes);
            receipt = ScenarioSuccess(await RunScenarioAsync(fixture.Backend, args));
            Assert.Equal(source.Id, receipt.GetProperty("source_id").GetString());
            Assert.Equal(take.Id, receipt.GetProperty("take_id").GetString());
            Assert.Equal(take.VideoAssetId, receipt.GetProperty("asset_id").GetString());
            Assert.Equal(2, files.Files.HistoryFiles.Length);
        }

        File.Move(videoPath, Path.Combine(files.Files.Root, "fixture-offline.mp4"));
        await using var restarted = new BackendHttpFixture(files);
        await restarted.StartAsync(migrate: false);
        Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(await RunScenarioAsync(restarted, args))));
        File.WriteAllText(form, JsonSerializer.Serialize(new { title = "Другой запрос", take_name = "Дубль", reference }));
        AssertError(await RunScenarioAsync(restarted, args), 3, "scenario_action_key_conflict", reconcile: true);
        var current = await restarted.Connection.OpenAsync();
        Assert.Equal(fingerprint, current.Fingerprint);
        Assert.Single(current.Project.Sources, row => row.Title == "Утраченный ответ");
        Assert.Equal(2, files.Files.HistoryFiles.Length);
    }

    private static string[] VideoRegistrationArgs(string form, string key, OpenedBackendProject baseline) =>
        ["backend-scenario-video-register", "--file", form, "--client-key", key, "--sha256", baseline.Fingerprint,
            "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture)];
}
