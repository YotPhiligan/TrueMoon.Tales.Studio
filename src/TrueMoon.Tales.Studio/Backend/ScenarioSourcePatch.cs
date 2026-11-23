using System.Text.Json;
using System.Text.Json.Serialization;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record ScenarioSourcePatch : ScenarioCommand
{
    internal const int MaxBodyBytes = 384 * 1024;
    public required string SourceId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Title { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Notes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Prompt { get; init; }

    internal bool Valid => SourceId is not null && BackendAgentContext.ValidSelector(SourceId) &&
        (Title.ValueKind != JsonValueKind.Undefined || Notes.ValueKind != JsonValueKind.Undefined ||
            Prompt.ValueKind != JsonValueKind.Undefined) &&
        OptionalText(Title, 160) &&
        (Title.ValueKind == JsonValueKind.Undefined || !string.IsNullOrWhiteSpace(Title.GetString())) &&
        OptionalText(Notes, 10_000) && OptionalText(Prompt, 100_000);

    internal ProjectContract Apply(ProjectContract project)
    {
        var index = Array.FindIndex(project.Sources, source => source.Id == SourceId);
        if (index < 0) throw new ScenarioEditException("invalid_scenario_edit");

        var source = project.Sources[index];
        var title = Title.ValueKind == JsonValueKind.Undefined ? source.Title : Title.GetString()!.Trim();
        var changeNotes = Changed(source, "notes", Notes);
        var changePrompt = Changed(source, "prompt", Prompt);
        if (title == source.Title && !changeNotes && !changePrompt) return project;

        var properties = new Dictionary<string, JsonElement>(source.Properties);
        if (changeNotes) properties["notes"] = Notes;
        if (changePrompt) properties["prompt"] = Prompt;
        var sources = (SourceContract[])project.Sources.Clone();
        sources[index] = source with { Title = title, Properties = properties };
        return project with { Sources = sources };
    }

    private static bool OptionalText(JsonElement value, int limit) => value.ValueKind == JsonValueKind.Undefined ||
        value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= limit;

    private static bool Changed(SourceContract source, string key, JsonElement submitted) =>
        submitted.ValueKind != JsonValueKind.Undefined &&
        (!source.Properties.TryGetValue(key, out var existing) || !JsonElement.DeepEquals(existing, submitted));
}
