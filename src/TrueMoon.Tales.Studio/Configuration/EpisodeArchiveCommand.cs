using System.Globalization;
using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Export;

namespace TrueMoon.Tales.Studio.Configuration;

internal static class EpisodeArchiveCommand
{
    internal const string Usage = "Архив: --archive-episode <каталог> --config <json> --revision <номер> --sha256 <SHA проекта>\n" +
        "Восстановление: --restore-episode <Episode-id.zip> --config <json> --sha256 <SHA архива>";

    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        void Interrupt(object? sender, ConsoleCancelEventArgs signal) { signal.Cancel = true; operation.Cancel(); }
        Console.CancelKeyPress += Interrupt;
        try
        {
            object receipt;
            switch (args)
            {
                case ["--archive-episode", var directory, "--config", var config, "--revision", var number, "--sha256", var sha]
                    when long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) && BackendExportStorage.ValidHash(sha):
                    receipt = await BackendEpisodeArchive.CreateAsync(BackendOptions.CreateForLocation(directory, config), revision, sha, operation.Token).ConfigureAwait(false);
                    break;
                case ["--restore-episode", var name, "--config", var config, "--sha256", var sha] when BackendExportStorage.ValidHash(sha):
                    receipt = await BackendEpisodeArchive.RestoreAsync(Path.GetFullPath(config), name, sha, operation.Token).ConfigureAwait(false);
                    break;
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
            Console.WriteLine(JsonSerializer.Serialize(receipt, BackendAdaptation.Json));
            return 0;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { return 130; }
        finally { Console.CancelKeyPress -= Interrupt; }
    }
}
