using TrueMoon.Tales.Studio.Backend;
using TrueMoon;
using TrueMoon.Dependencies;
using TrueMoon.Diagnostics;
using TrueMoon.Mithril;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendConnectionTests
{
    internal static BackendIdentity Identity() => new()
    {
        Role = "backend", ProtocolVersion = StudioBackendService.ProtocolVersion, ProjectContractVersion = 1, ProductVersion = StudioBackendService.ProductVersion,
        ProcessId = 12345, ParentProcessId = Environment.ProcessId
    };

    [Theory]
    [InlineData("role")]
    [InlineData("protocol")]
    [InlineData("contract")]
    [InlineData("product")]
    [InlineData("parent")]
    [InlineData("unowned")]
    public void Handshake_RejectsWrongRoleVersionParentAndUnownedProcess(string field)
    {
        var identity = Identity();
        switch (field)
        {
            case "role": identity.Role = "ui"; break;
            case "protocol": identity.ProtocolVersion++; break;
            case "contract": identity.ProjectContractVersion++; break;
            case "product": identity.ProductVersion = "another-build"; break;
            case "parent": identity.ParentProcessId++; break;
        }
        Assert.Throws<InvalidDataException>(() => BackendConnection.ValidateIdentity(identity, field != "unowned"));
    }

    [Fact]
    public async Task Decode_ValidatesSnapshotInsteadOfTrustingStorageOrMalformedEnvelope()
    {
        using var fixture = new BackendFixture();
        var reply = await fixture.Service().OpenProjectAsync(fixture.Options.SessionKey);
        BackendConnection.ValidateIdentity(Identity(), true);
        var opened = BackendConnection.Decode(Identity(), reply);
        Assert.Equal("project-synthetic", opened.Project.Id);
        Assert.Equal(11, opened.Project.Revision);
        reply.SchemaVersion = 999;
        Assert.Throws<InvalidDataException>(() => BackendConnection.Decode(Identity(), reply));
        reply.SchemaVersion = 1;
        reply.Fingerprint = new('z', 64);
        Assert.Throws<InvalidDataException>(() => BackendConnection.Decode(Identity(), reply));
        reply.Fingerprint = new('a', 64);
        reply.ProjectJson = "{\"schema_version\":1}";
        Assert.Throws<System.Text.Json.JsonException>(() => BackendConnection.Decode(Identity(), reply));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_UnresponsiveBackendHonorsDeadlineAndCallerCancellation_WithoutOpeningProject(bool callerCancelled)
    {
        using var fixture = new BackendFixture();
        using var app = App.Build(context => context.UseDiagnostics());
        var service = new UnresponsiveService();
        var factory = new BackendProcessFactory(fixture.Options, app.Services.Resolve<IEventsSource<UnitsController>>()!);
        var connection = new BackendConnection(service, fixture.Options, factory);
        using var cancellation = new CancellationTokenSource();
        if (callerCancelled) cancellation.Cancel();
        var pending = connection.OpenAsync(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(pending.IsCanceled);
        Assert.Equal(callerCancelled ? 0 : 1, service.HelloCalls);
        Assert.Equal(0, service.OpenCalls);
        Assert.Equal(fixture.Files.InitialBytes, File.ReadAllBytes(fixture.Files.ProjectPath));
    }

    private sealed class UnresponsiveService : IStudioBackend
    {
        public Task<BackendExportReply> CheckDependenciesAsync(string key, long compared, long revision, string hash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendExportReply> CompareJobSnapshotsAsync(string key, string before, string after, long revision, string hash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendTextReply> ReadAdaptationTextAsync(string key, string reference, int first, int last, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendExportReply> CheckReadinessAsync(string key, long revision, string hash, bool episode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendExportReply> WorkingReviewAsync(string key, string operation, string id, long revision, string hash, int startFrame, int endFrame, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendProjectReply> EditScenarioAsync(string key, string command, long revision, string hash, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<BackendExportReply> CheckExportAsync(string key, string profile, long revision, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendExportReply> ExportInterchangeAsync(string key, long revision, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendExportReply> FindExportAsync(string key, string clientKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendExportReply> StartExportAsync(string key, string clientKey, string profile, long revision, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendExportReply> ExportStatusAsync(string key, string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendExportReply> CancelExportAsync(string key, string id, long revision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendExportReply> RecoverExportAsync(string key, string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendExportReply> ExportChunkAsync(string key, string id, string sha, long artifactOffset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendProjectReply> TrimClipAsync(string key, string id, double sourceInFrame, double sourceOutFrame, int start, long revision, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendPlaybackReply> OpenPlaybackAsync(string key, string clipId, string kind, int localFrame, long revision, string fingerprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendPlaybackReply> ReadPlaybackAsync(string key, string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendPlaybackReply> ClosePlaybackAsync(string key, string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendMediaReply> MediaInfoAsync(string key, string id, long revision, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendMediaReply> FrameAsync(string key, string id, int frame, long revision, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendMediaReply> PreviewFrameAsync(string key, string id, int frame, long revision, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendExportReply> TakeProxyAsync(string key, string operation, string source, string take, long revision, string hash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendMediaReply> TakePairFrameAsync(string key, string source, string final, bool draft, int frame, long revision, string hash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendPreparationReply> PrepareFullTransferAsync(string key, string json, long revision, string hash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendPreparationReply> FullTransferJobAsync(string key, string id, string? request, long revision, string hash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendPreparationReply> FullTransferRunAsync(string key, string id, string request, string operation,
            long revision, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendProjectReply> CollectFullTransferAsync(string key, string id, string request, long revision, string hash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendMediaReply> StoryboardStillAsync(string key, string segment, string action, long revision, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendProjectReply> AcquireWriterAsync(string key, long revision, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendProjectReply> MigrateProjectAsync(string key, long revision, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendProjectReply> MoveClipAsync(string key, string id, int start, long revision, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendProjectReply> RecoverProjectAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        internal int HelloCalls, OpenCalls;
        public async Task<BackendIdentity> HelloAsync(string sessionKey, CancellationToken cancellationToken = default)
        { HelloCalls++; await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return Identity(); }
        public Task<BackendProjectReply> OpenProjectAsync(string sessionKey, CancellationToken cancellationToken = default)
        { OpenCalls++; throw new InvalidOperationException("Opening is forbidden before readiness."); }
    }
}
