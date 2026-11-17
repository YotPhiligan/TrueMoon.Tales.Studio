using TrueMoon.Mithril;

namespace TrueMoon.Tales.Studio.Backend;

// The selected directory and policy come from the launcher, never from an IPC request.
internal sealed record BackendOptions(string ProjectDirectory, string ConfigPath, string Endpoint, string SessionKey)
{
    internal bool WriteEnabled { get; init; }
    internal string? ExecutablePath { get; init; }
    internal string Action { get; init; } = "read";
    internal int HttpPort { get; init; }
    internal bool HttpWithUi { get; init; }
    internal static BackendOptions Create(string directory, string config) => new(
        Path.GetFullPath(directory), Path.GetFullPath(config), "studio-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));

    internal static bool TryParse(string[] args, UnitLaunchSettings launch, out BackendOptions? options,
        out bool backend, out bool inspect)
    {
        options = null;
        backend = args.FirstOrDefault() == "--backend";
        inspect = args.FirstOrDefault() == "--inspect-project";
        if (backend)
        {
            if (args.Length is not (5 or 6) || args.Length == 6 && args[5] != "--writer" || launch.UnitId != 1 || launch.ParentProcessId is null || launch.ControlPipe is null ||
                !args[3].StartsWith("studio-", StringComparison.Ordinal) || args[3].Length != 39 ||
                !Guid.TryParseExact(args[3][7..], "N", out _) || !Guid.TryParseExact(args[4], "N", out _)) return false;
            options = new(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3], args[4]) { WriteEnabled = args.Length == 6 };
            return true;
        }
        if (args.Length < 4 || args[0] is not ("--project" or "--inspect-project" or "--edit-project" or "--migrate-project" or "--recover-project" or "--serve-project") || args[2] != "--config" ||
            launch.UnitId is not null || launch.ParentProcessId is not null || launch.UnitsDisabled) return false;
        var action = args[0] switch { "--edit-project" => "edit", "--migrate-project" => "migrate", "--recover-project" => "recover", "--serve-project" => "serve", _ => "read" };
        var port = 0; var withUi = false; var portSet = false;
        if (action != "serve" && args.Length != 4) return false;
        for (var i = 4; i < args.Length; i++)
        {
            if (args[i] == "--with-ui" && !withUi) withUi = true;
            else if (args[i] == "--http-port" && !portSet && ++i < args.Length &&
                int.TryParse(args[i], out port) && port is >= 0 and <= 65535) portSet = true;
            else return false;
        }
        inspect |= action is "migrate" or "recover";
        options = Create(args[1], args[3]) with { WriteEnabled = action != "read", Action = action, HttpPort = port, HttpWithUi = withUi };
        return true;
    }
}
