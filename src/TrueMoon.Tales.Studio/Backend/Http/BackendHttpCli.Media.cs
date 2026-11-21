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
            !options.Remove("--output", out var destination) || options.Count != 0 || !TryNewOutputPath(destination, ".png", out output))
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
        try
        {
            await PublishOutputAsync(output, ".studio-frame-", async file =>
            {
                await file.WriteAsync(png, token).ConfigureAwait(false);
                return 0;
            }, token).ConfigureAwait(false);
        }
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
}
