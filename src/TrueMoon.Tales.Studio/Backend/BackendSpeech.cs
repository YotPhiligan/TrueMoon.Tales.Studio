using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record MontageSpeechInput(string Role, string Speaker, string? ActionId, int Version, string Origin,
    string Subtitle, int SubtitleOffsetFrames, int SubtitleDurationFrames, bool ConfirmSubtitle);
internal sealed record SpeechVersion(int Number, string AssetId, string Reference, int Stream, string Origin, string Sha256);
internal sealed record SpeechTiming(string WindowId, int Version, int OffsetFrames, int DurationFrames, double SourceInFrame);
internal sealed record SpeechSubtitle(string Text, int OffsetFrames, int DurationFrames, SpeechTiming Confirmed);
internal sealed record MontageSpeech(string Role, string Speaker, string? ActionId, int SelectedVersion,
    SpeechVersion[] Versions, SpeechSubtitle? Subtitle)
{
    internal SpeechVersion Selected => Versions[SelectedVersion - 1];
}

internal static class BackendSpeech
{
    private const int MaxVersions = 16;

    internal static MontageSpeech? Read(JsonNode? node) => node is null ? null :
        Validate(node.Deserialize<MontageSpeech>(BackendTrackEdits.Json));

    internal static MontageSpeech? Read(JsonElement row) =>
        row.TryGetProperty("speech", out var node) && node.ValueKind != JsonValueKind.Null
            ? Validate(node.Deserialize<MontageSpeech>(BackendTrackEdits.Json)) : null;

    private static MontageSpeech Validate(MontageSpeech? speech)
    {
        if (speech is not { Versions.Length: > 0 and <= MaxVersions } ||
            speech.SelectedVersion < 1 || speech.SelectedVersion > speech.Versions.Length ||
            speech.Role is not ("narrator" or "dialogue") || string.IsNullOrWhiteSpace(speech.Speaker) ||
            speech.Versions.Where((row, index) => row is null || row.Number != index + 1 ||
                string.IsNullOrWhiteSpace(row.AssetId) || string.IsNullOrWhiteSpace(row.Reference) ||
                string.IsNullOrWhiteSpace(row.Origin) || !BackendConnection.IsHash(row.Sha256) || row.Stream is < 0 or > 63).Any() ||
            speech.Subtitle is { } caption && (caption.Confirmed is null || caption.OffsetFrames < 0 ||
                caption.DurationFrames <= 0 || string.IsNullOrWhiteSpace(caption.Text)))
            throw Invalid();
        return speech;
    }

    internal static (string Input, int Stream) Source(SaveMontageSound sound, MontageSpeech? previous)
    {
        if (sound.Speech is not { } speech)
        {
            if (previous is not null) throw Invalid();
            return (sound.Input, sound.Stream);
        }
        if (sound.Lane != "voice" || sound.ClipId is not null || speech.Version is < 0 or > MaxVersions ||
            speech.Version > (previous?.Versions.Length ?? 0)) throw Invalid();
        if (speech.Version == 0) return (sound.Input, sound.Stream);
        var version = previous!.Versions[speech.Version - 1];
        return (version.AssetId, version.Stream);
    }

    internal static async Task<MontageSpeech> ApplyAsync(ProjectContract project, SegmentWindowContract window,
        SaveMontageSound sound, MontageSpeech? previous, string assetId, int stream, BackendMediaResolver resolver, CancellationToken token)
    {
        var input = sound.Speech!;
        if (input.Role is not ("narrator" or "dialogue") ||
            input.ActionId is { } action && !project.Segments.Any(row => row.Id == window.SegmentId && row.Actions.Any(value => value.Id == action)))
            throw Invalid();
        var speaker = Required(input.Speaker, 160);
        var versions = previous?.Versions ?? [];
        var reference = BackendScenarioEdit.TryReference(project, assetId) ?? throw Invalid();
        var path = resolver.Resolve(reference);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (file.Length is < 1 or > 512L * 1024 * 1024) throw Invalid();
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
        int selected;
        if (input.Version == 0)
        {
            if (versions.Length == MaxVersions || versions.Any(row => row.Reference == reference || row.Sha256 == sha))
                throw new ScenarioEditException("speech_version_exists");
            selected = versions.Length + 1;
            versions = [..versions, new(selected, assetId, reference, stream, Required(input.Origin, 240), sha)];
        }
        else
        {
            selected = input.Version;
            var prior = versions[selected - 1];
            if (prior.Reference != reference || prior.Sha256 != sha) throw new ScenarioEditException("speech_source_changed");
        }

        SpeechSubtitle? subtitle = null;
        if (input.Subtitle is null) throw Invalid();
        if (!string.IsNullOrWhiteSpace(input.Subtitle))
        {
            if (input.SubtitleOffsetFrames < 0 || input.SubtitleDurationFrames < 1 ||
                (long)input.SubtitleOffsetFrames + input.SubtitleDurationFrames > sound.DurationFrames) throw Invalid();
            var text = Required(input.Subtitle.ReplaceLineEndings("\n"), 350, multiline: true);
            if (text.Count(ch => ch == '\n') > 3) throw Invalid();
            var anchor = new SpeechTiming(window.Id, selected, sound.OffsetFrames, sound.DurationFrames, sound.SourceInFrame);
            subtitle = new(text, input.SubtitleOffsetFrames, input.SubtitleDurationFrames,
                input.ConfirmSubtitle || previous?.Subtitle is null ? anchor : previous.Subtitle.Confirmed);
        }
        return new(input.Role, speaker, input.ActionId, selected, versions, subtitle);
    }

    internal static bool NeedsReview(MontageSpeech speech, string window, int offset, int duration, double source) =>
        speech.Subtitle is { } subtitle && (subtitle.Confirmed != new SpeechTiming(window, speech.SelectedVersion, offset, duration, source) ||
            (long)subtitle.OffsetFrames + subtitle.DurationFrames > duration);

    private static string Required(string? value, int limit, bool multiline = false) =>
        value?.Trim() is { Length: > 0 } text && text.Length <= limit &&
        !text.Any(ch => char.IsControl(ch) && (!multiline || ch != '\n')) ? text : throw Invalid();

    internal static ScenarioEditException Invalid() => new("invalid_speech");
}
