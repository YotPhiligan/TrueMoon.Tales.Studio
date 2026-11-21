using System.Security.Cryptography;
using System.Text;

namespace TrueMoon.Tales.Studio.Configuration;

internal static class StudioConfigurationWriteGate
{
    // TrueMoon serializes updates within one App. The file gate also covers Studio windows in other processes.
    internal static void Run(string absolutePath, Action write)
    {
        var key = OperatingSystem.IsWindows() ? absolutePath.ToUpperInvariant() : absolutePath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        using var gate = new Mutex(false, "TrueMoon.Studio.Config." + hash);
        try
        {
            if (!gate.WaitOne(TimeSpan.FromSeconds(2))) throw new IOException("The configuration is busy.");
        }
        catch (AbandonedMutexException) { }
        try { write(); }
        finally { gate.ReleaseMutex(); }
    }
}
