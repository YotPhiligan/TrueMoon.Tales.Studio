using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Configuration;
using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Configuration;

internal static partial class WorkspaceProfile
{
    private sealed partial class WorkspaceJsonSource
    {
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public ValueTask WriteAsync(IReadOnlyList<ConfigurationKey> keys, IReadOnlyList<ConfigurationEdit> edits,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (edits.Count == 0) return ValueTask.CompletedTask;

            // The framework serializes one App. A path-scoped mutex serializes all Studio writers.
            var path = Path.GetFullPath(configPath);
            var mutexKey = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mutexKey)));
            using var gate = new Mutex(false, "TrueMoon.Studio.Config." + hash);
            try
            {
                if (!gate.WaitOne(TimeSpan.FromSeconds(2))) throw new IOException("The workspace configuration is busy.");
            }
            catch (AbandonedMutexException) { }
            try { Commit(path, edits, cancellationToken); }
            finally { gate.ReleaseMutex(); }
            return ValueTask.CompletedTask;
        }

        private void Commit(string path, IReadOnlyList<ConfigurationEdit> edits, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            BackendMediaResolver.CheckNoLinks(path);
            var current = File.ReadAllText(path);
            if (observedText is not null && !string.Equals(observedText, current, StringComparison.Ordinal))
                throw new IOException("The workspace configuration changed.");

            var root = JsonNode.Parse(current)!.AsObject();
            foreach (var edit in edits)
            {
                var property = edit.Path[(Section.Length + 1)..];
                if (edit.Value is { } value) root[property] = JsonNode.Parse(value.GetRawText());
                else root.Remove(property);
            }

            var candidate = root.ToJsonString(WriteOptions) + Environment.NewLine;
            // Rebind the complete candidate, including fields left untouched by the patch.
            var profile = Create(path, candidate);
            _ = Schema.Bind(profile.LoadAsync(token).AsTask().GetAwaiter().GetResult());
            var staging = Path.Combine(Path.GetDirectoryName(path)!, ".studio-config-" + Guid.NewGuid().ToString("N") + ".tmp");
            var owned = false;
            try
            {
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    owned = true;
                    stream.Write(Encoding.UTF8.GetBytes(candidate));
                    stream.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                BackendMediaResolver.CheckNoLinks(path);
                if (!string.Equals(current, File.ReadAllText(path), StringComparison.Ordinal))
                    throw new IOException("The workspace configuration changed.");
                File.Replace(staging, path, destinationBackupFileName: null);
                owned = false;
            }
            finally
            {
                if (owned) File.Delete(staging);
            }
        }
    }
}
