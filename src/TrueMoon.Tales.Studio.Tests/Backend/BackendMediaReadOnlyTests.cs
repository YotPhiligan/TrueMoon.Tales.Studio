using System.Security.Cryptography;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendMediaReadOnlyTests
{
    [BackendMediaFact]
    public async Task ReadOnlyConnection_ReadsRealFrameWhileAnotherSessionOwnsWriter_WithoutClaimingOrMutating()
    {
        await using var fixture = new BackendMediaFixture();
        await fixture.StartAsync();
        var options = BackendOptions.Create(fixture.Files.ProjectDirectory, fixture.Backend.Files.Base.ConfigPath)
            with { ExecutablePath = BackendWriteFixture.Executable() };
        await using var readerApp = TrueMoon.App.Builder(_ => { }).Setup(c => c.UseProjectBackend(options, false)).Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await readerApp.StartAsync(deadline.Token);
            var reader = readerApp.Services.Resolve<BackendConnection>()!;
            var opened = await reader.OpenAsync(deadline.Token);
            Assert.NotEqual(fixture.Baseline.Identity.ProcessId, opened.Identity.ProcessId);
            Assert.Equal(fixture.Baseline.Fingerprint, opened.Fingerprint);
            var frame = await reader.FrameAsync("video-a", 20, opened.Project.Revision, opened.Fingerprint, deadline.Token);
            Assert.Equal(await fixture.ReferenceFrameAsync(20), frame.Pixels);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(frame.Pixels)), frame.PixelSha256);
            Assert.Equal((30d, 90, 20), (frame.Fps, frame.FrameCount, frame.FrameIndex));
            Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Backend.Files.Options)));
            fixture.AssertUnchanged();
        }
        finally { await readerApp.StopAsync(); }
    }
}
