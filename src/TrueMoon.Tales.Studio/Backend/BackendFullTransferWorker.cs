using TrueMoon.Tales.Studio.Configuration;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record BackendFullTransferWorker(string Python, string ComfyRoot, string Script)
{
    internal static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "FullTransferWorker");

    internal static BackendFullTransferWorker Read(BackendOptions options)
    {
        var config = WorkspaceProfile.Read(options.ConfigPath);
        if (config.PythonExecutable is not { } python) throw new ScenarioEditException("transfer_unavailable");
        python = WorkspaceProfile.ResolvePath(python, options.ConfigPath);
        var script = Path.Combine(DirectoryPath, "ManagedRun.py");
        if (!File.Exists(python) || !File.Exists(script)) throw new ScenarioEditException("transfer_unavailable");
        BackendMediaResolver.CheckNoLinks(script);
        return new(python, ReadComfyRoot(options, config), script);
    }

    internal static string ReadComfyRoot(BackendOptions options, WorkspaceSettings? settings = null)
    {
        var config = settings ?? WorkspaceProfile.Read(options.ConfigPath);
        if (config.ComfyRoot is not { } root) throw new ScenarioEditException("transfer_unavailable");
        var path = WorkspaceProfile.ResolvePath(root, options.ConfigPath);
        if (!Directory.Exists(path)) throw new ScenarioEditException("transfer_unavailable");
        BackendMediaResolver.CheckNoLinks(path);
        return path;
    }
}
