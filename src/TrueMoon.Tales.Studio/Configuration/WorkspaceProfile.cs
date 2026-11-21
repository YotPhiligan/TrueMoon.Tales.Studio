using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TrueMoon.Configuration;

namespace TrueMoon.Tales.Studio.Configuration;

internal sealed record WorkspaceSettings
{
    internal int Version { get; set; }
    internal string? LegacyRoot { get; set; }
    internal bool LegacyReadEnabled { get; set; } = true;
    internal string? AmvRoot { get; set; }
    internal string? ProjectsRoot { get; set; }
    internal string? StateRoot { get; set; }
    internal string? CacheRoot { get; set; }
    internal string? ExportsRoot { get; set; }
    internal string? ProfilesRoot { get; set; }
    internal string? PythonExecutable { get; set; }
    internal string? ComfyRoot { get; set; }
    internal string? FfmpegExecutable { get; set; }
    internal string? FfprobeExecutable { get; set; }
    internal bool ArchiveMedia { get; set; }
    internal string[] ProtectedRoots { get; set; } = [];
    internal string[] ProjectWriteRoots { get; set; } = [];
    internal Dictionary<string, string> Mounts { get; set; } = new(StringComparer.Ordinal);
    internal WorkspaceAlias[] PathAliases { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WorkspaceAlias(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To);

internal static class WorkspaceProfile
{
    private const string Section = "workspace";

    internal static readonly IConfigurationSchema<WorkspaceSettings> Schema = ConfigurationSchemas.Create(
        Section, () => new WorkspaceSettings(),
        value => value with
        {
            ProtectedRoots = [.. value.ProtectedRoots],
            ProjectWriteRoots = [.. value.ProjectWriteRoots],
            Mounts = new(value.Mounts, StringComparer.Ordinal),
            PathAliases = [.. value.PathAliases]
        },
        _ => { },
        fields =>
        {
            fields.Field("version", o => o.Version, (o, v) => o.Version = v,
                codec: new JsonCodec<int>(JsonValueKind.Number, validate: version => version == 1));
            fields.Field("legacy_root", o => o.LegacyRoot, (o, v) => o.LegacyRoot = v);
            fields.Field("legacy_read_enabled", o => o.LegacyReadEnabled, (o, v) => o.LegacyReadEnabled = v, codec: BooleanCodec);
            fields.Field("amv_root", o => o.AmvRoot, (o, v) => o.AmvRoot = v);
            fields.Field("projects_root", o => o.ProjectsRoot, (o, v) => o.ProjectsRoot = v);
            fields.Field("state_root", o => o.StateRoot, (o, v) => o.StateRoot = v);
            fields.Field("cache_root", o => o.CacheRoot, (o, v) => o.CacheRoot = v);
            fields.Field("exports_root", o => o.ExportsRoot, (o, v) => o.ExportsRoot = v);
            fields.Field("profiles_root", o => o.ProfilesRoot, (o, v) => o.ProfilesRoot = v);
            fields.Field("python_executable", o => o.PythonExecutable, (o, v) => o.PythonExecutable = v);
            fields.Field("comfy_root", o => o.ComfyRoot, (o, v) => o.ComfyRoot = v);
            fields.Field("ffmpeg_executable", o => o.FfmpegExecutable, (o, v) => o.FfmpegExecutable = v);
            fields.Field("ffprobe_executable", o => o.FfprobeExecutable, (o, v) => o.FfprobeExecutable = v);
            fields.Field("archive_media", o => o.ArchiveMedia, (o, v) => o.ArchiveMedia = v, codec: BooleanCodec);
            fields.Field("protected_roots", o => o.ProtectedRoots, (o, v) => o.ProtectedRoots = v, codec: PathsCodec);
            fields.Field("project_write_roots", o => o.ProjectWriteRoots, (o, v) => o.ProjectWriteRoots = v, codec: PathsCodec);
            fields.Field("mounts", o => o.Mounts, (o, v) => o.Mounts = v, codec: new MountsCodec());
            fields.Field("path_aliases", o => o.PathAliases, (o, v) => o.PathAliases = v, codec: new AliasesCodec());
        });

    private static JsonCodec<bool> BooleanCodec => new(JsonValueKind.True, JsonValueKind.False);
    private static JsonCodec<string[]> PathsCodec => new(JsonValueKind.Array, validate: values => values.All(IsPath));

    internal static IConfigurationProfile Create(string configPath) => ConfigurationProfiles.Create(profile =>
    {
        profile.AddSchema(Schema);
        profile.AddSource("studio-json", ConfigurationSourcePriority.Files, new WorkspaceJsonSource(configPath));
    });

    internal static WorkspaceSettings Read(string configPath) => ReadAsync(configPath).AsTask().GetAwaiter().GetResult();

    internal static async ValueTask<WorkspaceSettings> ReadAsync(string configPath, CancellationToken token = default)
    {
        try { return Schema.Bind(await Create(configPath).LoadAsync(token).ConfigureAwait(false)); }
        catch (ConfigurationException error) { throw new InvalidDataException(Describe(error), error); }
    }

    internal static string Describe(ConfigurationException error) =>
        "Invalid Studio workspace configuration (" +
        string.Join(", ", error.Issues.Select(issue => issue.Path is { } path ? $"{path}: {issue.Code}" : issue.Code)) + ").";

    internal static string ResolvePath(string path, string configPath) => Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path), Path.GetDirectoryName(configPath)!));

    private static bool IsPath(string? text) => !string.IsNullOrWhiteSpace(text);

    private sealed class WorkspaceJsonSource(string configPath) : IConfigurationSource
    {
        public async ValueTask<IReadOnlyList<ConfigurationEntry>> ReadAsync(IReadOnlyList<ConfigurationKey> keys,
            CancellationToken cancellationToken = default)
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false));
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw Error("JsonRoot");
            var known = keys.ToDictionary(key => key.Path[(Section.Length + 1)..], StringComparer.Ordinal);
            var result = new List<ConfigurationEntry>();
            foreach (var property in json.RootElement.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!known.TryGetValue(property.Name, out var key)) throw Error("UnknownJsonKey");
                if (key.ValueType == typeof(string) && !IsPath(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null))
                    throw Error("JsonKey", key.Path);
                result.Add(new(key.Path, property.Value));
            }
            return result;
        }

        private static ConfigurationException Error(string code, string? path = null) =>
            new([new(code, path, null, "Invalid Studio workspace JSON.")]);
    }

    private class JsonCodec<T>(JsonValueKind kind, JsonValueKind? alternative = null, Func<T, bool>? validate = null)
        : IConfigurationValueCodec<T>
    {
        public virtual T Read(JsonElement value)
        {
            if (value.ValueKind != kind && value.ValueKind != alternative ||
                value.Deserialize<T>() is not { } parsed || validate?.Invoke(parsed) == false)
                throw new FormatException();
            return parsed;
        }

        public JsonElement Write(T value) => JsonSerializer.SerializeToElement(value);
    }

    private sealed class MountsCodec : JsonCodec<Dictionary<string, string>>
    {
        internal MountsCodec() : base(JsonValueKind.Object) { }

        public override Dictionary<string, string> Read(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new FormatException();
            var mounts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var mount in value.EnumerateObject())
            {
                if (!Regex.IsMatch(mount.Name, "\\A[a-z][a-z0-9_-]{0,31}\\z", RegexOptions.CultureInvariant) ||
                    mount.Value.ValueKind != JsonValueKind.String || mount.Value.GetString() is not { } path ||
                    !IsPath(path) || !mounts.TryAdd(mount.Name, path))
                    throw new FormatException();
            }
            return mounts;
        }
    }

    private sealed class AliasesCodec : JsonCodec<WorkspaceAlias[]>
    {
        internal AliasesCodec() : base(JsonValueKind.Array) { }

        public override WorkspaceAlias[] Read(JsonElement value)
        {
            var aliases = base.Read(value);
            var index = 0;
            foreach (var alias in value.EnumerateArray())
            {
                var parsed = aliases[index++];
                if (alias.ValueKind != JsonValueKind.Object || alias.EnumerateObject().Count() != 2 ||
                    parsed is null || !IsPath(parsed.From) || !IsPath(parsed.To))
                    throw new FormatException();
            }
            return aliases;
        }
    }
}
