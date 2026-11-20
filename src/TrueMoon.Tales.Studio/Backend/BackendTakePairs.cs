using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record TakeVideoSnapshot(string TakeId, string Name, string AssetId, string Reference, string Sha256,
    double Fps, int Frames, int Width, int Height);

internal sealed record DraftFinalPair(string Method, TakeVideoSnapshot Draft, TakeVideoSnapshot Final)
{
    internal string MethodName => Method == BackendTakePairs.FullTransfer ? "Full Transfer" : "Прямая генерация";

    internal int DraftFrameAt(int finalFrame) => (int)Math.Min(Draft.Frames - 1, Math.Floor(finalFrame * Draft.Fps / Final.Fps));
}

internal static class BackendTakePairs
{
    private const string Key = "studio_draft_final_v1";
    internal const string Direct = "direct";
    internal const string FullTransfer = "full_transfer";

    internal static DraftFinalPair? Read(TakeContract take)
    {
        if (!take.Properties.TryGetValue(Key, out var raw)) return null;
        return raw.Deserialize<DraftFinalPair>(BackendAdaptation.Json) is { } pair && ValidMethod(pair.Method) &&
            Valid(pair.Draft) && Valid(pair.Final) && pair.Final.TakeId == take.Id && pair.Final.AssetId == take.VideoAssetId &&
            pair.Draft.TakeId != pair.Final.TakeId && pair.Draft.AssetId != pair.Final.AssetId &&
            pair.Draft.Sha256 != pair.Final.Sha256 && (pair.Method != FullTransfer || TransferGrid(pair.Draft, pair.Final))
            ? pair : throw new InvalidDataException("Invalid draft/final take provenance.");
    }

    internal static async Task<ProjectContract> LinkAsync(ProjectContract project, LinkDraftFinalTake edit,
        BackendMediaReader reader, CancellationToken token)
    {
        if (!ValidMethod(edit.Method)) throw new ScenarioEditException("invalid_take_pair");
        var source = project.Sources.FirstOrDefault(row => row.Id == edit.SourceId) ?? throw Invalid();
        var draft = source.Takes.FirstOrDefault(row => row.Id == edit.DraftTakeId);
        var final = source.Takes.FirstOrDefault(row => row.Id == edit.FinalTakeId);
        if (draft?.VideoAssetId is null || final?.VideoAssetId is null || draft.VideoAssetId == final.VideoAssetId) throw Invalid();
        if (Read(final) is { } previous)
        {
            if (previous.Draft.TakeId == draft.Id && previous.Method == edit.Method) return project;
            throw new ScenarioEditException("take_pair_recorded");
        }
        if (Read(draft) is not null || source.Takes.Any(row => Read(row)?.Draft.TakeId == final.Id)) throw Invalid();

        var a = await reader.SnapshotTakeAsync(project, draft, token).ConfigureAwait(false);
        var b = await reader.SnapshotTakeAsync(project, final, token).ConfigureAwait(false);
        if (a.Sha256 == b.Sha256) throw Invalid();
        if (edit.Method == FullTransfer && !TransferGrid(a, b)) throw new ScenarioEditException("take_pair_timing");
        var recorded = WithPair(final, new DraftFinalPair(edit.Method, a, b));
        var updated = source with { Takes = source.Takes.Select(row => row.Id == final.Id ? recorded : row).ToArray() };
        return project with { Sources = project.Sources.Select(row => row.Id == source.Id ? updated : row).ToArray() };
    }

    internal static TakeContract WithPair(TakeContract take, DraftFinalPair pair) => take with
    {
        Properties = new Dictionary<string, JsonElement>(take.Properties)
        {
            [Key] = JsonSerializer.SerializeToElement(pair, BackendAdaptation.Json)
        }
    };

    internal static (TakeVideoSnapshot Sample, int Frame) Frame(ProjectContract project, string sourceId, string finalTakeId, bool draft, int finalFrame)
    {
        var source = project.Sources.FirstOrDefault(row => row.Id == sourceId);
        var take = source?.Takes.FirstOrDefault(row => row.Id == finalTakeId);
        var pair = take is null ? null : Read(take);
        if (source is null || pair is null || !source.Takes.Any(row => row.Id == pair.Draft.TakeId && row.VideoAssetId == pair.Draft.AssetId))
            throw Invalid();
        if (finalFrame < 0 || finalFrame >= pair.Final.Frames) throw new ScenarioEditException("invalid_frame");
        return draft ? (pair.Draft, pair.DraftFrameAt(finalFrame)) : (pair.Final, finalFrame);
    }

    private static bool TransferGrid(TakeVideoSnapshot draft, TakeVideoSnapshot final) =>
        draft.Fps == 24 && final.Fps == 24 && draft.Frames == final.Frames;

    private static bool ValidMethod(string? method) => method is Direct or FullTransfer;

    private static bool Valid(TakeVideoSnapshot? sample) => sample is
        { TakeId.Length: > 0, AssetId.Length: > 0, Name.Length: > 0, Reference.Length: > 0, Frames: > 0 and <= BackendMediaDecoder.MaxFrames,
            Width: > 0 and <= 8192, Height: > 0 and <= 8192 } && double.IsFinite(sample.Fps) && sample.Fps is > 0 and <= 240 &&
        BackendConnection.IsHash(sample.Sha256);

    private static ScenarioEditException Invalid() => new("invalid_take_pair");
}
