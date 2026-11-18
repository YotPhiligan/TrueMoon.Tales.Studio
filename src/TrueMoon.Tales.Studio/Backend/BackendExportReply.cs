namespace TrueMoon.Tales.Studio.Backend;

/// <summary>A bounded export status or verified artifact range. The transport contains no paths.</summary>
public sealed class BackendExportReply
{
    /// <summary>Empty on success, otherwise a stable export or project refusal code.</summary>
    public string ErrorCode { get; set; } = "";

    /// <summary>Versioned job status JSON, empty when the call was refused.</summary>
    public string JobJson { get; set; } = "";

    internal byte[] Data { get; set; } = [];

    /// <summary>Owned MP4 range bytes, empty for status-only replies.</summary>
    public Memory<byte> DataBuffer
    {
        get => Data;
        set { try { Data = value.ToArray(); } finally { MemoryPoolUtils.TryReturn(value); } }
    }
}
