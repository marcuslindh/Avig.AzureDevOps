using System.Text.Json;
using Avig.AzureDevOps.Builds;
using Avig.AzureDevOps.PullRequests;
using Microsoft.Extensions.Logging.Abstractions;

namespace Avig.AzureDevOps.Tests;

/// <summary>
/// The statuses are enums on the package's side and camelCase names on the API's. What must
/// hold is the translation both ways, and that a value the API adds later falls back instead
/// of breaking a list.
/// </summary>
public sealed class StatusTests
{
    private static readonly AzureDevOpsRemote Remote = new("contoso", "Webshop", "webshop-api");

    private const string PullRequest = """
        {"pullRequestId":42,"title":"T","status":"completed","mergeStatus":"rejectedByPolicy",
         "creationDate":"2026-10-01T10:00:00Z","sourceRefName":"refs/heads/feature/x",
         "targetRefName":"refs/heads/main","reviewers":[]}
        """;

    [Theory]
    [InlineData(PullRequestStatusFilter.Active, "active")]
    [InlineData(PullRequestStatusFilter.Completed, "completed")]
    [InlineData(PullRequestStatusFilter.Abandoned, "abandoned")]
    [InlineData(PullRequestStatusFilter.All, "all")]
    public async Task TheFilterIsSentByTheApisName(PullRequestStatusFilter filter, string expected)
    {
        (AzureDevOpsPullRequestClient service, FakeAzureDevOps server) = PullRequests();
        server.Respond("pullrequests", """{"count":0,"value":[]}""");

        await service.ListAsync(Remote, new PullRequestQuery(filter)).Ok();

        Assert.Contains($"searchCriteria.status={expected}&", server.Last("pullrequests").Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAndMergeStatusAreRead()
    {
        (AzureDevOpsPullRequestClient service, FakeAzureDevOps server) = PullRequests();
        server.Respond("pullrequests/42", PullRequest);

        PullRequestOverview overview = await service.GetOverviewAsync(Remote, 42).Ok();

        Assert.Equal(PullRequestStatus.Completed, overview.Status);
        Assert.Equal(PullRequestMergeStatus.RejectedByPolicy, overview.MergeStatus);
        Assert.False(overview.IsActive);
    }

    [Fact]
    public async Task AValueTheApiAddsLaterFallsBack()
    {
        (AzureDevOpsPullRequestClient service, FakeAzureDevOps server) = PullRequests();
        server.Respond(
            "pullrequests/42",
            PullRequest.Replace("\"completed\"", "\"somethingNew\"", StringComparison.Ordinal)
                .Replace("\"rejectedByPolicy\"", "\"7\"", StringComparison.Ordinal));

        PullRequestOverview overview = await service.GetOverviewAsync(Remote, 42).Ok();

        Assert.Equal(PullRequestStatus.Active, overview.Status);
        Assert.Equal(PullRequestMergeStatus.NotSet, overview.MergeStatus);
    }

    [Fact]
    public async Task AnUpdateSendsTheStatusByTheApisName()
    {
        (AzureDevOpsPullRequestClient service, FakeAzureDevOps server) = PullRequests();
        server.Respond("pullrequests/42", PullRequest);

        await service.UpdateAsync(Remote, 42, new PullRequestUpdate(Status: PullRequestStatus.Abandoned)).Ok();

        JsonElement body = JsonSerializer.Deserialize<JsonElement>(server.Last("pullrequests/42").Body ?? "{}");

        Assert.Equal("abandoned", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnUpdateWithoutStatusLeavesItAlone()
    {
        (AzureDevOpsPullRequestClient service, FakeAzureDevOps server) = PullRequests();
        server.Respond("pullrequests/42", PullRequest);

        await service.UpdateAsync(Remote, 42, new PullRequestUpdate(IsDraft: false)).Ok();

        JsonElement body = JsonSerializer.Deserialize<JsonElement>(server.Last("pullrequests/42").Body ?? "{}");

        Assert.False(body.TryGetProperty("status", out _));
    }

    [Fact]
    public async Task ChangeTypesAreFlags()
    {
        (AzureDevOpsPullRequestClient service, FakeAzureDevOps server) = PullRequests();
        server.Respond("iterations?", """{"count":1,"value":[{"id":1}]}""");
        server.Respond("iterations/1/changes", """
            {"changeEntries":[
                { "changeTrackingId": 1, "changeType": "edit, rename", "item": { "path": "/a.cs" } },
                { "changeTrackingId": 2, "changeType": "delete", "item": { "path": "/b.cs" } },
                { "changeTrackingId": 3, "changeType": "somethingNew", "item": { "path": "/c.cs" } }
            ]}
            """);

        PullRequestDetails details = await service.GetDetailsAsync(
            Remote,
            new PullRequestSummary(42, "T", "A", "b", "main", DateTime.UtcNow, new Uri("https://x/"))).Ok();

        Assert.Equal(PullRequestChangeType.Edit | PullRequestChangeType.Rename, details.Files[0].ChangeType);
        Assert.Equal(PullRequestChangeType.Delete, details.Files[1].ChangeType);

        // A change the package does not know is still a change.
        Assert.Equal(PullRequestChangeType.Edit, details.Files[2].ChangeType);
    }

    [Fact]
    public async Task ThreadStatusesAreRead()
    {
        (AzureDevOpsPullRequestClient service, FakeAzureDevOps server) = PullRequests();
        server.Respond("threads", """
            {"count":3,"value":[
                { "id": 1, "status": "byDesign", "comments": [ { "id": 1, "content": "a", "commentType": "text" } ] },
                { "id": 2, "status": "wontFix", "comments": [ { "id": 1, "content": "b", "commentType": "text" } ] },
                { "id": 3, "comments": [ { "id": 1, "content": "c", "commentType": "text" } ] }
            ]}
            """);

        IReadOnlyList<PullRequestThread> threads = await service.GetThreadsAsync(Remote, 42, 1).Ok();

        Assert.Equal(
            [PullRequestThreadStatus.ByDesign, PullRequestThreadStatus.WontFix, PullRequestThreadStatus.Unknown],
            threads.Select(thread => thread.Status));
    }

    [Fact]
    public async Task ByDesignIsSentAsTheApiSpellsIt()
    {
        (AzureDevOpsPullRequestClient service, FakeAzureDevOps server) = PullRequests();
        server.Respond("threads/501", """{"id":501,"status":"byDesign"}""");

        await service.SetThreadStatusAsync(Remote, 42, 501, PullRequestThreadStatus.ByDesign).Ok();

        JsonElement body = JsonSerializer.Deserialize<JsonElement>(server.Last("threads/501").Body ?? "{}");

        Assert.Equal("byDesign", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnknownCannotBeSet()
    {
        (AzureDevOpsPullRequestClient service, _) = PullRequests();

        (string error, ResultStatus status) =
            await service.SetThreadStatusAsync(Remote, 42, 501, PullRequestThreadStatus.Unknown).Failed();

        Assert.Equal(ResultStatus.Invalid, status);
        Assert.Contains("unknown status", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildStatusesAreRead()
    {
        FakeAzureDevOps server = new();
        AzureDevOpsBuildClient service = new(server.CreateClient(), new FakeCredential(), NullLogger<AzureDevOpsBuildClient>.Instance);
        server.Respond("build/builds", """
            {"count":3,"value":[
              {"id":1,"status":"inProgress","queueTime":"2026-10-01T10:00:00Z"},
              {"id":2,"status":"completed","result":"succeeded","queueTime":"2026-10-01T09:00:00Z"},
              {"id":3,"status":"somethingNew","queueTime":"2026-10-01T08:00:00Z"}
            ]}
            """);

        IReadOnlyList<BuildSummary> builds = await service.GetAsync(Remote).Ok();

        Assert.Equal(
            [BuildStatus.InProgress, BuildStatus.Completed, BuildStatus.Unknown],
            builds.Select(build => build.Status));
    }

    private static (AzureDevOpsPullRequestClient Service, FakeAzureDevOps Server) PullRequests()
    {
        FakeAzureDevOps server = new();

        AzureDevOpsPullRequestClient service = new(
            server.CreateClient(),
            new FakeCredential(),
            NullLogger<AzureDevOpsPullRequestClient>.Instance);

        return (service, server);
    }
}
