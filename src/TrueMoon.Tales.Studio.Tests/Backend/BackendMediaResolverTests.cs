using System.Diagnostics;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Backend;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendMediaResolverTests
{
    [Fact]
    public void Resolve_LongestPrefixAndChainedAliases_RelativeAbsoluteAndNamedRoutes_ReadSameUnicodeFile()
    {
        using var files = new Files();
        var expected = files.Write("Клэр/кадр 01.mp4", [17, 29, 43]);
        files.Alias("Old", "@videos/missing");
        files.Alias("Old/Claire", "@videos/Клэр");
        files.Alias("Chain", "Old/Claire");
        files.Alias("@videos/Legacy", "@videos/Клэр");
        var resolver = files.Resolver();
        foreach (var reference in new[] { "Old/Claire/кадр 01.mp4", Path.Combine(files.Legacy, "Old/Claire/кадр 01.mp4"),
                     "Chain/кадр 01.mp4", "@videos/Legacy/кадр 01.mp4", "@videos\\Клэр\\кадр 01.mp4", expected })
            Assert.Equal(expected, resolver.Resolve(reference));
        Assert.Equal(new byte[] { 17, 29, 43 }, File.ReadAllBytes(expected));
        Assert.False(Directory.Exists(files.Legacy));
    }

    [Fact]
    public void Resolve_PrefixBoundaries_DoNotGrantSiblingNamespaceOrDisabledLegacy()
    {
        using var files = new Files();
        files.Alias("Old", "@videos");
        var sibling = Path.Combine(files.Root, "media-sibling", "frame.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!); File.WriteAllText(sibling, "preserve");
        var resolver = files.Resolver();
        foreach (var reference in new[] { "Older/frame.mp4", "Unmapped/frame.mp4", "@videos2/frame.mp4", sibling })
            Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve(reference));
        Assert.False(Directory.Exists(files.Legacy));
        Assert.Equal("preserve", File.ReadAllText(sibling));
    }

    [Theory]
    [InlineData("@videos/../secret.mp4")]
    [InlineData("Old/../frame.mp4")]
    [InlineData("@videos//frame.mp4")]
    [InlineData("@videos/C:/frame.mp4")]
    [InlineData("@videos/./../frame.mp4")]
    [InlineData("C:frame.mp4")]
    [InlineData("http://127.0.0.1/frame.mp4")]
    [InlineData("file:///C:/frame.mp4")]
    [InlineData("//server/share/frame.mp4")]
    [InlineData("\\\\server\\share\\frame.mp4")]
    [InlineData("\\\\?\\C:\\frame.mp4")]
    [InlineData("@videos/frame.mp4:stream")]
    public void Resolve_InvalidReferences_AreDeniedBeforeFileAccess(string reference)
    {
        using var files = new Files();
        var resolver = files.Resolver();
        Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve(reference));
        Assert.False(Directory.Exists(files.Legacy));
        Assert.Empty(Directory.GetFiles(files.Media, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Resolve_MissingFileAndDirectory_AreDistinctFromPolicyDenial_WithoutCreatingAnything()
    {
        using var files = new Files();
        var resolver = files.Resolver();
        Assert.Throws<FileNotFoundException>(() => resolver.Resolve("@videos/missing.mp4"));
        Assert.Throws<FileNotFoundException>(() => resolver.Resolve("@videos/missing/parent/frame.mp4"));
        Assert.Throws<FileNotFoundException>(() => resolver.Resolve("@videos"));
        Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve(Path.Combine(files.Root, "outside.mp4")));
        Assert.Empty(Directory.GetFileSystemEntries(files.Media));
        Assert.False(Directory.Exists(files.Legacy));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Constructor_ProtectedAliasEndpointCannotGrantRead_EvenWhenUnderAnAllowedRoot(bool protectedSource)
    {
        using var files = new Files();
        var marker = files.Write("private/preserve.mp4", [3, 7, 11]);
        files.Config["protected_roots"] = new JsonArray(Path.GetDirectoryName(marker));
        files.Alias(protectedSource ? "@videos/private" : "Old", protectedSource ? "@videos/public" : "@videos/private");
        Assert.Throws<UnauthorizedAccessException>(() => files.Resolver());
        Assert.Equal(new byte[] { 3, 7, 11 }, File.ReadAllBytes(marker));
        Assert.False(Directory.Exists(files.Legacy));
    }

    [Fact]
    public void Resolve_ExplicitProtectedAndAutomaticFrameworkRoots_AreDeniedBeforeMissingFileProbe()
    {
        using var files = new Files();
        var source = new DirectoryInfo(Environment.CurrentDirectory);
        while (!File.Exists(Path.Combine(source.FullName, "TrueMoon.Tales.Studio.slnx"))) source = source.Parent!;
        var framework = Path.GetFullPath(Path.Combine(source.FullName, "..", "..", "TrueMoon"));
        files.Config["mounts"]!["framework"] = framework;
        var resolver = files.Resolver();
        var denied = Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve("@framework/a-missing-media-file.mp4"));
        Assert.Contains("Protected", denied.Message);
    }

    [Fact]
    public void Resolve_TrustedConfigRelativeRootsAndReservedProjects_PreserveLegacyRelativeMeaning()
    {
        using var files = new Files();
        files.Config["projects_root"] = "projects";
        files.Config["amv_root"] = "media";
        files.Config["legacy_root"] = "disabled-legacy";
        files.Config["mounts"] = new JsonObject { ["videos"] = "media", ["arts"] = "arts" };
        var expected = Path.Combine(files.Project, "local.mp4"); File.WriteAllText(expected, "local");
        var resolver = files.Resolver();
        Assert.Equal(expected, resolver.Resolve("@projects/project/local.mp4"));
        Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve("local.mp4"));

        files.Config.Remove("legacy_root");
        Assert.Equal(expected, files.Resolver().Resolve("local.mp4"));
        // Explicitly trusted roots may be relative to a nested config directory and use '..'.
        files.Config["projects_root"] = "../projects";
        files.Config["amv_root"] = "../media";
        files.Config["mounts"] = new JsonObject { ["videos"] = "../media" };
        var nested = Path.Combine(files.Root, "configuration", "Studio.json");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, files.Config.ToJsonString());
        Assert.Equal(expected, new BackendMediaResolver(BackendOptions.Create(files.Project, nested)).Resolve("@projects/project/local.mp4"));
        var outside = new BackendMediaResolver(BackendOptions.Create(Path.Combine(files.Root, "untrusted"), nested));
        Assert.Throws<UnauthorizedAccessException>(() => outside.Resolve("local.mp4"));
    }

    [Fact]
    public void Resolve_DisabledLegacyCannotBeAReadRoot_ButExplicitEnabledLegacyIsReadable()
    {
        using var files = new Files();
        files.Config["mounts"]!["old"] = files.Legacy;
        Assert.Throws<InvalidDataException>(() => files.Resolver());
        files.Config["mounts"]!.AsObject().Remove("old");
        files.Config["legacy_read_enabled"] = true;
        Directory.CreateDirectory(files.Legacy);
        var old = Path.Combine(files.Legacy, "old.mp4"); File.WriteAllText(old, "history");
        Assert.Equal(old, files.Resolver().Resolve("old.mp4"));
        Assert.Equal("history", File.ReadAllText(old));
    }

    [Fact]
    public void Resolve_AliasCycleAndDepthBoundaries_FailWithoutTouchingLegacy()
    {
        using var cycle = new Files();
        cycle.Alias("A", "B"); cycle.Alias("B", "A");
        Assert.Throws<InvalidDataException>(() => cycle.Resolver().Resolve("A/frame.mp4"));
        Assert.False(Directory.Exists(cycle.Legacy));
        using var files = new Files();
        var expected = files.Write("frame.mp4", [8]);
        for (var i = 0; i < 15; i++) files.Alias("L" + i, "L" + (i + 1));
        files.Alias("L15", "@videos");
        Assert.Equal(expected, files.Resolver().Resolve("L0/frame.mp4"));
        files.Alias("L-1", "L0");
        Assert.Throws<InvalidDataException>(() => files.Resolver().Resolve("L-1/frame.mp4"));
        Assert.False(Directory.Exists(files.Legacy));
    }

    [Fact]
    public void Constructor_RejectsAmbiguousAliasesAndReservedMounts()
    {
        using var files = new Files();
        files.Alias("Old", "Old");
        Assert.Throws<InvalidDataException>(() => files.Resolver());
        files.Config["path_aliases"] = new JsonArray();
        files.Alias("Old", "@videos"); files.Alias("Old", "@arts");
        Assert.Throws<InvalidDataException>(() => files.Resolver());
        files.Config["path_aliases"] = new JsonArray();
        files.Config["mounts"]!["projects"] = files.Media;
        Assert.Throws<InvalidDataException>(() => files.Resolver());
    }

    [Fact]
    public void Resolve_JunctionIsDeniedBeforeFollowingTarget_WhileDisabledLegacyAliasesStayLexical()
    {
        using var files = new Files();
        var target = Path.Combine(files.Root, "outside"); Directory.CreateDirectory(target);
        var marker = Path.Combine(target, "preserve.mp4"); File.WriteAllText(marker, "untouched");
        var actual = files.Write("mapped.mp4", [41]);
        var linked = Path.Combine(files.Media, "linked");
        CreateDirectoryLink(files.Legacy, target);
        CreateDirectoryLink(linked, target);
        try
        {
            files.Alias("Old", "@videos");
            var resolver = files.Resolver();
            Assert.Equal(actual, resolver.Resolve("Old/mapped.mp4"));
            var legacyDenied = Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve("preserve.mp4"));
            Assert.Contains("Legacy", legacyDenied.Message);
            var linkedDenied = Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve("@videos/linked/preserve.mp4"));
            Assert.Contains("Linked", linkedDenied.Message);
            Assert.Equal("untouched", File.ReadAllText(marker));
            Assert.Equal(new byte[] { 41 }, File.ReadAllBytes(actual));
        }
        finally { Directory.Delete(linked); Directory.Delete(files.Legacy); }
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\""
        };
        using var process = Process.Start(start)!;
        try
        {
            Assert.True(process.WaitForExit(5000), "Junction creation deadline expired.");
            Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
    }

    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "studio-media-policy-" + Guid.NewGuid().ToString("N"));
        internal string Media => Path.Combine(Root, "media");
        internal string Project => Path.Combine(Root, "projects", "project");
        internal string Legacy => Path.Combine(Root, "disabled-legacy");
        internal JsonObject Config { get; }
        internal Files()
        {
            Directory.CreateDirectory(Media); Directory.CreateDirectory(Project); Directory.CreateDirectory(Path.Combine(Root, "arts"));
            Config = new JsonObject
            {
                ["version"] = 1, ["legacy_read_enabled"] = false, ["legacy_root"] = Legacy,
                ["projects_root"] = Path.Combine(Root, "projects"), ["amv_root"] = Media,
                ["mounts"] = new JsonObject { ["videos"] = Media, ["arts"] = Path.Combine(Root, "arts") },
                ["path_aliases"] = new JsonArray(), ["protected_roots"] = new JsonArray()
            };
        }
        internal void Alias(string from, string to) => Config["path_aliases"]!.AsArray().Add(new JsonObject { ["from"] = from, ["to"] = to });
        internal string Write(string relative, byte[] bytes)
        {
            var path = Path.GetFullPath(Path.Combine(Media, relative)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes); return path;
        }
        internal BackendMediaResolver Resolver()
        {
            var config = Path.Combine(Root, "Studio.json"); File.WriteAllText(config, Config.ToJsonString());
            return new(BackendOptions.Create(Project, config));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
