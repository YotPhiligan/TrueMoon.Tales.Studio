using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Persistence;

/// <summary>A loaded file with its complete storage envelope and optimistic concurrency token.</summary>
public sealed class ProjectFileDocument
{
    internal ProjectFileDocument(int schemaVersion, ProjectContract project, JsonElement original,
        string fingerprint, string? filePath = null)
    {
        SchemaVersion = schemaVersion;
        Project = project;
        OriginalDocument = original;
        Fingerprint = fingerprint;
        FilePath = filePath;
    }

    /// <summary>The storage schema, independent of the transport contract version.</summary>
    public int SchemaVersion { get; }
    /// <summary>The normalized project snapshot.</summary>
    public ProjectContract Project { get; }
    /// <summary>The complete owned JSON envelope, including unknown properties.</summary>
    public JsonElement OriginalDocument { get; }
    /// <summary>SHA256 of the exact file bytes, including any BOM and whitespace.</summary>
    public string Fingerprint { get; }
    /// <summary>The actual path assigned by ProjectStore, or null for an in-memory decode.</summary>
    public string? FilePath { get; }

    internal ProjectFileDocument AtPath(string path) => new(SchemaVersion, Project, OriginalDocument, Fingerprint, path);
}
