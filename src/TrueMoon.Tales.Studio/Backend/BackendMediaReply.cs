using System.Text.Json.Serialization;

namespace TrueMoon.Tales.Studio.Backend;

/// <summary>Bounded video metadata or a single 480 by 270 BGRA frame; never contains filesystem authority.</summary>
public sealed class BackendMediaReply
{
    /// <summary>Empty on success; otherwise a stable refusal code.</summary>
    public string ErrorCode { get; set; } = "";
    /// <summary>The project-selected asset identity.</summary>
    public string AssetId { get; set; } = "";
    /// <summary>The exact project revision used for this query.</summary>
    public long Revision { get; set; }
    /// <summary>SHA256 of the exact project bytes used for this query.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>The constant source frame rate.</summary>
    public double Fps { get; set; }
    /// <summary>The bounded source frame count.</summary>
    public int FrameCount { get; set; }
    /// <summary>Original video width.</summary>
    public int SourceWidth { get; set; }
    /// <summary>Original video height.</summary>
    public int SourceHeight { get; set; }
    /// <summary>Source frame index, or minus one for metadata only.</summary>
    public int FrameIndex { get; set; } = -1;
    /// <summary>The output pixel width.</summary>
    public int Width { get; set; }
    /// <summary>The output pixel height.</summary>
    public int Height { get; set; }
    internal byte[] Pixels { get; set; } = [];
    /// <summary>Raw binary wire payload. Incoming memory is copied into owned pixels and any whole TrueMoon rental is returned.</summary>
    [JsonPropertyName("pixels")]
    public Memory<byte> PixelBuffer
    {
        get => Pixels;
        set
        {
            try { Pixels = value.ToArray(); }
            finally { MemoryPoolUtils.TryReturn(value); }
        }
    }
    /// <summary>SHA256 of the returned pixel bytes, empty for metadata only.</summary>
    public string PixelSha256 { get; set; } = "";
    /// <summary>SHA256 of the exact image file, present only for a storyboard still.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceSha256 { get; set; }
}
