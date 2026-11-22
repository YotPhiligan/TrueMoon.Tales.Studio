using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using TrueMoon.Alloy.Rendering.Skia;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Tests.Persistence;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendNativeCliTests
{
    private static string Fingerprint => new('A', 64);

    [Fact]
    public async Task NativeMove_UsesExistingWriter_AndConflictsAndNoopDoNotWrite()
    {
        await using var fixture = new BackendHttpFixture();
        await fixture.StartAsync();
        var files = fixture.Files.Files;
        var before = File.ReadAllBytes(files.ProjectPath);
        var original = await fixture.Connection.OpenAsync();

        var initial = Snapshot(await RunAsync(fixture.Http.Address, fixture.Http.Token, "backend-project"));
        Assert.Equal(original.Project.Id, initial.Project.Id);
        Assert.Equal(original.Fingerprint, initial.Fingerprint);
        Assert.Equal(before, File.ReadAllBytes(files.ProjectPath));
        Assert.Single(files.HistoryFiles);
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Files.Options)));

        var moved = Snapshot(await MoveAsync(initial.Project.Revision, initial.Fingerprint));
        var clip = Assert.Single(moved.Project.Montage.Clips, row => row.Id == "clip-main");
        Assert.Equal(14, clip.StartFrame);
        Assert.Equal(initial.Project.Revision + 1, moved.Project.Revision);
        Assert.NotEqual(initial.Fingerprint, moved.Fingerprint);
        Assert.Equal(original.Identity.ProcessId, (await fixture.Connection.OpenAsync()).Identity.ProcessId);
        Assert.Equal(2, files.HistoryFiles.Length);
        Assert.Contains(files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(before));
        var committed = File.ReadAllBytes(files.ProjectPath);

        AssertError(await MoveAsync(initial.Project.Revision, moved.Fingerprint), 3, "revision_conflict", reconcile: true);
        AssertError(await MoveAsync(moved.Project.Revision, initial.Fingerprint), 3, "revision_conflict", reconcile: true);
        Assert.Equal(moved.Fingerprint, Snapshot(await MoveAsync(moved.Project.Revision, moved.Fingerprint)).Fingerprint);
        Assert.Equal(committed, File.ReadAllBytes(files.ProjectPath));
        Assert.Equal(2, files.HistoryFiles.Length);

        Task<CliResult> MoveAsync(long revision, string sha) => RunAsync(fixture.Http.Address, fixture.Http.Token,
            "backend-move", "--clip", "clip-main", "--start-frame", "14",
            "--revision", revision.ToString(CultureInfo.InvariantCulture), "--sha256", sha);
    }

    [BackendMediaFact]
    public async Task NativeTrim_ReturnsCommittedExtent_AndRejectsAStaleRepeat()
    {
        await using var fixture = new BackendMediaFixture();
        await fixture.StartAsync();
        var before = File.ReadAllBytes(fixture.Files.ProjectPath);
        string[] trim = ["backend-trim", "--clip", "clip-main", "--source-in", "8", "--source-out", "48", "--start", "14",
            "--revision", fixture.Baseline.Project.Revision.ToString(CultureInfo.InvariantCulture), "--sha256", fixture.Baseline.Fingerprint];
        var result = Snapshot(await RunAsync(fixture.Backend.Http.Address, fixture.Backend.Http.Token, trim));
        var clip = Assert.Single(result.Project.Montage.Clips, row => row.Id == "clip-main");
        Assert.Equal((14, 20, 8d, 48d), (clip.StartFrame, clip.DurationFrames, clip.SourceInFrame, clip.SourceOutFrame!.Value));
        var committed = File.ReadAllBytes(fixture.Files.ProjectPath);
        Assert.Equal(result.Fingerprint, ProjectFileCodec.Read(committed).Fingerprint);
        AssertError(await RunAsync(fixture.Backend.Http.Address, fixture.Backend.Http.Token, trim), 3, "revision_conflict", reconcile: true);
        Assert.Equal(committed, File.ReadAllBytes(fixture.Files.ProjectPath));
        Assert.Equal(2, fixture.Files.HistoryFiles.Length);
        Assert.Contains(fixture.Files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(before));
    }

    [Theory]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://127.1:8080/")]
    [InlineData("http://127.0.0.1:8080/api/")]
    [InlineData("http://127.0.0.1:8080/?scope=project")]
    public async Task NativeProject_RefusesNoncanonicalServerAddresses(string url)
    {
        AssertError(await RunAsync(new Uri(url), Fingerprint, "backend-project"), 2, "invalid_arguments", reconcile: false);
    }

    [Fact]
    public async Task NativeMove_RequiresBaselineBeforeContactingServer()
    {
        AssertError(await RunAsync(new Uri("http://127.0.0.1:1/"), Fingerprint, "backend-move",
            "--clip", "clip-main", "--start-frame", "14", "--revision", "12"), 2, "invalid_arguments", reconcile: false);
    }

    [BackendMediaFact]
    public async Task NativeMediaAndFrame_RejectStaleBaselineAndUnavailableFrameWithoutWriting()
    {
        await using var fixture = new BackendMediaFixture();
        await fixture.StartAsync();
        var (address, token) = (fixture.Backend.Http.Address, fixture.Backend.Http.Token);
        var revision = fixture.Baseline.Project.Revision.ToString(CultureInfo.InvariantCulture);
        AssertError(await RunAsync(address, token, "backend-media", "--asset", "video-a", "--revision", revision,
            "--sha256", Fingerprint), 3, "revision_conflict", reconcile: true);
        var result = await RunAsync(address, token, "backend-frame", "--asset", "video-a", "--revision", revision,
            "--sha256", fixture.Baseline.Fingerprint, "--frame", "90", "--output", fixture.PngPath);
        AssertError(result, 1, "invalid_frame", reconcile: true);
        Assert.False(File.Exists(fixture.PngPath));

        byte[] existing = [11, 22, 33];
        File.WriteAllBytes(fixture.PngPath, existing);
        AssertError(await RunAsync(address, token, "backend-frame", "--asset", "video-a", "--revision", revision,
            "--sha256", fixture.Baseline.Fingerprint, "--frame", "20", "--output", fixture.PngPath),
            2, "invalid_arguments", reconcile: false);
        Assert.Equal(existing, File.ReadAllBytes(fixture.PngPath));
        fixture.AssertUnchanged();
    }

    [Theory]
    [InlineData("bad-pixels", false, "invalid_response")]
    [InlineData("wrong-asset", false, "invalid_response")]
    [InlineData("wrong-index", false, "invalid_response")]
    [InlineData("pixels-in-metadata", true, "invalid_response")]
    [InlineData("output-race", false, "output_exists")]
    public async Task NativeMedia_ValidatesIdentityAndPixelsBeforePublishing(string mode, bool metadata, string error)
    {
        using var files = new PersistenceFixture();
        var output = Path.Combine(files.Root, "кадр с пробелом.png");
        byte[] competing = [3, 7, 11];
        var pixels = new byte[BackendMediaDecoder.FrameBytes];
        var media = new BackendMediaReply
        {
            AssetId = mode == "wrong-asset" ? "video-b" : "video-a",
            Revision = 12,
            Fingerprint = Fingerprint,
            Fps = 24,
            FrameCount = 90,
            SourceWidth = 320,
            SourceHeight = 240,
            FrameIndex = metadata ? -1 : mode == "wrong-index" ? 21 : 20,
            Width = 480,
            Height = 270,
            Pixels = pixels,
            PixelSha256 = Convert.ToHexString(SHA256.HashData(pixels))
        };
        if (mode == "bad-pixels") pixels[0] ^= 1;
        var requests = 0;
        await WithStubAsync(async context =>
        {
            Interlocked.Increment(ref requests);
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            if (mode == "output-race") File.WriteAllBytes(output, competing);
            await context.Response.WriteAsJsonAsync(new BackendHttpMediaSnapshot(1, 2, media), BackendHttpServer.Json);
        }, async address =>
        {
            string[] args = metadata
                ? ["backend-media", "--asset", "video-a", "--revision", "12", "--sha256", Fingerprint]
                : ["backend-frame", "--asset", "video-a", "--revision", "12", "--sha256", Fingerprint,
                    "--frame", "20", "--output", output];
            AssertError(await RunAsync(address, Fingerprint, args), 1, error, reconcile: false);
            Assert.Equal(1, Volatile.Read(ref requests));
        });
        if (mode == "output-race") Assert.Equal(competing, File.ReadAllBytes(output));
        else Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(files.Root, ".studio-frame-*.tmp"));
    }

    [Fact]
    public async Task NativeFrame_PngPreservesBgraIncludingNonopaquePixels()
    {
        using var files = new PersistenceFixture();
        var output = Path.Combine(files.Root, "кадр с прозрачностью.png");
        var pixels = new byte[BackendMediaDecoder.FrameBytes];
        new byte[] { 20, 91, 231, 0, 18, 42, 197, 128, 19, 71, 186, 255 }.CopyTo(pixels, 0);
        var media = new BackendMediaReply
        {
            AssetId = "video-a", Revision = 12, Fingerprint = Fingerprint, Fps = 24, FrameCount = 1,
            SourceWidth = 480, SourceHeight = 270, FrameIndex = 0, Width = 480, Height = 270,
            Pixels = pixels, PixelSha256 = Convert.ToHexString(SHA256.HashData(pixels))
        };
        await WithStubAsync(async context =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            await context.Response.WriteAsJsonAsync(new BackendHttpMediaSnapshot(1, 2, media), BackendHttpServer.Json);
        }, async address =>
        {
            var result = await RunAsync(address, Fingerprint, "backend-frame", "--asset", "video-a", "--revision", "12",
                "--sha256", Fingerprint, "--frame", "0", "--output", output);
            Assert.True(result.Code == 0, result.Error);
            using var receipt = JsonDocument.Parse(result.Output);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(output))), receipt.RootElement.GetProperty("png_sha256").GetString());
            SkiaNativeLibrary.Initialize();
            using var decoded = SKBitmap.Decode(output, new SKImageInfo(480, 270, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            Assert.NotNull(decoded);
            Assert.Equal(pixels, decoded.GetPixelSpan().ToArray());
        });
    }

    [Theory]
    [InlineData("redirect", "backend_http_error")]
    [InlineData("lost", "backend_unavailable")]
    [InlineData("duplicate-json", "invalid_response")]
    public async Task NativeWrite_SendsOnePostAndRequiresReconciliationWhenTheReplyIsUnknown(string mode, string error)
    {
        var count = 0;
        string? method = null, route = null;
        await WithStubAsync(async context =>
        {
            Interlocked.Increment(ref count);
            method = context.Request.Method;
            route = context.Request.Path;
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            if (mode == "lost") { context.Abort(); return; }
            if (mode == "redirect")
            {
                context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location = "/redirected";
                return;
            }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"api_version":1,"api_version":1}""");
        }, async address =>
        {
            var result = await RunAsync(address, Fingerprint,
                "backend-move", "--clip", "clip-main", "--start-frame", "14", "--revision", "12", "--sha256", Fingerprint);
            AssertError(result, 1, error, reconcile: true);
            Assert.Equal(1, Volatile.Read(ref count));
            Assert.Equal(HttpMethods.Post, method);
            Assert.Equal(BackendHttpServer.MoveRoute, route);
        });
    }

    [Theory]
    [InlineData("ok", 0, null)]
    [InlineData("running", 3, "export_not_ready")]
    [InlineData("foreign-sha", 3, "export_not_ready")]
    [InlineData("wrong-id", 1, "invalid_response")]
    [InlineData("wrong-type", 1, "invalid_response")]
    [InlineData("short", 1, "invalid_response")]
    [InlineData("corrupt", 1, "artifact_sha_mismatch")]
    [InlineData("collision", 1, "output_exists")]
    public async Task NativeExportDownload_StreamsExactPartsAndPublishesOnlyVerifiedBytes(string mode, int code, string? error)
    {
        using var files = new PersistenceFixture();
        var output = Path.Combine(files.Root, "Экспорт с пробелом.mp4");
        var id = BackendExportJobs.JobId("studio-export-test");
        var bytes = new byte[2 * BackendExportJobs.ChunkSize + 8];
        new Random(118).NextBytes(bytes);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var existing = new byte[] { 7, 18, 34 };
        List<long> offsets = [];
        var statusCount = 0;
        await WithStubAsync(async context =>
        {
            Assert.Equal(HttpMethods.Post, context.Request.Method);
            Assert.Equal(Fingerprint, context.Request.Headers["X-Studio-Token"].ToString());
            using var request = await JsonDocument.ParseAsync(context.Request.Body);
            Assert.Equal(id, request.RootElement.GetProperty("id").GetString());
            if (context.Request.Path == BackendHttpServer.ExportStatusRoute)
            {
                Interlocked.Increment(ref statusCount);
                var job = mode == "running"
                    ? new BackendExportStatus(1, id, "project", 12, Fingerprint, "draft", 1, "running", Fingerprint, 72, 24)
                    : new BackendExportStatus(1, mode == "wrong-id" ? BackendExportJobs.JobId("other-project-test") : id,
                        "project", 12, Fingerprint, "draft", 2, "completed", Fingerprint, 72, 24,
                        bytes.Length, mode == "foreign-sha" ? Fingerprint : sha, ProgressPercent: 100);
                await context.Response.WriteAsJsonAsync(job, BackendHttpServer.Json);
                return;
            }
            Assert.Equal(BackendHttpServer.ExportChunkRoute, context.Request.Path.Value);
            Assert.Equal(sha, request.RootElement.GetProperty("artifact_sha256").GetString());
            var offset = request.RootElement.GetProperty("offset").GetInt32();
            lock (offsets) offsets.Add(offset);
            Assert.False(File.Exists(output));
            var chunk = bytes.AsMemory(offset, Math.Min(BackendExportJobs.ChunkSize, bytes.Length - offset));
            var last = offset + chunk.Length == bytes.Length;
            context.Response.ContentType = last && mode == "wrong-type" ? "text/plain" : "application/octet-stream";
            context.Response.ContentLength = chunk.Length;
            if (last && mode == "collision")
                File.WriteAllBytes(output, existing);
            if (last && mode == "short")
            {
                await context.Response.Body.WriteAsync(chunk[..^1]);
                await context.Response.Body.FlushAsync();
                context.Abort();
                return;
            }
            if (last && mode == "corrupt") chunk = new byte[chunk.Length];
            await context.Response.Body.WriteAsync(chunk);
        }, async address =>
        {
            var result = await RunAsync(address, Fingerprint, "backend-export-download", "--id", id, "--sha256", sha, "--output", output);
            Assert.Equal(1, Volatile.Read(ref statusCount));
            long[] seen;
            lock (offsets) seen = offsets.ToArray();
            long[] expected = mode is "running" or "foreign-sha" or "wrong-id" ? [] : [0, BackendExportJobs.ChunkSize, 2 * BackendExportJobs.ChunkSize];
            Assert.Equal(expected, seen);
            Assert.Empty(Directory.GetFiles(files.Root, ".studio-export-*.tmp"));
            if (error is not null)
            {
                AssertError(result, code, error, reconcile: false);
                if (mode == "collision") Assert.Equal(existing, File.ReadAllBytes(output));
                else Assert.False(File.Exists(output));
                return;
            }
            Assert.True(result.Code == 0, result.Error);
            using var receipt = JsonDocument.Parse(result.Output);
            Assert.Equal(sha, receipt.RootElement.GetProperty("artifact_sha256").GetString());
            Assert.Equal(bytes.LongLength, receipt.RootElement.GetProperty("artifact_size_bytes").GetInt64());
            Assert.Equal(bytes, File.ReadAllBytes(output));
        });
    }

    [Fact]
    public async Task NativeExportDownload_RejectsExistingDestinationBeforeSendingARequest()
    {
        using var files = new PersistenceFixture();
        var output = Path.Combine(files.Root, "Existing.mp4");
        File.WriteAllBytes(output, [42]);
        var result = await RunAsync(new Uri("http://127.0.0.1:1/"), Fingerprint, "backend-export-download",
            "--id", BackendExportJobs.JobId("studio-export-test"), "--sha256", Fingerprint, "--output", output);
        AssertError(result, 2, "invalid_arguments", reconcile: false);
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData("start", BackendHttpServer.ExportStartRoute)]
    [InlineData("cancel", BackendHttpServer.ExportCancelRoute)]
    [InlineData("recover", BackendHttpServer.ExportRecoverRoute)]
    public async Task NativeExportMutation_LostReplyIdentifiesJobWithoutReplay(string operation, string route)
    {
        const string key = "lost-reply-export";
        var id = BackendExportJobs.JobId(key);
        var requests = 0;
        await WithStubAsync(async context =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal(route, context.Request.Path.Value);
            Assert.Equal(HttpMethods.Post, context.Request.Method);
            Assert.Equal(Fingerprint, context.Request.Headers["X-Studio-Token"].ToString());
            using var request = await JsonDocument.ParseAsync(context.Request.Body);
            if (operation == "cancel")
            {
                Assert.Equal(id, request.RootElement.GetProperty("id").GetString());
                Assert.Equal(2, request.RootElement.GetProperty("expected_job_revision").GetInt64());
            }
            else if (operation == "start")
            {
                Assert.Equal(key, request.RootElement.GetProperty("client_key").GetString());
                Assert.Equal(12, request.RootElement.GetProperty("expected_revision").GetInt64());
                Assert.Equal(Fingerprint, request.RootElement.GetProperty("fingerprint").GetString());
                Assert.Equal("final", request.RootElement.GetProperty("profile").GetString());
            }
            else Assert.Equal(id, Assert.Single(request.RootElement.EnumerateObject()).Value.GetString());
            context.Abort();
        }, async address =>
        {
            string[] args = operation switch
            {
                "cancel" => ["backend-export-cancel", "--id", id, "--job-revision", "2"],
                "start" => ["backend-export-start", "--client-key", key, "--revision", "12", "--sha256", Fingerprint, "--profile", "final"],
                _ => ["backend-export-recover", "--id", id]
            };
            var result = await RunAsync(address, Fingerprint, args);
            AssertError(result, 1, "backend_unavailable", reconcile: true);
            using var failure = JsonDocument.Parse(result.Error);
            Assert.Contains($"backend-export-status --id {id}", failure.RootElement.GetProperty("message").GetString());
            Assert.Equal(1, Volatile.Read(ref requests));
        });
    }

    [Theory]
    [InlineData("their-job")]
    [InlineData("running")]
    public async Task NativeExportRecover_RejectsUnrelatedOrNonterminalStatus(string mode)
    {
        var id = BackendExportJobs.JobId("studio-export-recover");
        var running = mode == "running";
        var job = new BackendExportStatus(1, running ? id : BackendExportJobs.JobId("other-export-recover"),
            "project", 12, Fingerprint, "draft", running ? 1 : 2, running ? "running" : "interrupted", Fingerprint, 72, 24,
            ErrorCode: running ? null : "export_interrupted");
        await WithStubAsync(context => context.Response.WriteAsJsonAsync(job, BackendHttpServer.Json), async address =>
        {
            var result = await RunAsync(address, Fingerprint, "backend-export-recover", "--id", id);
            AssertError(result, 1, "invalid_response", reconcile: true);
        });
    }

    [Theory]
    [InlineData(true, "backend_unavailable")]
    [InlineData(false, "invalid_response")]
    public async Task NativeTransferStage_SendsOnePostAndRequiresReconciliation(bool loseReply, string error)
    {
        var requests = 0;
        var id = BackendFullTransfer.JobId(new string('a', 32));
        var sha = new string('B', 64);
        await WithStubAsync(async context =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal(BackendHttpServer.TransferStageRoute, context.Request.Path);
            Assert.Equal(HttpMethods.Post, context.Request.Method);
            var request = await JsonSerializer.DeserializeAsync<BackendHttpServer.TransferJobBody>(context.Request.Body, BackendHttpServer.Json);
            Assert.Equal((id, 12L, Fingerprint, sha), (request!.Id, request.ExpectedRevision, request.Fingerprint, request.RequestSha256));
            if (loseReply) context.Abort();
            else
            {
                var wrong = new FullTransferReceipt(1, id, "staged", 12, Fingerprint, "draft", Fingerprint,
                    Fingerprint, Fingerprint, Fingerprint, 56, 24, 1280, 736, true, false);
                await context.Response.WriteAsJsonAsync(wrong, BackendHttpServer.Json);
            }
        }, async address =>
        {
            var result = await RunAsync(address, Fingerprint, "backend-transfer-stage", "--id", id,
                "--revision", "12", "--sha256", Fingerprint, "--request-sha256", sha);
            AssertError(result, 1, error, reconcile: true);
            Assert.Contains($"backend-transfer-job --id {id}", result.Error);
            Assert.Equal(1, Volatile.Read(ref requests));
        });
    }

    private static async Task WithStubAsync(RequestDelegate handler, Func<Uri, Task> test)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.Run(handler);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await app.StartAsync(deadline.Token);
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            await test(new Uri(addresses.Addresses.Single()));
        }
        finally { await app.StopAsync(deadline.Token); }
    }

    private static BackendHttpSnapshot Snapshot(CliResult result)
    {
        Assert.True(result.Code == 0, result.Error);
        Assert.Empty(result.Error);
        return JsonSerializer.Deserialize<BackendHttpSnapshot>(result.Output, BackendHttpServer.Json)!;
    }

    private static void AssertError(CliResult result, int code, string expected, bool reconcile)
    {
        Assert.Equal(code, result.Code);
        Assert.Empty(result.Output);
        using var payload = JsonDocument.Parse(result.Error);
        Assert.Equal(expected, payload.RootElement.GetProperty("error").GetString());
        Assert.Equal(reconcile, payload.RootElement.GetProperty("reconciliation_required").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("automatic_retry").GetBoolean());
    }

    private static async Task<CliResult> RunAsync(Uri address, string token, params string[] args)
    {
        using var cli = BackendCliProcess.Native(address, token, args);
        var result = await cli.WaitAsync();
        return new(result.Code, result.Output, result.Error);
    }

    private sealed record CliResult(int Code, string Output, string Error);
}
