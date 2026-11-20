using System.Text.Json;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Configuration;

internal static class StudioStartup
{
    private const string ConfigName = "Studio.config.local.json";

    internal static string? ProjectPickerConfig(string[] args, UnitLaunchSettings launch, Func<string> defaultConfig)
    {
        if (launch.UnitId is not null || launch.ParentProcessId is not null || launch.ControlPipe is not null || launch.UnitsDisabled)
            return null;

        return args switch
        {
            [] => defaultConfig(),
            ["--projects", "--config", var config] => config,
            _ => null
        };
    }

    internal static string DefaultConfig(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return DefaultConfig(Environment.GetEnvironmentVariable("TRUE_MOON_STUDIO_CONFIG"),
            Environment.CurrentDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    internal static string DefaultConfig(string? configured, string workingDirectory, string localApplicationData)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured), workingDirectory);

        if (string.IsNullOrEmpty(localApplicationData))
            throw new DirectoryNotFoundException("Локальный каталог данных пользователя недоступен.");

        return CreatePersonalConfig(Path.Combine(localApplicationData, "TrueMoon", "Tales Studio"));
    }

    private static string CreatePersonalConfig(string root)
    {
        var config = Path.Combine(root, ConfigName);
        BackendMediaResolver.CheckNoLinks(config, allowMissing: true);
        if (File.Exists(config)) return config;

        var projects = Path.Combine(root, "Projects");
        BackendMediaResolver.CheckNoLinks(projects, allowMissing: true);
        Directory.CreateDirectory(projects);
        var staging = Path.Combine(root, Path.GetRandomFileName());
        var created = false;
        try
        {
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                JsonSerializer.Serialize(output, new
                {
                    version = 1,
                    legacy_root = "Legacy",
                    legacy_read_enabled = false,
                    amv_root = "Projects",
                    projects_root = "Projects",
                    state_root = "State",
                    cache_root = "Cache",
                    exports_root = "Exports",
                    mounts = new Dictionary<string, string>(),
                    project_write_roots = new[] { "Projects" }
                });
            }
            try { File.Move(staging, config); }
            catch (IOException) when (File.Exists(config)) { }
        }
        finally { if (created) File.Delete(staging); }

        BackendMediaResolver.CheckNoLinks(config);
        return config;
    }
}
