using System.Diagnostics;
using System.Text.Json;
using TrueMoon;
using TrueMoon.Dependencies;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class PythonWriterFactAttribute : FactAttribute
{
    public PythonWriterFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON") is null)
            Skip = "Requires an explicitly selected Python executable; uses only isolated synthetic project data.";
    }
}

public sealed class BackendWriteProcessTests
{
    [Fact]
    public async Task ConcurrentIpcMoves_WithSameBaseline_CommitOnlyOneAndReturnConflictForTheOther()
    {
        using var fixture = new BackendWriteFixture();
        await using var app = App.Builder(_ => { }).Setup(context => context.UseProjectBackend(fixture.Options, false)).Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await app.StartAsync(deadline.Token);
            var connection = app.Services.Resolve<BackendConnection>()!;
            var opened = await connection.OpenAsync(deadline.Token);
            await connection.AcquireWriterAsync(opened, deadline.Token);
            var migrated = await connection.MigrateAsync(opened, deadline.Token);
            async Task<string> Move(int start)
            {
                try { await connection.MoveAsync("clip-main", start, 12, migrated.Fingerprint, deadline.Token); return "saved"; }
                catch (BackendCommandException error) { return error.Code; }
            }
            var replies = await Task.WhenAll(Move(14), Move(16));
            Assert.Equal(new[] { "revision_conflict", "saved" }, replies.Order());
            var saved = ProjectFileCodec.Read(File.ReadAllBytes(fixture.Files.ProjectPath));
            Assert.Equal(13, saved.Project.Revision);
            Assert.Contains(saved.Project.Montage.Clips[0].StartFrame, new[] { 14, 16 });
            Assert.Equal(2, fixture.Files.HistoryFiles.Length);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Migrate_CliUsesOwnedBackend_AndRepeatedExplicitMigrationDoesNotWriteAgain()
    {
        using var fixture = new BackendWriteFixture();
        var before = File.ReadAllBytes(fixture.Files.ProjectPath);
        var args = new[] { "--migrate-project", fixture.Files.ProjectDirectory, "--config", fixture.Base.ConfigPath };
        var result = await RunAsync(Start(BackendWriteFixture.Executable(), args));
        Assert.True(result.Code == 0, result.Error);
        using var output = JsonDocument.Parse(result.Output);
        Assert.Equal(2, output.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(12, output.RootElement.GetProperty("Project").GetProperty("Revision").GetInt64());
        Assert.Equal(before, File.ReadAllBytes(Assert.Single(fixture.Files.HistoryFiles)));
        var bytes = File.ReadAllBytes(fixture.Files.ProjectPath);
        var repeat = await RunAsync(Start(BackendWriteFixture.Executable(), args));
        Assert.True(repeat.Code == 0, repeat.Error);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Single(fixture.Files.HistoryFiles);
        using (ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Options))) { }
    }

    [Fact]
    public async Task BackendCrash_ReleasesOnlyItsWriter_WithoutAutomaticRestartOrMigration()
    {
        using var fixture = new BackendWriteFixture();
        await using var app = App.Builder(_ => { }).Setup(context => context.UseProjectBackend(fixture.Options, false)).Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await app.StartAsync(deadline.Token);
            var connection = app.Services.Resolve<BackendConnection>()!;
            var opened = await connection.OpenAsync(deadline.Token);
            await connection.AcquireWriterAsync(opened, deadline.Token);
            var owned = app.Services.Resolve<BackendProcessFactory>()!.Owned!;
            Assert.True(owned.IsServing(opened.Identity.ProcessId));
            using var child = Process.GetProcessById(opened.Identity.ProcessId);
            Assert.Equal(Path.GetFullPath(BackendWriteFixture.Executable()), child.MainModule!.FileName, ignoreCase: true);
            var created = child.StartTime;
            Assert.True(owned.IsAlive);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(deadline.Token);
            Assert.False(owned.IsAlive);
            using (ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Options))) { }
            Assert.False(owned.IsAlive); Assert.Equal(created, child.StartTime);
            Assert.Equal(11, ProjectFileCodec.Read(File.ReadAllBytes(fixture.Files.ProjectPath)).Project.Revision);
            Assert.Empty(fixture.Files.HistoryFiles);
        }
        finally { await app.StopAsync(); }
    }

    [PythonWriterFact]
    public async Task PythonAndBackend_ExcludeEachOther_AndSchema2RejectsOldPythonDraftAfterRelease()
    {
        using var fixture = new BackendWriteFixture();
        var cached = Path.Combine(fixture.Files.Root, "cached-schema1.json");
        File.Copy(fixture.Files.ProjectPath, cached);
        var lockPath = Path.Combine(fixture.Files.ProjectDirectory, ".Studio.project.writer.lock");
        using var holder = new Process { StartInfo = Python("from studio.writer_lease import writer_lease\nimport sys\nwith writer_lease(sys.argv[1]):\n print('ready',flush=True)\n sys.stdin.readline()", lockPath) };
        holder.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var holderError = holder.StandardError.ReadToEndAsync(deadline.Token);
        await using var app = App.Builder(_ => { }).Setup(context => context.UseProjectBackend(fixture.Options, false)).Build();
        try
        {
            var ready = await holder.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.True(ready == "ready", ready is null ? await holderError : ready);
            await app.StartAsync(deadline.Token);
            var connection = app.Services.Resolve<BackendConnection>()!;
            var opened = await connection.OpenAsync(deadline.Token);
            var busy = await Assert.ThrowsAsync<BackendCommandException>(() => connection.AcquireWriterAsync(opened, deadline.Token));
            Assert.Equal("writer_busy", busy.Code); Assert.Empty(fixture.Files.HistoryFiles);
            await holder.StandardInput.WriteLineAsync(""); await holder.StandardInput.FlushAsync(deadline.Token);
            await holder.WaitForExitAsync(deadline.Token); Assert.Equal(0, holder.ExitCode); Assert.Empty(await holderError);
            await connection.AcquireWriterAsync(opened, deadline.Token);
            const string bootstrap = """
                import sys,json
                from pathlib import Path
                from studio.model import Store,StudioError
                root=Path(sys.argv[1]); project=root/'project'
                store=Store(root,root/'AMV',root/'python-state')
                store.registry={'projects':[{'id':'project-synthetic','root':str(project)}]}
                cached=json.loads(Path(sys.argv[2]).read_text(encoding='utf-8'))
                def blocked(call):
                    try: call()
                    except StudioError as error:
                        assert error.status==409,error
                        print('blocked')
                    else: raise AssertionError('Python writer must be refused')
                """;
            var before = File.ReadAllBytes(fixture.Files.ProjectPath);
            var denied = await RunAsync(Python(bootstrap + "\nblocked(lambda:store.commit(cached,11,'stale-python','move'))", fixture.Files.Root, cached));
            Assert.True(denied.Code == 0, denied.Error); Assert.Contains("blocked", denied.Output);
            Assert.Equal(before, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Empty(fixture.Files.HistoryFiles);
            var migrated = await connection.MigrateAsync(opened, deadline.Token);
            Assert.Equal(12, migrated.Project.Revision);
            await app.StopAsync();
            var schema2 = File.ReadAllBytes(fixture.Files.ProjectPath);
            var stale = await RunAsync(Python(bootstrap + "\nblocked(lambda:store.get('project-synthetic'))\nblocked(lambda:store.commit(cached,11,'stale-python','move'))", fixture.Files.Root, cached));
            Assert.True(stale.Code == 0, stale.Error); Assert.Equal(2, stale.Output.Split("blocked").Length - 1);
            Assert.Equal(schema2, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Single(fixture.Files.HistoryFiles);
        }
        finally
        {
            await app.StopAsync();
            if (!holder.HasExited) { holder.Kill(entireProcessTree: true); await holder.WaitForExitAsync(); }
        }
    }

    private static ProcessStartInfo Python(string script, params string[] args)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "TrueMoon.Tales.Studio.slnx"))) directory = directory.Parent!;
        script = "import sys\nsys.path.insert(0," + JsonSerializer.Serialize(directory.Parent!.FullName) + ")\n" + script;
        var info = Start(Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON")!, ["-s", "-B", "-c", script, .. args]);
        info.WorkingDirectory = directory.Parent!.FullName;
        info.Environment["PYTHONPATH"] = directory.Parent.FullName;
        return info;
    }

    private static ProcessStartInfo Start(string executable, string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var key in new[] { "-u", "-p", UnitParentProcessEventsHandler.ControlPipeKey, "TRUEMOON_MITHRIL__DISABLED" }) info.Environment.Remove(key);
        return info;
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(ProcessStartInfo info)
    {
        using var process = new Process { StartInfo = info };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        process.Start();
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
