using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record BackendEpisodeSummary(long Frames, double Fps, int Windows, int WindowsWithVideo,
    int Joins, int DocumentedJoins, int VoiceEvents, int Notes)
{
    internal bool Valid => Frames >= 0 && double.IsFinite(Fps) && Fps > 0 && Windows >= 0 &&
        WindowsWithVideo >= 0 && WindowsWithVideo <= Windows && Joins >= 0 && Joins < Math.Max(1, Windows) &&
        DocumentedJoins >= 0 && DocumentedJoins <= Joins && VoiceEvents >= 0 && Notes >= 0;
}

internal static class BackendEpisodeReadiness
{
    internal static BackendEpisodeSummary Collect(ProjectContract project, string fingerprint, List<BackendReadinessIssue> issues)
    {
        var start = issues.Count;
        var montage = project.Montage;
        var tracks = BackendTrackProjection.Spans(project);
        var frames = montage.Windows.Select(row => (long)row.StartFrame + row.DurationFrames)
            .Concat(tracks.Where(row => row.Track != "markers").Select(row => (long)row.StartFrame + row.DurationFrames))
            .DefaultIfEmpty().Max();
        if (frames < 180 * project.Fps || frames > 300 * project.Fps) Add("episode_duration");

        var takesWithVideo = project.Sources.SelectMany(source => source.Takes
            .Where(take => !string.IsNullOrWhiteSpace(take.VideoAssetId)).Select(take => (SourceId: source.Id, TakeId: take.Id)))
            .ToHashSet();
        var covered = montage.Clips.Where(clip => clip.TakeId is { } take && takesWithVideo.Contains((clip.SourceId, take)))
            .Select(clip => clip.WindowId).ToHashSet(StringComparer.Ordinal);
        foreach (var window in montage.Windows)
            if (!covered.Contains(window.Id)) Add("episode_window_without_video", window);

        var joins = BackendSceneJoins.Pairs(project).ToArray();
        var documented = 0;
        try
        {
            var notes = BackendSceneJoins.Read(project);
            foreach (var pair in joins)
            {
                var note = notes.GetValueOrDefault((pair.Left.Id, pair.Right.Id));
                if (note is null || string.IsNullOrWhiteSpace(note.ExitState) || string.IsNullOrWhiteSpace(note.EntryState))
                    Add("episode_join_states_missing", pair.Right);
                else if (note.ExitState != note.EntryState && string.IsNullOrWhiteSpace(note.Bridge))
                    Add("episode_join_bridge_missing", pair.Right);
                else documented++;
            }
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        { Add("episode_join_data_invalid"); }

        var voice = tracks.Count(row => row.Lane == BackendTrackProjection.SpeechLane && !row.Muted);
        if (voice == 0) Add("episode_voice_missing");
        return new(frames, project.Fps, montage.Windows.Length, covered.Count, joins.Length, documented, voice, issues.Count - start);

        void Add(string reason, SegmentWindowContract? window = null) =>
            issues.Add(new(reason, project.Revision, fingerprint, Frame: window?.StartFrame, WindowId: window?.Id));
    }
}
