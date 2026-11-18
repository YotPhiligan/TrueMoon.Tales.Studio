using System.Net;
using System.Net.Http.Json;
using System.Text;
using TrueMoon.Tales.Studio.Backend.Http;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendExportHttpContractTests
{
    [Fact]
    public async Task ExportRoutes_RejectInvalidAuthorityAndRequestShapes_WithoutChangingProject()
    {
        await using var fixture = new BackendHttpFixture();
        await fixture.StartAsync();
        using var client = fixture.Client();
        using var unauthorized = fixture.Client(authenticated: false);

        var projectPath = fixture.Files.Files.ProjectPath;
        var original = File.ReadAllBytes(projectPath);
        var id = "export-" + new string('a', 32);
        var baseline = (await fixture.Connection.OpenAsync()).Fingerprint;
        var lookup = $"{{\"id\":\"{id}\"}}";
        var start = $"{{\"client_key\":\"backend7-http-fixture-key\",\"profile\":\"draft\",\"expected_revision\":12,\"fingerprint\":\"{baseline}\"}}";
        var cases = new (string Route, string Body, HttpMethod Method, string MediaType, bool Authorized, HttpStatusCode Status, string Code)[]
        {
            (BackendHttpServer.ExportStartRoute, start, HttpMethod.Post, "application/json", false, HttpStatusCode.Forbidden, "forbidden"),
            (BackendHttpServer.ExportStatusRoute, lookup, HttpMethod.Get, "application/json", true, HttpStatusCode.MethodNotAllowed, "method_not_allowed"),
            (BackendHttpServer.ExportStartRoute, start, HttpMethod.Post, "text/plain", true, HttpStatusCode.UnsupportedMediaType, "json_required"),
            (BackendHttpServer.ExportStartRoute + "?project=other", start, HttpMethod.Post, "application/json", true, HttpStatusCode.BadRequest, "invalid_request"),
            (BackendHttpServer.ExportStartRoute, start.Insert(1, "\"profile\":\"final\","), HttpMethod.Post, "application/json", true, HttpStatusCode.BadRequest, "invalid_request"),
            (BackendHttpServer.ExportStartRoute, start.Insert(1, "\"exports_root\":\"C:/other\","), HttpMethod.Post, "application/json", true, HttpStatusCode.BadRequest, "invalid_request"),
            (BackendHttpServer.ExportStartRoute, start.Replace("12,", "true,", StringComparison.Ordinal), HttpMethod.Post, "application/json", true, HttpStatusCode.BadRequest, "invalid_request"),
            (BackendHttpServer.ExportStatusRoute, "{\"id\":\"../outside\"}", HttpMethod.Post, "application/json", true, HttpStatusCode.BadRequest, "invalid_request"),
            (BackendHttpServer.ExportCancelRoute, lookup, HttpMethod.Post, "application/json", true, HttpStatusCode.BadRequest, "invalid_request"),
            (BackendHttpServer.ExportRecoverRoute, "[]", HttpMethod.Post, "application/json", true, HttpStatusCode.BadRequest, "invalid_request"),
            (BackendHttpServer.ExportChunkRoute, lookup.Insert(1, $"\"artifact_sha256\":\"{baseline}\",\"offset\":-1,"), HttpMethod.Post, "application/json", true, HttpStatusCode.BadRequest, "invalid_request")
        };

        foreach (var (route, body, method, mediaType, authorized, status, code) in cases)
        {
            using var request = new HttpRequestMessage(method, route)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            };
            using var response = await (authorized ? client : unauthorized).SendAsync(request);
            Assert.Equal(status, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var failure = await response.Content.ReadFromJsonAsync<BackendHttpFailure>(BackendHttpServer.Json);
            Assert.Equal(code, failure?.Error);
        }

        Assert.Equal(original, File.ReadAllBytes(projectPath));
        Assert.Single(fixture.Files.Files.HistoryFiles);
        Assert.False(File.Exists(fixture.Files.Files.JournalPath));
    }
}
