using System.Text.Json.Nodes;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Tests.Backend;

internal sealed class BackendWriteFixture : IDisposable
{
    internal BackendFixture Base { get; } = new();
    internal Persistence.PersistenceFixture Files => Base.Files;
    internal BackendOptions Options { get; }

    internal BackendWriteFixture(bool scenario = false)
    {
        var config = JsonNode.Parse(File.ReadAllText(Base.ConfigPath))!;
        config["project_write_roots"] = new JsonArray(Files.ProjectDirectory);
        File.WriteAllText(Base.ConfigPath, config.ToJsonString());
        if (!scenario)
        {
            var project = JsonNode.Parse(File.ReadAllText(Files.ProjectPath))!;
            project["timeline"]!.AsObject().Remove("scenario");
            File.WriteAllText(Files.ProjectPath, project.ToJsonString());
        }
        Options = Base.Options with { WriteEnabled = true, Action = "edit", ExecutablePath = Executable() };
    }

    internal StudioBackendService Service() => new(Options, new(BackendProjectAccess.Read(Options)), new UnitLaunchSettings(1, 12345, "test-control"));

    internal static string Executable()
    {
        if (Environment.GetEnvironmentVariable("STUDIO_BACKEND_EXE") is { Length: > 0 } executable) return executable;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Name;
        while (!File.Exists(Path.Combine(directory.FullName, "TrueMoon.Tales.Studio.slnx"))) directory = directory.Parent ?? throw new InvalidOperationException("Studio source root not found.");
        return Path.Combine(directory.FullName, ".local", "studio-build", "bin", "TrueMoon.Tales.Studio", configuration, "TrueMoon.Tales.Studio.exe");
    }

    internal async Task<BackendProjectReply> MigrateAsync(StudioBackendService service)
    {
        var opened = await service.OpenProjectAsync(Options.SessionKey);
        var acquired = await service.AcquireWriterAsync(Options.SessionKey, 11, opened.Fingerprint);
        Xunit.Assert.Empty(acquired.ErrorCode);
        var migrated = await service.MigrateProjectAsync(Options.SessionKey, 11, opened.Fingerprint);
        Xunit.Assert.Empty(migrated.ErrorCode);
        return migrated;
    }

    public void Dispose() => Base.Dispose();
}
