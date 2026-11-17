namespace TrueMoon.Tales.Studio.Backend;

/// <summary>A bounded, baseline-bound playback session message; paths never cross the transport.</summary>
public sealed class BackendPlaybackReply
{
    /// <summary>Empty on success, otherwise a stable refusal code.</summary>
    public string ErrorCode { get; set; } = "";
    /// <summary>The unguessable resource identity, scoped to this backend launch.</summary>
    public string SessionId { get; set; } = "";
    /// <summary>Either audio (48000 Hz stereo f32le) or video (480 by 270 BGRA).</summary>
    public string Kind { get; set; } = "";
    /// <summary>The saved montage clip identity.</summary>
    public string ClipId { get; set; } = "";
    /// <summary>The observed project revision.</summary>
    public long Revision { get; set; }
    /// <summary>The exact-byte project SHA256.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>The montage frame rate.</summary>
    public double Fps { get; set; }
    /// <summary>The selected video's source frame rate.</summary>
    public double SourceFps { get; set; }
    /// <summary>The complete selected clip length in montage frames.</summary>
    public int DurationFrames { get; set; }
    /// <summary>The requested first local video frame; audio starts at zero.</summary>
    public int StartFrame { get; set; }
    /// <summary>The whole clip's stereo sample-frame count on the absolute montage grid.</summary>
    public int TotalSamples { get; set; }
    /// <summary>The local video frame, or minus one when absent.</summary>
    public int LocalFrame { get; set; } = -1;
    /// <summary>The source video frame, or minus one when absent.</summary>
    public int NativeFrame { get; set; } = -1;
    /// <summary>The first audio sample-frame in this chunk, relative to the clip.</summary>
    public int SampleOffset { get; set; }
    /// <summary>True after a clean end or explicit close.</summary>
    public bool Completed { get; set; }
    internal byte[] Data { get; set; } = [];
    /// <summary>Owned binary payload; incoming whole TrueMoon rentals are returned after copying.</summary>
    public Memory<byte> DataBuffer
    {
        get => Data;
        set { try { Data = value.ToArray(); } finally { MemoryPoolUtils.TryReturn(value); } }
    }
    /// <summary>SHA256 of payload bytes; empty on metadata and completion messages.</summary>
    public string DataSha256 { get; set; } = "";
}
