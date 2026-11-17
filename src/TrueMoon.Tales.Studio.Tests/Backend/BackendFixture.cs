using System.Text.Json;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Tests.Persistence;

namespace TrueMoon.Tales.Studio.Tests.Backend;

internal sealed class BackendFixture : IDisposable
{
    internal PersistenceFixture Files { get; } = new();
    internal string ConfigPath { get; }
    internal BackendOptions Options { get; }
    internal BackendFixture()
    {
        ConfigPath = Path.Combine(Files.Root, "Studio.config.json");
        Configure();
        Options = BackendOptions.Create(Files.ProjectDirectory, ConfigPath);
    }

    internal void Configure(string? root = null, string[]? protectedRoots = null, string? legacyRoot = null) =>
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new
        {
            version = 1, projects_root = root ?? Files.Root, protected_roots = protectedRoots ?? [],
            legacy_read_enabled = false, legacy_root = legacyRoot ?? Path.Combine(Files.Root, "legacy"),
            project_write_roots = new[] { Files.ProjectPath }
        }));

    internal StudioBackendService Service() => new(Options, new(BackendProjectAccess.Read(Options)),
        new UnitLaunchSettings(1, 12345, "studio-unit-test"));

    public void Dispose() => Files.Dispose();
}
