using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Presentation.Project;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class ProjectViewModelTests
{
    [Fact]
    public async Task Reload_OneReadAtATime_PreservesPreviousSnapshotOnFailureAndReadsFreshRevision()
    {
        using var fixture = new BackendFixture();
        var reply = await fixture.Service().OpenProjectAsync(fixture.Options.SessionKey);
        var first = BackendConnection.Decode(BackendConnectionTests.Identity(), reply);
        var reader = new ControlledReader();
        using var model = new ProjectViewModel(reader);

        model.Reload();
        model.Reload();
        Assert.Equal(1, reader.Calls);
        Assert.True(model.Loading);
        Assert.False(model.Poll());
        reader.Pending.SetResult(first);
        Assert.True(model.Poll());
        Assert.Same(first, model.Opened);
        Assert.False(model.Failed);

        reader.Reset();
        model.Reload();
        reader.Pending.SetException(new IOException("Disconnected"));
        Assert.True(model.Poll());
        Assert.True(model.Failed);
        Assert.Same(first, model.Opened);
        Assert.Contains("Disconnected", model.Status);
        Assert.Equal(2, reader.Calls); // Failure does not trigger an automatic retry.

        reader.Reset();
        model.Reload();
        var fresh = first with { Project = first.Project with { Revision = first.Project.Revision + 1 } };
        reader.Pending.SetResult(fresh);
        model.Poll();
        Assert.Same(fresh, model.Opened);
        Assert.False(model.Failed);
        Assert.Equal(3, reader.Calls);
    }

    [Fact]
    public void Dispose_CancelsOnlyPendingReadAndPreventsFurtherReads()
    {
        var reader = new ControlledReader();
        var model = new ProjectViewModel(reader);
        model.Reload();
        var token = reader.Token;
        model.Dispose();
        Assert.True(token.IsCancellationRequested);
        model.Reload();
        Assert.Equal(1, reader.Calls);
        reader.Pending.SetException(new IOException("late disconnect"));
    }

    private sealed class ControlledReader : IBackendProjectReader
    {
        internal int Calls;
        internal CancellationToken Token;
        internal TaskCompletionSource<OpenedBackendProject> Pending = new();
        internal void Reset() => Pending = new();
        public Task<OpenedBackendProject> OpenAsync(CancellationToken cancellationToken = default)
        { Calls++; Token = cancellationToken; return Pending.Task; }
    }
}
