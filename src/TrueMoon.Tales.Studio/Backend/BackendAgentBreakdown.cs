using System.Text.Json;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record AgentBreakdownAction(string Id, string Title, string Description, int DurationFrames, int GapBeforeFrames,
    string? SoundNotes, string? ContinuityNotes, long? StartFrame, long? EndFrame);

internal sealed record AgentBreakdownPlan(int ApiVersion, string ProjectId, long ProjectRevision, string Fingerprint, string RequestId,
    string SegmentId, string SegmentName, double Fps, string Status, int? WindowFrames, long? UsedFrames, long ProposedFrames,
    long? RemainingFrames, AgentBreakdownAction[] Items, string[] Issues);

internal static class BackendAgentBreakdown
{
    internal const string AppliedProperty = "studio_applied_agent_breakdowns_v1";
    private const int MaxActions = 2000;

    internal static (AgentBreakdownPlan Plan, ProjectContract? Draft) Calculate(ProjectFileDocument document, string requestId,
        string segmentId, AgentRequestProposalItem[] proposal)
    {
        var project = document.Project;
        var segment = project.Segments.SingleOrDefault(candidate => candidate.Id == segmentId);
        var proposed = proposal.Sum(item => (long)item.DurationFrames + item.GapBeforeFrames);
        var applied = IsApplied(project, requestId);
        var issues = new List<string>();
        var windows = project.Montage.Windows.Where(window => window.SegmentId == segmentId).Take(2).ToArray();
        var window = windows.Length == 1 ? windows[0] : null;
        long? cursor = null;
        long? storyEnd = null;
        long? used = null;
        long? remaining = null;

        if (!applied)
        {
            if (segment is null) issues.Add("segment_missing");
            else if (window is null || window.StartFrame != segment.PlannedWindow.StartFrame ||
                window.DurationFrames != segment.PlannedWindow.DurationFrames ||
                (long)window.StartFrame + window.DurationFrames > int.MaxValue)
                issues.Add("window_undefined_or_inconsistent");
            else
            {
                var end = (long)window.StartFrame + window.DurationFrames;
                if (project.Montage.Windows.Any(other => other.Id != window.Id && other.StartFrame < end &&
                    window.StartFrame < (long)other.StartFrame + other.DurationFrames))
                    issues.Add("window_overlap");
                var clips = project.Montage.Clips.Where(clip => clip.WindowId == window.Id).ToArray();
                var shots = BackendAnimatic.PlannedShots(window, segment.Actions, clips).ToArray();
                if (shots.Any(shot => shot.Start is null || shot.Duration is null)) issues.Add("existing_timing_unresolved");
                else
                {
                    var plannedEnd = shots.Select(shot => (long)shot.Start!.Value + shot.Duration!.Value).DefaultIfEmpty(window.StartFrame).Max();
                    var linkedEnd = clips.Where(clip => clip.ActionId is not null).Select(ClipEnd).DefaultIfEmpty(window.StartFrame).Max();
                    storyEnd = Math.Max(plannedEnd, linkedEnd);
                    cursor = Math.Max(storyEnd.Value, clips.Select(ClipEnd).DefaultIfEmpty(window.StartFrame).Max());
                    used = cursor - window.StartFrame;
                    remaining = end - cursor - proposed;
                    if (remaining < 0) issues.Add("insufficient_frames");
                }
            }
            if (project.Segments.Sum(scene => (long)scene.Actions.Length) + proposal.Length > MaxActions)
                issues.Add("action_limit");
        }

        var tail = cursor;
        var items = proposal.Select((item, index) =>
        {
            var start = cursor + item.GapBeforeFrames;
            cursor = start + item.DurationFrames;
            return new AgentBreakdownAction(ActionId(requestId, index), item.Title.Trim(), item.Description, item.DurationFrames,
                item.GapBeforeFrames, item.SoundNotes, item.ContinuityNotes, start, cursor);
        }).ToArray();
        ProjectContract? draft = null;
        if (!applied && issues.Count == 0)
        {
            var existingIds = project.Segments.SelectMany(scene => scene.Actions).Select(action => action.Id).ToHashSet(StringComparer.Ordinal);
            if (items.Any(item => existingIds.Contains(item.Id))) issues.Add("action_id_conflict");
            else if (OverlapsTransition(project.Montage, items)) issues.Add("transition_conflict");
            else draft = Append(project, segment!, requestId, items, tail > storyEnd);
        }
        return (new(1, project.Id, project.Revision, document.Fingerprint, requestId, segmentId, segment?.Name ?? "",
            project.Fps, applied ? "applied" : draft is not null ? "ready" : "blocked", window?.DurationFrames,
            used, proposed, remaining, items, issues.ToArray()), draft);
    }

    private static bool OverlapsTransition(MontageContract montage, AgentBreakdownAction[] items)
    {
        try
        {
            return BackendBlackTransitions.Compile(montage).Any(transition =>
                items.Any(item => item.StartFrame < transition.InEndFrame && item.EndFrame > transition.OutStartFrame));
        }
        catch (ScenarioEditException) { return true; }
    }

    private static string ActionId(string requestId, int index) => FormattableString.Invariant($"action-{requestId}-{index + 1:D2}");
    private static long ClipEnd(ClipContract clip) => (long)clip.StartFrame + clip.DurationFrames;

    internal static bool IsApplied(ProjectContract project, string id) =>
        AppliedIds(project).Contains(id, StringComparer.Ordinal) ||
        project.Sources.Any(source => source.Properties.TryGetValue("scenario_breakdown_id", out var marker) &&
            marker.ValueKind == JsonValueKind.String && marker.GetString() == id);

    private static string[] AppliedIds(ProjectContract project)
    {
        if (!project.Properties.TryGetValue(AppliedProperty, out var raw)) return [];
        if (raw.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid agent breakdown history.");
        var ids = raw.Deserialize<string[]>() ?? throw new JsonException("Invalid agent breakdown history.");
        if (ids.Any(id => !BackendAgentRequests.ValidId(id))) throw new JsonException("Invalid applied agent request ID.");
        return ids;
    }

    private static ProjectContract Append(ProjectContract project, ScenarioSegmentContract segment, string requestId,
        AgentBreakdownAction[] rows, bool anchorFirst)
    {
        var added = rows.Select((row, index) => new StoryActionContract
        {
            Id = row.Id, Title = row.Title, Description = row.Description, PlannedDurationFrames = row.DurationFrames,
            GapBeforeFrames = row.GapBeforeFrames, SoundNotes = row.SoundNotes, ContinuityNotes = row.ContinuityNotes,
            AnchorFrame = anchorFirst && index == 0 ? checked((int)row.StartFrame!.Value) : null
        });
        var updated = segment with { Actions = [.. segment.Actions, .. added] };
        var properties = new Dictionary<string, JsonElement>(project.Properties)
        {
            [AppliedProperty] = JsonSerializer.SerializeToElement(AppliedIds(project).Append(requestId))
        };
        return project with
        {
            Segments = project.Segments.Select(candidate => candidate.Id == segment.Id ? updated : candidate).ToArray(),
            Properties = properties
        };
    }

    internal static AgentBreakdownPlan Decode(JsonElement response, string id)
    {
        if (response.Deserialize<AgentBreakdownPlan>(AgentRequestDraft.Json) is not { ApiVersion: 1, ProjectRevision: >= 0 } plan ||
            plan.RequestId != id || !BackendConnection.IsHash(plan.Fingerprint) ||
            string.IsNullOrWhiteSpace(plan.ProjectId) || string.IsNullOrWhiteSpace(plan.SegmentId) ||
            !double.IsFinite(plan.Fps) || plan.Fps <= 0 || plan.Items is not { Length: >= 1 and <= 24 } ||
            plan.Issues is null || plan.Status is not ("ready" or "blocked" or "applied"))
            throw new JsonException("Invalid segment breakdown response.");
        if (plan.Items.Where((item, index) => item is null || item.Id != ActionId(id, index) || item.DurationFrames <= 0 ||
            item.GapBeforeFrames < 0 || item.EndFrame != item.StartFrame + item.DurationFrames).Any() ||
            plan.ProposedFrames != plan.Items.Sum(item => (long)item.DurationFrames + item.GapBeforeFrames))
            throw new JsonException("Invalid segment breakdown timing.");
        if (plan.Status == "ready" && (plan.Issues.Length != 0 || plan.WindowFrames is not > 0 || plan.UsedFrames is not >= 0 ||
            plan.RemainingFrames is not >= 0 || plan.WindowFrames - plan.UsedFrames - plan.ProposedFrames != plan.RemainingFrames ||
            plan.Items.Any(item => item.StartFrame is not >= 0 || item.EndFrame is not <= int.MaxValue)))
            throw new JsonException("Invalid ready segment breakdown budget.");
        return plan;
    }
}
