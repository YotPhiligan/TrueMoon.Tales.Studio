namespace TrueMoon.Tales.Studio.Backend;

/// <summary>A read-only excerpt with the exact version fingerprint of its Markdown source.</summary>
public sealed class BackendTextReply
{
    /// <summary>Empty on success, otherwise a stable refusal code.</summary>
    public string ErrorCode { get; set; } = "";
    /// <summary>Bounded chapter fragment JSON, empty on refusal.</summary>
    public string FragmentJson { get; set; } = "";
}
