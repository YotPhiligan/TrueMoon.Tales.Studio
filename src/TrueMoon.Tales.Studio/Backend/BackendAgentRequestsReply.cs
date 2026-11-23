namespace TrueMoon.Tales.Studio.Backend;

/// <summary>Bounded UTF-8 data for a project brief or an agent request.</summary>
public sealed class BackendAgentRequestsReply
{
    /// <summary>Empty on success, otherwise a stable refusal code.</summary>
    public string ErrorCode { get; set; } = "";

    internal byte[] Json { get; set; } = [];

    /// <summary>Owned JSON payload bytes, empty on refusal.</summary>
    public Memory<byte> JsonBuffer
    {
        get => Json;
        set { try { Json = value.ToArray(); } finally { MemoryPoolUtils.TryReturn(value); } }
    }
}
