using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TrueMoon.Tales.Studio.Contracts.Compatibility;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;

namespace TrueMoon.Tales.Studio.Persistence;

/// <summary>Versioned storage adaptation; encoding never performs filesystem operations.</summary>
public static class ProjectFileCodec
{
    private static readonly JsonSerializerOptions StorageOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        WriteIndented = true
    };

    internal static byte[] SerializeNew(ProjectContract project) => JsonSerializer.SerializeToUtf8Bytes(new JsonObject
    {
        ["schema_version"] = 2,
        ["project"] = JsonNode.Parse(ProjectContractJson.Serialize(project))
    }, StorageOptions);

    /// <summary>Decodes legacy schema 1 or the versioned storage envelope of schema 2.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>A complete detached storage document.</returns>
    public static ProjectFileDocument Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Read(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Decodes exact UTF8 bytes, accepting a BOM without losing the original fingerprint.</summary>
    /// <param name="utf8">The file bytes.</param>
    /// <returns>A complete detached storage document.</returns>
    public static ProjectFileDocument Read(ReadOnlyMemory<byte> utf8)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(utf8.Span));
        var payload = utf8.Length >= 3 && utf8.Span[0] == 0xEF && utf8.Span[1] == 0xBB && utf8.Span[2] == 0xBF ? utf8[3..] : utf8;
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var schema = Version(root, "schema_version");
        if (schema == 1)
        {
            var adapted = LegacyProjectReader.Read(root);
            return new(1, adapted.Project, adapted.OriginalDocument, fingerprint);
        }
        if (schema != 2) throw new NotSupportedException($"Unsupported project storage schema {schema}.");
        if (!root.TryGetProperty("project", out var project)) throw new JsonException("Missing stored project.");
        if (Version(project, "contract_version") != 1) throw new NotSupportedException("Unsupported stored contract version.");
        var snapshot = project.Deserialize<ProjectContract>(StorageOptions) ?? throw new JsonException("Missing project snapshot.");
        ProjectContractJson.Validate(snapshot);
        return new(2, snapshot, root.Clone(), fingerprint);
    }

    /// <summary>Explicitly encodes a legacy document as schema 2, retaining its entire original JSON.</summary>
    /// <param name="source">The legacy baseline.</param>
    /// <param name="project">The validated new snapshot.</param>
    /// <returns>UTF8 storage bytes; no file is changed.</returns>
    public static byte[] SerializeMigration(ProjectFileDocument source, ProjectContract project)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SchemaVersion != 1) throw new InvalidOperationException("Only schema 1 requires migration.");
        SameProject(source, project);
        var root = new JsonObject
        {
            ["schema_version"] = 2,
            ["project"] = JsonNode.Parse(ProjectContractJson.Serialize(project)),
            ["legacy_document"] = JsonNode.Parse(source.OriginalDocument.GetRawText())
        };
        return Encoding.UTF8.GetBytes(root.ToJsonString(StorageOptions));
    }

    /// <summary>Encodes an updated schema 2 snapshot while retaining unknown envelope/entity properties.</summary>
    /// <param name="source">The loaded storage baseline.</param>
    /// <param name="project">The desired project state.</param>
    /// <returns>UTF8 storage bytes; metadata omissions retain prior values, explicit null clears a value.</returns>
    public static byte[] Serialize(ProjectFileDocument source, ProjectContract project)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
        SameProject(source, project);
        var root = JsonNode.Parse(source.OriginalDocument.GetRawText())!.AsObject();
        var updated = JsonNode.Parse(ProjectContractJson.Serialize(project));
        root["project"] = Overlay(root["project"], updated);
        return Encoding.UTF8.GetBytes(root.ToJsonString(StorageOptions));
    }

    private static void SameProject(ProjectFileDocument source, ProjectContract project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ProjectContractJson.Validate(project);
        if (source.Project.Id != project.Id) throw new InvalidOperationException("A save cannot change project identity.");
    }

    private static int Version(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
            throw new JsonException($"Explicit integer {property} is required.");
        return number;
    }

    private static JsonNode? Overlay(JsonNode? previous, JsonNode? next)
    {
        if (previous is JsonObject oldObject && next is JsonObject newObject)
        {
            var result = oldObject.DeepClone().AsObject();
            foreach (var (key, value) in newObject) result[key] = Overlay(result[key], value);
            return result;
        }
        if (previous is JsonArray oldArray && next is JsonArray newArray)
        {
            var key = IdentityKey(oldArray, newArray);
            if (key is not null)
            {
                var existing = oldArray.ToDictionary(item => item![key]!.GetValue<string>(), StringComparer.Ordinal);
                return new JsonArray(newArray.Select(item => existing.TryGetValue(item![key]!.GetValue<string>(), out var old)
                    ? Overlay(old, item) : item.DeepClone()).ToArray());
            }
        }
        return next?.DeepClone();
    }

    private static string? IdentityKey(JsonArray previous, JsonArray next)
    {
        foreach (var key in new[] { "id", "source_id" })
        {
            bool Unique(JsonArray rows)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                return rows.Count > 0 && rows.All(row => row is JsonObject obj && obj[key] is JsonValue value &&
                    value.TryGetValue<string>(out var id) && !string.IsNullOrEmpty(id) && ids.Add(id));
            }
            if (Unique(previous) && Unique(next)) return key;
        }
        return null;
    }
}
