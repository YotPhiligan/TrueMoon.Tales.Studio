using System.Globalization;
using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed partial class BackendNativeCliTests
{
    [Fact]
    public async Task NativeClipAudio_PersistsToggleAndSharesEditorUndo()
    {
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var baseline = await fixture.Connection.OpenAsync();
        Assert.False(ClipAudioEnabled(baseline));
        var projectBytes = File.ReadAllBytes(files.Files.ProjectPath);
        var enableArgs = ClipAudioArgs("true", "clip-audio-activate-01", baseline);

        var receipt = ScenarioSuccess(await RunScenarioAsync(fixture, enableArgs));
        Assert.True(receipt.GetProperty("clip_audio").GetBoolean());
        Assert.True(receipt.GetProperty("changed").GetBoolean());
        var enabled = await fixture.Connection.OpenAsync();
        Assert.True(ClipAudioEnabled(enabled));
        Assert.Equal(baseline.Project.Revision + 1, enabled.Project.Revision);
        Assert.Equal(enabled.Project.Revision, receipt.GetProperty("project_revision").GetInt64());
        Assert.True(enabled.CanUndoMontage);
        Assert.Equal(2, files.Files.HistoryFiles.Length);
        Assert.Contains(files.Files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(projectBytes));
        Assert.Equal(baseline.Project.Montage.Clips, enabled.Project.Montage.Clips);
        Assert.Equal(baseline.Project.Montage.Windows, enabled.Project.Montage.Windows);
        foreach (var (name, value) in baseline.Project.Montage.Properties.Where(entry => entry.Key != "clip_audio"))
            Assert.True(JsonElement.DeepEquals(value, enabled.Project.Montage.Properties[name]), name);

        var undone = await fixture.Connection.EditScenarioAsync(new UndoMontage(), enabled);
        Assert.False(ClipAudioEnabled(undone));
        Assert.True(undone.CanRedoMontage);
        Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(await RunScenarioAsync(fixture, enableArgs))));
        Assert.Equal(undone.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(3, files.Files.HistoryFiles.Length);

        var redone = await fixture.Connection.EditScenarioAsync(new RedoMontage(), undone);
        Assert.True(ClipAudioEnabled(redone));
        var disabled = ScenarioSuccess(await RunScenarioAsync(fixture, ClipAudioArgs("false", "clip-audio-disable-01", redone)));
        Assert.False(disabled.GetProperty("clip_audio").GetBoolean());
        Assert.True(disabled.GetProperty("changed").GetBoolean());
        var saved = await fixture.Connection.OpenAsync();
        Assert.False(saved.Project.Montage.Properties["clip_audio"].GetBoolean());
        Assert.Equal(redone.Project.Revision + 1, saved.Project.Revision);
        Assert.Equal(5, files.Files.HistoryFiles.Length);
    }

    [Fact]
    public async Task NativeClipAudio_RejectsStaleBaselineAndSkipsNoOpWrites()
    {
        using var files = new BackendWriteFixture();
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var head = await fixture.Connection.OpenAsync();
        var before = File.ReadAllBytes(files.Files.ProjectPath);
        const string key = "clip-audio-refusal-01";
        var off = ScenarioSuccess(await RunScenarioAsync(fixture, ClipAudioArgs("false", key, head)));
        Assert.False(off.GetProperty("changed").GetBoolean());
        Assert.False(off.GetProperty("clip_audio").GetBoolean());
        Assert.Equal(head.Project.Revision, off.GetProperty("project_revision").GetInt64());
        var stale = head with { Project = head.Project with { Revision = head.Project.Revision - 1 } };
        AssertError(await RunScenarioAsync(fixture, ClipAudioArgs("true", key, stale)), 3, "revision_conflict", reconcile: true);
        AssertError(await RunScenarioAsync(fixture, ClipAudioArgs("true", key, head with { Fingerprint = new string('0', 64) })),
            3, "revision_conflict", reconcile: true);
        AssertError(await RunScenarioAsync(fixture, ClipAudioArgs("yes", key, head)), 2, "invalid_arguments", reconcile: false);
        Assert.Equal(before, File.ReadAllBytes(files.Files.ProjectPath));
        Assert.Single(files.Files.HistoryFiles);

        var first = ScenarioSuccess(await RunScenarioAsync(fixture, ClipAudioArgs("true", key, head)));
        Assert.True(first.GetProperty("changed").GetBoolean());
        var updated = await fixture.Connection.OpenAsync();
        var noChange = ScenarioSuccess(await RunScenarioAsync(fixture, ClipAudioArgs("true", "clip-audio-noop-0001", updated)));
        Assert.False(noChange.GetProperty("changed").GetBoolean());
        Assert.True(noChange.GetProperty("clip_audio").GetBoolean());
        Assert.Equal(updated.Project.Revision, noChange.GetProperty("project_revision").GetInt64());
        Assert.Equal(updated.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(2, files.Files.HistoryFiles.Length);
    }

    private static bool ClipAudioEnabled(OpenedBackendProject project) =>
        project.Project.Montage.Properties.TryGetValue("clip_audio", out var value) && value.GetBoolean();

    private static string[] ClipAudioArgs(string enabled, string key, OpenedBackendProject baseline) =>
        ["backend-montage-clip-audio", "--enabled", enabled, "--client-key", key, "--sha256", baseline.Fingerprint,
            "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture)];
}
