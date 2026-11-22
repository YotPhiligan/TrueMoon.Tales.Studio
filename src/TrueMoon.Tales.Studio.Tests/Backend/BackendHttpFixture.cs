using TrueMoon;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;

namespace TrueMoon.Tales.Studio.Tests.Backend;

internal sealed class BackendHttpFixture : IAsyncDisposable
{
    private readonly bool _ownsFiles;
    internal BackendWriteFixture Files { get; }
    internal IApp App { get; }
    internal BackendHttpServer Http { get; }
    internal BackendConnection Connection => App.Services.Resolve<BackendConnection>()!;
    internal BackendHttpFixture(int port = 0) : this(new BackendWriteFixture(), port, ownsFiles: true) { }

    internal BackendHttpFixture(BackendWriteFixture files) : this(files, 0, ownsFiles: false) { }

    private BackendHttpFixture(BackendWriteFixture files, int port, bool ownsFiles)
    {
        Files = files;
        _ownsFiles = ownsFiles;
        App = TrueMoon.App.Builder(_ => { }).Setup(context => context.UseProjectBackend(Files.Options, false).UseBackendScene()).Build();
        Http = new(Connection, port);
    }
    internal async Task StartAsync(bool migrate = true)
    {
        if (migrate)
        {
            var store = Files.Files.Store();
            await store.MigrateAsync(await store.OpenAsync(), 11, "http-fixture");
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await App.StartAsync(deadline.Token);
        await Http.StartAsync(deadline.Token);
    }
    internal HttpClient Client(bool authenticated = true)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = Http.Address, Timeout = TimeSpan.FromSeconds(15) };
        if (authenticated) client.DefaultRequestHeaders.Add("X-Studio-Token", Http.Token);
        return client;
    }
    public async ValueTask DisposeAsync()
    {
        try { await Http.DisposeAsync(); }
        finally
        {
            try { await App.StopAsync(); }
            finally { try { await App.DisposeAsync(); } finally { if (_ownsFiles) Files.Dispose(); } }
        }
    }
}
