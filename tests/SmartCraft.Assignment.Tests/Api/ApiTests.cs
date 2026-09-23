using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using SmartCraft.Assignment.Api.Authorization;
using SmartCraft.Assignment.Api.Infrastructure;

namespace SmartCraft.Assignment.Tests.Api;

/// <summary>
/// HTTP-level checks of docs/03 "HTTP behavior" and docs/04 "Authentication/Authorization/
/// Idempotency": the real app (JWT, policies, exception mapping) over a per-test SQLite file
/// with the startup seed (Alice/Bob/Carol on Project Phoenix).
/// </summary>
public sealed class ApiTests : IDisposable
{
    private static readonly Guid Alice = SeedData.WorkerAId;
    private static readonly Guid Bob = SeedData.WorkerBId;
    private static readonly Guid Carol = SeedData.ApproverId;
    private static readonly Guid Project = SeedData.ProjectId;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"smartcraft-api-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Development")
            .UseSetting("ConnectionStrings:Default", $"Data Source={_dbPath};Foreign Keys=True"));
    }

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private async Task<HttpClient> ClientAsync(Guid workerId, params string[] permissions)
    {
        var client = _factory.CreateClient();
        var tokenResponse = await client.PostAsJsonAsync("/dev/token", new { workerId, permissions });
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> CreateWorklogAsync(HttpClient worker, decimal hours = 8m)
    {
        var response = await worker.PostAsJsonAsync("/api/worklogs", new { projectId = Project, workDate = "2026-09-23", hours, description = "work" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    private static Task<HttpResponseMessage> TransitionAsync(HttpClient client, JsonElement worklog, string action) =>
        client.PostAsJsonAsync($"/api/worklogs/{worklog.GetProperty("id")}/{action}", new { expectedVersion = worklog.GetProperty("version").GetInt64() });

    private static HttpRequestMessage InvoiceRequest(string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{Project}/invoices");
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [Fact]
    public async Task Request_without_token_is_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_without_the_required_permission_is_403()
    {
        var alice = await ClientAsync(Alice, Permissions.WorklogCreate);

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.SendAsync(InvoiceRequest("key-1"))).StatusCode);
    }

    [Fact]
    public async Task Worklog_to_invoice_flow_with_idempotent_retry()
    {
        var alice = await ClientAsync(Alice, Permissions.WorklogCreate, Permissions.WorklogSubmitOwn);
        var carol = await ClientAsync(Carol, Permissions.WorklogApprove, Permissions.InvoiceCreate, Permissions.InvoiceRead);

        var created = await CreateWorklogAsync(alice);
        Assert.Equal(Alice, created.GetProperty("workerId").GetGuid());
        var submitted = await TransitionAsync(alice, created, "submit");
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var approved = await TransitionAsync(carol, await JsonAsync(submitted), "approve");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await carol.SendAsync(InvoiceRequest(null))).StatusCode);

        var first = await carol.SendAsync(InvoiceRequest("invoice-run-1"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var invoice = await JsonAsync(first);
        Assert.Equal(800m, invoice.GetProperty("total").GetDecimal()); // 8h x Alice's seeded 100 rate

        var retry = await carol.SendAsync(InvoiceRequest("invoice-run-1"));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(invoice.GetProperty("id").GetGuid(), (await JsonAsync(retry)).GetProperty("id").GetGuid());

        var fetched = await carol.GetAsync($"/api/invoices/{invoice.GetProperty("id")}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    [Fact]
    public async Task Workers_cannot_act_on_others_worklogs_or_approve_their_own()
    {
        var alice = await ClientAsync(Alice, Permissions.WorklogCreate, Permissions.WorklogSubmitOwn, Permissions.WorklogApprove);
        var bob = await ClientAsync(Bob, Permissions.WorklogUpdateOwn, Permissions.WorklogSubmitOwn);
        var created = await CreateWorklogAsync(alice);

        var bobUpdate = await bob.PutAsJsonAsync($"/api/worklogs/{created.GetProperty("id")}",
            new { expectedVersion = created.GetProperty("version").GetInt64(), workDate = "2026-09-23", hours = 1m, description = "hijack" });
        Assert.Equal(HttpStatusCode.Forbidden, bobUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TransitionAsync(bob, created, "submit")).StatusCode);

        var submitted = await TransitionAsync(alice, created, "submit");
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TransitionAsync(alice, await JsonAsync(submitted), "approve")).StatusCode);
    }

    [Fact]
    public async Task Stale_expected_version_is_409_problem_details()
    {
        var alice = await ClientAsync(Alice, Permissions.WorklogCreate, Permissions.WorklogUpdateOwn);
        var created = await CreateWorklogAsync(alice);
        var url = $"/api/worklogs/{created.GetProperty("id")}";
        var staleVersion = created.GetProperty("version").GetInt64();

        var firstEdit = await alice.PutAsJsonAsync(url, new { expectedVersion = staleVersion, workDate = "2026-09-23", hours = 6m, description = "first" });
        Assert.Equal(HttpStatusCode.OK, firstEdit.StatusCode);
        var secondEdit = await alice.PutAsJsonAsync(url, new { expectedVersion = staleVersion, workDate = "2026-09-23", hours = 7m, description = "lost update" });

        Assert.Equal(HttpStatusCode.Conflict, secondEdit.StatusCode);
        Assert.Equal("application/problem+json", secondEdit.Content.Headers.ContentType?.MediaType);
    }
}
