using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TrueMoon.Tales.Studio.Backend.Http;

// The parent owns the HTTP listener; all project I/O stays in the same owned Enerit backend as the UI.
internal sealed partial class BackendHttpServer(BackendConnection backend, int port = 0) : IAsyncDisposable
{
    internal const string ProjectRoute = "/api/backend/v1/project";
    internal const string MoveRoute = "/api/backend/v1/move-clip";
    internal const string TrimRoute = "/api/backend/v1/trim-clip";
    internal const string MediaRoute = "/api/backend/v1/media-info";
    internal const string FrameRoute = "/api/backend/v1/frame";
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private WebApplication? _app;
    private bool _started, _disposed;
    internal Uri Address { get; private set; } = null!;
    internal string Token => _token;
    internal object Ready { get; private set; } = null!;

    internal async Task StartAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("HTTP server already started.");
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _started = true;
        var opened = await backend.OpenAsync(token).ConfigureAwait(false);
        if (opened.SchemaVersion != 2) throw new InvalidDataException("HTTP editing requires explicit --migrate-project first.");
        opened = await backend.AcquireWriterAsync(opened, token).ConfigureAwait(false);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ApplicationName = typeof(BackendHttpServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production"
        });
        // Environment/appsettings cannot add listeners or override the explicit loopback binding.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 4096;
            options.Limits.MaxConcurrentConnections = 8;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
            options.Limits.MaxRequestHeadersTotalSize = 8192;
            options.Limits.MaxRequestLineSize = 2048;
            options.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1);
        });
        _app = builder.Build();
        _app.Run(HandleAsync);
        await _app.StartAsync(token).ConfigureAwait(false);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        Address = new(addresses.Addresses.Single().TrimEnd('/') + "/");
        Ready = new { api_version = 1, url = Address.AbsoluteUri, token = _token, project_id = opened.Project.Id,
            owner_pid = Environment.ProcessId, backend_pid = opened.Identity.ProcessId, product_version = StudioBackendService.ProductVersion };
    }

    private async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Connection = "close";
        if (context.Request.Host.Host != "127.0.0.1" || (context.Request.Host.Port ?? 80) != context.Connection.LocalPort ||
            context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
            context.Request.Headers.ContainsKey("Origin") || context.Request.Headers["Sec-Fetch-Site"] is { Count: > 0 })
        { await Error(context, 403, "forbidden"); return; }
        var supplied = context.Request.Headers["X-Studio-Token"];
        if (supplied.Count != 1 || supplied[0] is not { } key || key.Length != _token.Length ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(_token)))
        { await Error(context, 403, "forbidden"); return; }
        if (!await _slots.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
        { await Error(context, 429, "busy"); return; }
        var path = context.Request.Path.Value;
        var agentRequests = IsAgentRequestsRoute(path);
        var reconcile = path != AgentContextRoute && !(path == BriefRoute && HttpMethods.IsGet(context.Request.Method)) &&
            (!agentRequests || HttpMethods.IsPost(context.Request.Method));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(IsTransferRoute(path) ? 48 : 12));
            if (context.Request.QueryString.HasValue) { await Error(context, 400, "invalid_request"); return; }
            if (path == ProjectRoute)
            {
                if (!HttpMethods.IsGet(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
                var opened = await backend.OpenAsync(deadline.Token).ConfigureAwait(false);
                await context.Response.WriteAsJsonAsync(BackendHttpSnapshot.From(opened), Json, deadline.Token).ConfigureAwait(false);
            }
            else if (path == AgentContextRoute)
                await HandleAgentContextAsync(context, deadline.Token).ConfigureAwait(false);
            else if (agentRequests)
                await HandleAgentRequestsAsync(context, path!, deadline.Token).ConfigureAwait(false);
            else if (path is ScenarioActionRoute or ScenarioSegmentRoute or ScenarioVideoRoute)
                await HandleScenarioActionAsync(context, deadline.Token).ConfigureAwait(false);
            else if (path == BriefRoute)
                await HandleBriefAsync(context, deadline.Token).ConfigureAwait(false);
            else if (path == MoveRoute)
            {
                if (!HttpMethods.IsPost(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
                if (!context.Request.HasJsonContentType()) { await Error(context, 415, "json_required"); return; }
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: deadline.Token).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject()
                    .GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
                { await Error(context, 400, "invalid_request"); return; }
                var request = document.RootElement.Deserialize<BackendHttpMove>(Json);
                if (request is null || !request.Valid) { await Error(context, 400, "invalid_request"); return; }
                // No claim, read-modify-write, migration, or retry here: the existing owner sends one Move RPC.
                var saved = await backend.MoveAsync(request.ClipId, request.StartFrame, request.ExpectedRevision, request.Fingerprint, deadline.Token).ConfigureAwait(false);
                await context.Response.WriteAsJsonAsync(BackendHttpSnapshot.From(saved), Json, deadline.Token).ConfigureAwait(false);
            }
            else if (path == TrimRoute)
            {
                if (!HttpMethods.IsPost(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
                if (!context.Request.HasJsonContentType()) { await Error(context, 415, "json_required"); return; }
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: deadline.Token).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject()
                    .GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
                { await Error(context, 400, "invalid_request"); return; }
                var request = document.RootElement.Deserialize<BackendHttpTrim>(Json);
                if (request is null || !request.Valid) { await Error(context, 400, "invalid_request"); return; }
                // The existing writer applies one absolute trim. The adapter neither reads a replacement baseline nor retries.
                var saved = await backend.TrimAsync(request.ClipId, request.SourceInFrame, request.SourceOutFrame, request.StartFrame,
                    request.ExpectedRevision, request.Fingerprint, deadline.Token).ConfigureAwait(false);
                await context.Response.WriteAsJsonAsync(BackendHttpSnapshot.From(saved), Json, deadline.Token).ConfigureAwait(false);
            }
            else if (path == MediaRoute || path == FrameRoute)
            {
                if (!HttpMethods.IsPost(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
                if (!context.Request.HasJsonContentType()) { await Error(context, 415, "json_required"); return; }
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: deadline.Token).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject()
                    .GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
                { await Error(context, 400, "invalid_request"); return; }
                BackendHttpMediaQuery? request = path == FrameRoute ? document.RootElement.Deserialize<BackendHttpFrameQuery>(Json)
                    : document.RootElement.Deserialize<BackendHttpMediaQuery>(Json);
                if (request is null || !request.Valid) { await Error(context, 400, "invalid_request"); return; }
                if (request is BackendHttpFrameQuery { Frame: < 0 }) { await Error(context, 422, "invalid_frame"); return; }
                var result = request is BackendHttpFrameQuery frame
                    ? await backend.FrameAsync(frame.AssetId, frame.Frame, frame.ExpectedRevision, frame.Fingerprint, deadline.Token).ConfigureAwait(false)
                    : await backend.MediaInfoAsync(request.AssetId, request.ExpectedRevision, request.Fingerprint, deadline.Token).ConfigureAwait(false);
                await context.Response.WriteAsJsonAsync(new BackendHttpMediaSnapshot(1, 2, result), Json, deadline.Token).ConfigureAwait(false);
            }
            else if (path is ExportStartRoute or ExportStatusRoute or ExportCancelRoute or ExportRecoverRoute or ExportChunkRoute)
                await HandleExportAsync(context, path, deadline.Token).ConfigureAwait(false);
            else if (IsTransferRoute(path))
                await HandleTransferAsync(context, path!, deadline.Token).ConfigureAwait(false);
            else await Error(context, 404, "not_found");
        }
        catch (BackendCommandException error)
        {
            var status = error.Code switch
            {
                "revision_conflict" or "project_file_conflict" or "writer_busy" or "writer_required" or "migration_required" or "recovery_required" or
                    "recovery_conflict" or "export_conflict" or "export_not_ready" or "invalid_export_artifact" or
                    "transfer_key_conflict" or "transfer_input_changed" or "transfer_damaged" or "transfer_stage_conflict" or
                    "transfer_stage_path_changed" or "transfer_already_launched" or "transfer_not_staged" or
                    "transfer_run_incomplete" or "transfer_take_removed" or "take_pair_changed" or "agent_context_block_ambiguous" or
                    "agent_request_key_conflict" or "agent_request_insertion_conflict" or "agent_request_revision_conflict" or
                    "agent_request_owner_conflict" or "agent_request_state_conflict" or "agent_breakdown_not_ready" or "agent_breakdown_already_applied" or
                    "scenario_action_key_conflict" or "brief_key_conflict" => 409,
                "invalid_move" or "invalid_trim" or "invalid_frame" or "invalid_media" or "invalid_export" or
                    "invalid_transfer" or "transfer_invalid_profile" or "transfer_timing" or "transfer_prompt_sound" or "invalid_agent_context" or
                    "invalid_agent_request_id" or "invalid_agent_request_record" or "invalid_agent_request" or "invalid_agent_request_scope" or
                    "invalid_agent_request_update" or "invalid_agent_breakdown" or "agent_breakdown_blocked" or "invalid_scenario_edit" or
                    "scenario_order_timing" or "scenario_window_conflict" or "invalid_brief" or "invalid_brief_patch" => 422,
                "access_denied" or "write_not_enabled" => 403,
                "project_not_found" or "media_not_found" or "export_not_found" or "transfer_missing" or "agent_context_block_missing" or
                    "agent_request_not_found" or "agent_request_scope_missing" => 404,
                "media_busy" or "export_busy" or "transfer_run_busy" => 429, "media_timeout" or "export_timeout" or "transfer_timeout" => 504,
                "project_too_large" or "agent_context_too_large" or "agent_requests_too_large" or "agent_request_input_too_large" or
                    "brief_input_too_large" => 413, _ => 503
            };
            await Error(context, status, error.Code, reconcile);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error) { await Error(context, error.StatusCode, "invalid_request"); }
        catch (JsonException) { await Error(context, 400, "invalid_request"); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { await Error(context, 504, "deadline_exceeded", reconcile); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        { await Error(context, 503, "backend_unavailable", reconcile); }
        finally { _slots.Release(); }
    }

    private static async Task Error(HttpContext context, int status, string code, bool reconcile = false)
    {
        if (context.RequestAborted.IsCancellationRequested || context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new BackendHttpFailure(code, "Операция не повторяется автоматически.", reconcile), Json, context.RequestAborted).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_app is not { } app) return;
        try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await app.StopAsync(deadline.Token).ConfigureAwait(false); }
        finally { await app.DisposeAsync().ConfigureAwait(false); }
    }
}
