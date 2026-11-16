using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Contracts.Compatibility;

/// <summary>A read-only adaptation together with the complete original project document.</summary>
/// <param name="Project">The normalized contract snapshot.</param>
/// <param name="OriginalDocument">An owned clone retaining all legacy fields; not rewritten from the snapshot.</param>
/// <param name="HasScenario">Whether the file contains active scenario intentions.</param>
/// <param name="Issues">Explicit detached links between the draft and last-valid montage.</param>
public sealed record LegacyProjectReadResult(ProjectContract Project, JsonElement OriginalDocument,
    bool HasScenario, IReadOnlyList<LegacyProjectIssue> Issues);

/// <summary>A preserved last-valid placement that no longer attaches to the scenario draft.</summary>
/// <param name="Code">The stable compatibility issue code.</param>
/// <param name="EntityId">The unchanged saved placement identifier.</param>
public sealed record LegacyProjectIssue(string Code, string EntityId);
