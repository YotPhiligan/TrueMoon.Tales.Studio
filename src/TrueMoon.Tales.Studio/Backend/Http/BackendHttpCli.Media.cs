using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;
using TrueMoon.Alloy.Rendering.Skia;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal static partial class BackendHttpCli
{
    private static bool TryMediaRequest(string command, Dictionary<string, string> options, long revision, string fingerprint,
        out string route, out HttpContent? content, out string? output)
    {
        route = "";
        content = null;
        output = null;
        if (!options.Remove("--asset", out var asset) || !ValidId(asset)) return false;
        if (command == MediaCommand)
        {
            if (options.Count != 0) return false;
            route = BackendHttpServer.MediaRoute;
            content = JsonContent.Create(new BackendHttpMediaQuery
                { AssetId = asset, ExpectedRevision = revision, Fingerprint = fingerprint }, options: BackendHttpServer.Json);
            return true;
        }
        if (!options.Remove("--frame", out var number) || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var frame) ||
            !options.Remove("--output", out var destination) || options.Count != 0 || !TryNewPngPath(destination, out output))
            return false;
        route = BackendHttpServer.FrameRoute;
        content = JsonContent.Create(new BackendHttpFrameQuery
            { AssetId = asset, ExpectedRevision = revision, Fingerprint = fingerprint, Frame = frame }, options: BackendHttpServer.Json);
        return true;
    }

    private static readonly string[] RequiredMediaFields =
        ["error_code", "asset_id", "revision", "fingerprint", "fps", "frame_count", "source_width", "source_height",
         "frame_index", "width", "height", "pixels", "pixel_sha256"];

    private static async Task<int> WriteMediaResultAsync(JsonElement snapshot, BackendHttpMediaQuery query, string? output,
        CancellationToken token)
    {
        if (!ValidEnvelope(snapshot) || snapshot.EnumerateObject().Count() != 3 ||
            !snapshot.TryGetProperty("media", out var data) || data.ValueKind != JsonValueKind.Object ||
            data.EnumerateObject().Count() != RequiredMediaFields.Length || !RequiredMediaFields.All(name => data.TryGetProperty(name, out _)) ||
            data.Deserialize<BackendMediaReply>(BackendHttpServer.Json) is not { ErrorCode.Length: 0 } reply)
            throw new JsonException("Invalid backend media snapshot.");
        var frame = query is BackendHttpFrameQuery requested ? requested.Frame : -1;
        BackendConnection.ValidateMedia(reply, query.AssetId, frame, query.ExpectedRevision, query.Fingerprint);
        if (output is null)
        {
            Console.WriteLine(snapshot.GetRawText());
            return 0;
        }

        byte[] png;
        try { png = EncodePng(reply); }
        catch (Exception error) when (error is InvalidDataException or DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
        { return Fail("frame_encode_failed", "Не удалось преобразовать кадр в PNG."); }
        try { await PublishPngAsync(output, png, token).ConfigureAwait(false); }
        catch (IOException) when (Path.Exists(output))
        { return Fail("output_exists", "По указанному пути уже есть файл или каталог."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Fail("output_unavailable", "Не удалось сохранить PNG в указанной папке."); }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            api_version = 1,
            schema_version = 2,
            media = data.EnumerateObject().Where(field => !field.NameEquals("pixels")).ToDictionary(field => field.Name, field => field.Value),
            output_path = output,
            png_sha256 = Convert.ToHexStringLower(SHA256.HashData(png))
        }));
        return 0;
    }

    private static bool TryNewPngPath(string value, out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            if (value.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && value.StartsWith("~\\", StringComparison.Ordinal))
                value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]);
            if (OperatingSystem.IsWindows())
            {
                value = value.Replace('/', '\\');
                if (value.StartsWith(@"\\.\", StringComparison.Ordinal) || value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                    value.EndsWith(' ') || value.EndsWith('.'))
                    return false;
            }
            var candidate = Path.GetFullPath(value);
            if (!Path.GetExtension(candidate).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(Path.GetDirectoryName(candidate)) || Path.Exists(candidate))
                return false;
            if (OperatingSystem.IsWindows())
            {
                var relative = candidate.AsSpan(Path.GetPathRoot(candidate)!.Length);
                var file = Path.GetFileName(candidate);
                var stem = file[..file.IndexOf('.')].TrimEnd(' ').ToUpperInvariant();
                if (relative.Contains(':') || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    stem is "CON" or "PRN" or "AUX" or "NUL" ||
                    stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    "0123456789¹²³".Contains(stem[3]))
                    return false;
            }
            path = candidate;
            return true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        { return false; }
    }

    private static byte[] EncodePng(BackendMediaReply frame)
    {
        SkiaNativeLibrary.Initialize();
        using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var pixels = bitmap.GetPixels();
        if (pixels == IntPtr.Zero || bitmap.ByteCount != frame.Pixels.Length)
            throw new InvalidDataException("Could not allocate a bitmap for the backend frame.");
        Marshal.Copy(frame.Pixels, 0, pixels, frame.Pixels.Length);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100) ??
            throw new InvalidDataException("Could not encode the backend frame as PNG.");
        return encoded.ToArray();
    }

    private static async Task PublishPngAsync(string output, byte[] png, CancellationToken token)
    {
        var stage = Path.Combine(Path.GetDirectoryName(output)!, $".studio-frame-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            await using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                created = true;
                await file.WriteAsync(png, token).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(stage, output);
            created = false;
        }
        finally { if (created) File.Delete(stage); }
    }
}
