using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Persistence;

public sealed class ProjectFileCodecTests
{
    [Fact]
    public void Read_LegacyBytes_KeepsExactFingerprintAndDetachedOriginal()
    {
        var bytes = PersistenceFixture.LegacyBytes();
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes));

        var document = ProjectFileCodec.Read(bytes);
        Array.Fill<byte>(bytes, 0);

        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal(11, document.Project.Revision);
        Assert.Equal(expectedHash, document.Fingerprint);
        Assert.Null(document.FilePath);
        Assert.Equal("project-synthetic", document.OriginalDocument.GetProperty("id").GetString());
        Assert.Equal(9007199254740993, document.OriginalDocument.GetProperty("opaque_root").GetProperty("precise_integer").GetInt64());
    }

    [Fact]
    public void SerializeMigration_KeepsLegacyCapsuleAndIdsWithoutAdvancingRevision()
    {
        var legacy = ProjectFileCodec.Read(PersistenceFixture.LegacyBytes());

        var migrated = ProjectFileCodec.Read(ProjectFileCodec.SerializeMigration(legacy, legacy.Project));

        Assert.Equal(2, migrated.SchemaVersion);
        Assert.Equal(11, migrated.Project.Revision);
        Assert.Equal(["source-a", "source-b"], migrated.Project.Sources.Select(s => s.Id));
        Assert.Equal(["clip-main", "clip-extra"], migrated.Project.Montage.Clips.Select(c => c.Id));
        Assert.Equal("clip-main", migrated.Project.Montage.Clips[0].ActionId);
        Assert.Equal(12, migrated.Project.Montage.Clips[0].StartFrame);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(legacy.OriginalDocument.GetRawText()),
            JsonNode.Parse(migrated.OriginalDocument.GetProperty("legacy_document").GetRawText())));
        Assert.Equal("@videos/native.safetensors", migrated.Project.Sources[0].Takes[0].Properties["native_av"].GetString());
        Assert.Null(migrated.FilePath);
    }

    [Fact]
    public void Serialize_RequiresExplicitMigrationAndPreservesProjectIdentity()
    {
        var legacy = ProjectFileCodec.Read(PersistenceFixture.LegacyBytes());
        Assert.Throws<ProjectMigrationRequiredException>(() => ProjectFileCodec.Serialize(legacy, legacy.Project));
        Assert.Throws<InvalidOperationException>(() => ProjectFileCodec.SerializeMigration(legacy, legacy.Project with { Id = "other" }));
        var migrated = ProjectFileCodec.Read(ProjectFileCodec.SerializeMigration(legacy, legacy.Project));
        Assert.Throws<InvalidOperationException>(() => ProjectFileCodec.SerializeMigration(migrated, migrated.Project));
        Assert.Throws<InvalidOperationException>(() => ProjectFileCodec.Serialize(migrated, migrated.Project with { Id = "other" }));
    }

    [Fact]
    public void Serialize_ReorderedEntities_PreservesUnknownFieldsByStableIdentity()
    {
        var legacy = ProjectFileCodec.Read(PersistenceFixture.LegacyBytes());
        var annotated = ProjectFileCodec.Read(PersistenceFixture.Annotate(ProjectFileCodec.SerializeMigration(legacy, legacy.Project)));

        var saved = ProjectFileCodec.Read(ProjectFileCodec.Serialize(annotated, PersistenceFixture.Expanded(annotated.Project)));

        var root = saved.OriginalDocument;
        Assert.Equal("envelope", root.GetProperty("opaque_envelope").GetString());
        var project = root.GetProperty("project");
        Assert.Equal("project", project.GetProperty("opaque_project").GetString());
        Assert.Equal("Сборка A–B–A", saved.Project.Name);
        Assert.Equal(["source-b", "source-a"], saved.Project.Sources.Select(s => s.Id));
        var source = PersistenceFixture.ById(project.GetProperty("sources"), "source-a");
        Assert.Equal("source-a", source.GetProperty("opaque_source").GetString());
        Assert.Equal(["take-a-alt", "take-a"], saved.Project.Sources.Single(s => s.Id == "source-a").Takes.Select(t => t.Id));
        Assert.Equal("take-a", PersistenceFixture.ById(source.GetProperty("takes"), "take-a").GetProperty("opaque_take").GetString());
        var segment = PersistenceFixture.ById(project.GetProperty("segments"), "segment-main");
        Assert.Equal("segment-main", segment.GetProperty("opaque_segment").GetString());
        var action = PersistenceFixture.ById(segment.GetProperty("actions"), "clip-main");
        Assert.Equal("clip-main", action.GetProperty("opaque_action").GetString());
        Assert.Equal("source-a", PersistenceFixture.ById(action.GetProperty("sources"), "source-a", "source_id").GetProperty("opaque_binding").GetString());
        Assert.False(PersistenceFixture.ById(segment.GetProperty("actions"), "new-text-action").TryGetProperty("opaque_action", out _));
        Assert.False(PersistenceFixture.ById(action.GetProperty("sources"), "source-b", "source_id").TryGetProperty("opaque_binding", out _));
        var montage = project.GetProperty("montage");
        Assert.Equal("segment-main", PersistenceFixture.ById(montage.GetProperty("windows"), "segment-main").GetProperty("opaque_window").GetString());
        Assert.Equal("clip-main", PersistenceFixture.ById(montage.GetProperty("clips"), "clip-main").GetProperty("opaque_clip").GetString());
        Assert.False(PersistenceFixture.ById(montage.GetProperty("clips"), "clip-reaction").TryGetProperty("opaque_clip", out _));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(annotated.OriginalDocument.GetProperty("legacy_document").GetRawText()),
            JsonNode.Parse(root.GetProperty("legacy_document").GetRawText())));
        Assert.Equal(["clip-extra", "clip-main-tail", "clip-reaction", "clip-main"], saved.Project.Montage.Clips.Select(c => c.Id));
        Assert.Equal(["source-a", "source-b", "source-a"], saved.Project.Montage.Clips.Where(c => c.ActionId == "clip-main").OrderBy(c => c.StartFrame).Select(c => c.SourceId));
    }

    [Fact]
    public void Serialize_MetadataOmissionRetainsValuesAndExplicitNullClears()
    {
        var legacy = ProjectFileCodec.Read(PersistenceFixture.LegacyBytes());
        var migrated = ProjectFileCodec.Read(ProjectFileCodec.SerializeMigration(legacy, legacy.Project));
        var source = migrated.Project.Sources[0] with
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["prompt"] = JsonSerializer.SerializeToElement<string?>(null),
                ["settings"] = JsonSerializer.SerializeToElement(new { profile = "new" })
            }
        };
        var updated = migrated.Project with { Sources = [source, migrated.Project.Sources[1]] };

        var saved = ProjectFileCodec.Read(ProjectFileCodec.Serialize(migrated, updated));

        Assert.Equal(JsonValueKind.Null, saved.Project.Sources[0].Properties["prompt"].ValueKind);
        Assert.Equal("new", saved.Project.Sources[0].Properties["settings"].GetProperty("profile").GetString());
        Assert.Equal("18446744073709551615", saved.Project.Sources[0].Properties["settings"].GetProperty("seed").GetString());
        Assert.Equal("Оригинальный промпт", saved.OriginalDocument.GetProperty("legacy_document").GetProperty("blocks")[0].GetProperty("prompt").GetString());
    }

    [Theory]
    [InlineData("{\"schema_version\":3}")]
    [InlineData("{\"schema_version\":2,\"project\":{\"contract_version\":2}}")]
    public void Read_UnsupportedStorageAndContractVersions_AreRejected(string json)
    {
        Assert.Throws<NotSupportedException>(() => ProjectFileCodec.Read(json));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"schema_version\":true}")]
    [InlineData("{\"schema_version\":2}")]
    public void Read_MalformedStorageEnvelope_IsRejected(string json)
    {
        Assert.Throws<JsonException>(() => ProjectFileCodec.Read(json));
    }
}
