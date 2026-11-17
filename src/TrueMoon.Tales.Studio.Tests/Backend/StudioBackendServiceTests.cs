using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class StudioBackendServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_Schema1And2_PreservesBytesRevisionHistoryAndLinks(bool migrate)
    {
        using var fixture = new BackendFixture();
        if (migrate)
        {
            var store = fixture.Files.Store();
            var baseline = await store.OpenAsync();
            await store.MigrateAsync(baseline, baseline.Project.Revision, "fixture");
        }
        var before = await File.ReadAllBytesAsync(fixture.Files.ProjectPath);
        var files = Directory.GetFiles(fixture.Files.ProjectDirectory, "*", SearchOption.AllDirectories).Order().ToArray();
        var reply = await fixture.Service().OpenProjectAsync(fixture.Options.SessionKey);
        var project = ProjectContractJson.Deserialize(reply.ProjectJson);

        Assert.Empty(reply.ErrorCode);
        Assert.Equal(migrate ? 2 : 1, reply.SchemaVersion);
        Assert.Equal(migrate ? 12 : 11, project.Revision);
        Assert.Equal("project-synthetic", project.Id);
        Assert.Equal("clip-main", project.Montage.Clips[0].Id);
        Assert.Equal("clip-main", project.Montage.Clips[0].ActionId);
        Assert.Equal(12, project.Montage.Clips[0].StartFrame);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before)), reply.Fingerprint, ignoreCase: true);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Files.ProjectPath));
        Assert.Equal(files, Directory.GetFiles(fixture.Files.ProjectDirectory, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task Hello_ReportsRoleVersionsAndOwnedParent_AndRejectsWrongSession()
    {
        using var fixture = new BackendFixture();
        var service = fixture.Service();
        var hello = await service.HelloAsync(fixture.Options.SessionKey);
        Assert.Equal("backend", hello.Role);
        Assert.Equal(StudioBackendService.ProtocolVersion, hello.ProtocolVersion);
        Assert.Equal(1, hello.ProjectContractVersion);
        Assert.Equal(StudioBackendService.ProductVersion, hello.ProductVersion);
        Assert.Equal(Environment.ProcessId, hello.ProcessId);
        Assert.Equal(12345, hello.ParentProcessId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.OpenProjectAsync("wrong"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.HelloAsync("wrong"));
    }

    [Theory]
    [InlineData("missing", "project_not_found")]
    [InlineData("invalid", "invalid_project")]
    [InlineData("pending", "recovery_required")]
    [InlineData("ambiguous", "invalid_project")]
    public async Task Open_ClassifiesFailureWithoutMutationOrAutomaticRecovery(string mode, string expected)
    {
        using var fixture = new BackendFixture();
        switch (mode)
        {
            case "missing": File.Delete(fixture.Files.ProjectPath); break;
            case "invalid": File.WriteAllText(fixture.Files.ProjectPath, "{"); break;
            case "pending": File.WriteAllText(fixture.Files.JournalPath, "{} "); break;
            case "ambiguous":
                Directory.CreateDirectory(Path.Combine(fixture.Files.ProjectDirectory, "Production"));
                File.WriteAllBytes(Path.Combine(fixture.Files.ProjectDirectory, "Production", "Studio.project.json"), fixture.Files.InitialBytes);
                break;
        }
        var before = Directory.GetFiles(fixture.Files.ProjectDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var result = await fixture.Service().OpenProjectAsync(fixture.Options.SessionKey);
        Assert.Equal(expected, result.ErrorCode);
        Assert.Empty(result.ProjectJson);
        Assert.Empty(result.Fingerprint);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(fixture.Files.ProjectDirectory, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Open_CancelledReadDoesNotMutateProject()
    {
        using var fixture = new BackendFixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service().OpenProjectAsync(fixture.Options.SessionKey, cancelled.Token));
        Assert.Equal(fixture.Files.InitialBytes, File.ReadAllBytes(fixture.Files.ProjectPath));
        Assert.Empty(fixture.Files.HistoryFiles);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task Open_Utf16PayloadBoundary_AcceptsLimitAndRejectsNextCharacterWithoutWriting(int extra, bool rejected)
    {
        using var fixture = new BackendFixture();
        var baseline = await fixture.Files.Store().OpenAsync();
        var empty = ProjectContractJson.Serialize(baseline.Project with { Name = "" });
        var count = (StudioBackendService.MaxProjectBytes - System.Text.Encoding.Unicode.GetByteCount(empty)) / 2;
        var json = System.Text.Json.Nodes.JsonNode.Parse(baseline.OriginalDocument.GetRawText())!;
        json["name"] = new string('x', count + extra);
        File.WriteAllText(fixture.Files.ProjectPath, json.ToJsonString());
        var before = File.ReadAllBytes(fixture.Files.ProjectPath);
        var reply = await fixture.Service().OpenProjectAsync(fixture.Options.SessionKey);
        Assert.Equal(rejected ? "project_too_large" : "", reply.ErrorCode);
        if (!rejected)
        {
            Assert.Equal(StudioBackendService.MaxProjectBytes, System.Text.Encoding.Unicode.GetByteCount(reply.ProjectJson));
            Assert.Equal(count, ProjectContractJson.Deserialize(reply.ProjectJson).Name.Length);
        }
        else Assert.Empty(reply.ProjectJson);
        Assert.Equal(before, File.ReadAllBytes(fixture.Files.ProjectPath));
        Assert.Empty(fixture.Files.HistoryFiles);
    }
}
