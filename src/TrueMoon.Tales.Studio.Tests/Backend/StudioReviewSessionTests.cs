using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Tests.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class StudioReviewSessionTests
{
    [Fact]
    public async Task Cleanup_RemovesAbandonedSessionsAndPreservesAnActiveOwnerAndUnrelatedFiles()
    {
        using var files = new PersistenceFixture();
        var root = Path.Combine(files.Root, "review-temp");
        using var active = new StudioReviewSession(root);
        await StudioReviewSession.CleanAbandonedAsync(root);
        Assert.False(Directory.Exists(root));

        active.Open();
        var current = Path.Combine(active.DirectoryPath, "Draft.mp4");
        File.WriteAllText(current, "active");
        using var stale = new StudioReviewSession(root);
        // A crashed owner has left its directory and lease file on disk with no open handle.
        var job = Path.Combine(stale.DirectoryPath, "Jobs", "working-old");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "Draft.mp4"), "abandoned");
        File.WriteAllText(stale.DirectoryPath + ".lock", "");
        var unrelated = Path.Combine(root, "review-other.lock");
        File.WriteAllText(unrelated, "keep");

        await StudioReviewSession.CleanAbandonedAsync(root);

        Assert.False(Directory.Exists(stale.DirectoryPath));
        Assert.False(File.Exists(stale.DirectoryPath + ".lock"));
        Assert.Equal("active", File.ReadAllText(current));
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }
}
