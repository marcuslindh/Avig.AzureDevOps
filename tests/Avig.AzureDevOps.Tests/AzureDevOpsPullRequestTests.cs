using System.Text.Json;
using Avig.AzureDevOps.PullRequests;
using Microsoft.Extensions.Logging.Abstractions;

namespace Avig.AzureDevOps.Tests;

/// <summary>
/// Tests for the HTTP exchange with Azure DevOps.
/// </summary>
/// <remarks>
/// What can go wrong here is the requests, not logic. Three details in the API are easy to
/// miss and hard to debug after the fact: <c>changeTrackingId</c> must be included,
/// otherwise the comment ends up on the overview tab instead of on the line; the path must
/// have a leading slash; and the column counts from one even though the documentation for
/// version 7.1 claims zero. All three are pinned down below.
/// </remarks>
public sealed class AzureDevOpsPullRequestTests
{
    private static readonly AzureDevOpsRemote Remote = new("contoso", "UmeMap och Geodatabas", "UmeMap");

    private static (AzureDevOpsPullRequestClient Service, FakeAzureDevOps Server) Create()
    {
        FakeAzureDevOps server = new();

        AzureDevOpsPullRequestClient service = new(
            server.CreateClient(),
            new FakeCredential(),
            NullLogger<AzureDevOpsPullRequestClient>.Instance);

        return (service, server);
    }

    private static JsonElement Json(string? body) =>
        JsonSerializer.Deserialize<JsonElement>(body ?? "{}");

    // ------------------------------------------------------------------ addresses

    [Fact]
    public async Task AddressPointsAtTheRightProjectAndApiVersion()
    {
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("pullrequests", """{"count":0,"value":[]}""");

        await service.GetOpenAsync(Remote).Ok();

        RecordedRequest request = server.Last("pullrequests");

        // The project name has spaces and must be encoded in the address.
        Assert.Contains("UmeMap%20och%20Geodatabas/_apis/git/repositories/UmeMap", request.Uri, StringComparison.Ordinal);
        Assert.Contains("api-version=7.1", request.Uri, StringComparison.Ordinal);
        Assert.Equal("Basic testtoken", request.Authorization);
    }

    [Fact]
    public async Task LookupForTheBranchesPullRequestUsesTheFullReference()
    {
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("pullrequests", """{"count":0,"value":[]}""");

        await service.FindForBranchAsync(Remote, "feature/pagination");

        RecordedRequest request = server.Last("pullrequests");

        // The API wants refs/heads/…, not the short branch name.
        Assert.Contains(
            "searchCriteria.sourceRefName=refs%2Fheads%2Ffeature%2Fpagination",
            request.Uri,
            StringComparison.Ordinal);

        Assert.Contains("searchCriteria.status=active", request.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BranchNameThatIsAlreadyAReferenceIsNotDoubled()
    {
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("pullrequests", """{"count":0,"value":[]}""");

        await service.FindForBranchAsync(Remote, "refs/heads/main");

        Assert.Contains(
            "sourceRefName=refs%2Fheads%2Fmain&",
            server.Last("pullrequests").Uri,
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ reading

    [Fact]
    public async Task PullRequestIsReadWithShortBranchName()
    {
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();

        server.Respond("pullrequests", """
            {"count":1,"value":[{
                "pullRequestId": 42,
                "title": "Pagination in the user list",
                "createdBy": { "displayName": "Marcus" },
                "sourceRefName": "refs/heads/feature/pagination",
                "targetRefName": "refs/heads/main",
                "creationDate": "2026-08-01T09:00:00Z",
                "status": "active"
            }]}
            """);

        Result<PullRequestSummary> found = await service.FindForBranchAsync(Remote, "feature/pagination");

        Assert.True(found.IsSuccess(out PullRequestSummary? pullRequest));
        Assert.Equal(42, pullRequest.Id);
        Assert.Equal("Pagination in the user list", pullRequest.Title);
        Assert.Equal("Marcus", pullRequest.Author);
        Assert.Equal("feature/pagination", pullRequest.SourceBranch);
        Assert.Equal("main", pullRequest.TargetBranch);
    }

    [Fact]
    public async Task IterationIsTheLatestAndFilesCarryTheirTrackingId()
    {
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();

        server.Respond("iterations?", """{"count":3,"value":[{"id":1},{"id":2},{"id":3}]}""");

        server.Respond("iterations/3/changes", """
            {"changeEntries":[
                { "changeTrackingId": 7, "changeType": "edit", "item": { "path": "/src/Order.cs" } },
                { "changeTrackingId": 9, "changeType": "add",  "item": { "path": "/src/New.cs" } }
            ]}
            """);

        PullRequestDetails details = await service.GetDetailsAsync(
            Remote,
            new PullRequestSummary(42, "T", "A", "b", "main", DateTime.UtcNow, new Uri("https://x/"))).Ok();

        // Every push adds an iteration; the comments should hang on the latest.
        Assert.Equal(3, details.Iteration);
        Assert.Equal(2, details.Files.Count);
        Assert.Equal(7, details.Files[0].ChangeTrackingId);
        Assert.Equal("/src/Order.cs", details.Files[0].Path);
    }

    [Fact]
    public async Task SystemMessagesDoNotCountAsReview()
    {
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();

        server.Respond("threads", """
            {"count":3,"value":[
                { "id": 1, "status": "active", "isDeleted": false,
                  "threadContext": { "filePath": "/src/Order.cs", "rightFileStart": { "line": 12, "offset": 1 } },
                  "comments": [ { "id": 1, "author": { "displayName": "Marcus" },
                                  "content": "Extract this", "commentType": "text",
                                  "publishedDate": "2026-08-01T10:00:00Z", "isDeleted": false } ] },
                { "id": 2, "status": "closed", "isDeleted": false,
                  "comments": [ { "id": 1, "content": "The merge succeeded",
                                  "commentType": "system", "isDeleted": false } ] },
                { "id": 3, "status": "active", "isDeleted": true,
                  "comments": [ { "id": 1, "content": "deleted", "commentType": "text", "isDeleted": true } ] }
            ]}
            """);

        IReadOnlyList<PullRequestThread> threads = await service.GetThreadsAsync(Remote, pullRequestId: 42, iteration: 3).Ok();

        // Only the real comment remains: the system message and the deleted
        // thread do not belong in a review.
        PullRequestThread thread = Assert.Single(threads);

        Assert.Equal(1, thread.Id);
        Assert.Equal("/src/Order.cs", thread.FilePath);
        Assert.Equal(12, thread.Line);
        Assert.Equal("Extract this", thread.Comments[0].Text);
    }

    [Fact]
    public async Task ThreadsAreFetchedForTheIterationBeingShown()
    {
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("threads", """{"count":0,"value":[]}""");

        await service.GetThreadsAsync(Remote, pullRequestId: 42, iteration: 3).Ok();

        // Without the iteration the server does not recompute the line numbers for the diff being viewed.
        Assert.Contains("$iteration=3", server.Last("threads").Uri, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ writing

    [Fact]
    public async Task LineCommentCarriesEverythingNeededToAnchor()
    {
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();

        server.Respond("threads", """
            {"id":501,"status":"active","isDeleted":false,
             "threadContext":{"filePath":"/src/Order.cs","rightFileStart":{"line":12,"offset":1}},
             "comments":[{"id":1,"content":"Extract this","commentType":"text","isDeleted":false}]}
            """);

        await service.CreateThreadAsync(
            Remote,
            pullRequestId: 42,
            iteration: 3,
            "Extract this",
            new PullRequestAnchor("src/Order.cs", StartLine: 12, EndLine: 14, ChangeTrackingId: 7)).Ok();

        JsonElement body = Json(server.Last("threads").Body);
        JsonElement context = body.GetProperty("threadContext");

        // The path must have a leading slash.
        Assert.Equal("/src/Order.cs", context.GetProperty("filePath").GetString());

        // The right side is the proposed code.
        Assert.Equal(12, context.GetProperty("rightFileStart").GetProperty("line").GetInt32());
        Assert.Equal(14, context.GetProperty("rightFileEnd").GetProperty("line").GetInt32());

        // The column counts from one, despite what the documentation for 7.1 claims.
        Assert.Equal(1, context.GetProperty("rightFileStart").GetProperty("offset").GetInt32());

        // Without the tracking id the comment ends up on the overview tab instead of on the line.
        JsonElement pullRequestContext = body.GetProperty("pullRequestThreadContext");

        Assert.Equal(7, pullRequestContext.GetProperty("changeTrackingId").GetInt32());
        Assert.Equal(
            3,
            pullRequestContext.GetProperty("iterationContext").GetProperty("secondComparingIteration").GetInt32());

        Assert.Equal("text", body.GetProperty("comments")[0].GetProperty("commentType").GetString());
        Assert.Equal("active", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CommentWithoutLineBecomesGeneralComment()
    {
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();

        server.Respond("threads", """
            {"id":502,"status":"active","isDeleted":false,
             "comments":[{"id":1,"content":"About the whole","commentType":"text","isDeleted":false}]}
            """);

        await service.CreateThreadAsync(Remote, 42, 3, "About the whole", anchor: null).Ok();

        JsonElement body = Json(server.Last("threads").Body);

        Assert.False(body.TryGetProperty("threadContext", out JsonElement context) && context.ValueKind is JsonValueKind.Object);
        Assert.False(body.TryGetProperty("pullRequestThreadContext", out JsonElement prContext)
                     && prContext.ValueKind is JsonValueKind.Object);
    }

    [Fact]
    public async Task ReplyHangsUnderTheFirstComment()
    {
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();

        server.Respond("threads/501/comments", """
            {"id":2,"content":"Fixed","commentType":"text","publishedDate":"2026-08-02T08:00:00Z"}
            """);

        PullRequestComment comment = await service.ReplyAsync(Remote, 42, 501, "Fixed").Ok();

        JsonElement body = Json(server.Last("threads/501/comments").Body);

        Assert.Equal(1, body.GetProperty("parentCommentId").GetInt32());
        Assert.Equal("Fixed", body.GetProperty("content").GetString());
        Assert.Equal(2, comment.Id);
    }

    [Fact]
    public async Task ResolvingAThreadSendsTheStatusTheApiCallsFixed()
    {
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("threads/501", """{"id":501,"status":"fixed"}""");

        await service.SetThreadStatusAsync(Remote, 42, 501, PullRequestThreadStatus.Fixed).Ok();

        RecordedRequest request = server.Last("threads/501");

        Assert.Equal("PATCH", request.Method);

        // The web interface calls it "Resolved", the API "fixed".
        Assert.Equal("fixed", Json(request.Body).GetProperty("status").GetString());
    }

    // ------------------------------------------------------------------ errors

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, "sign-in", ResultStatus.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "permissions", ResultStatus.Forbidden)]
    [InlineData(System.Net.HttpStatusCode.NotFound, "Could not find", ResultStatus.NotFound)]
    [InlineData(System.Net.HttpStatusCode.Conflict, "responded 409: ", ResultStatus.Conflict)]
    [InlineData(System.Net.HttpStatusCode.BadRequest, "responded 400: ", ResultStatus.Invalid)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, "responded 500: ", ResultStatus.Unavailable)]
    public async Task RejectedCallIsExplainedToTheUser(
        System.Net.HttpStatusCode status,
        string expected,
        ResultStatus expectedStatus)
    {
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("pullrequests", """{"message":"nope"}""", status);

        (string error, ResultStatus actual) = await service.GetOpenAsync(Remote).Failed();

        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedStatus, actual);
    }

    [Fact]
    public async Task ANetworkErrorIsAResultAndNotAnException()
    {
        AzureDevOpsPullRequestClient service = new(
            new HttpClient(new FailingHandler(new HttpRequestException("No such host is known."))),
            new FakeCredential(),
            NullLogger<AzureDevOpsPullRequestClient>.Instance);

        (string error, ResultStatus status) = await service.GetOpenAsync(Remote).Failed();

        Assert.Equal(ResultStatus.Unavailable, status);
        Assert.Contains("Could not reach Azure DevOps", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeoutIsAResultAndNotAnException()
    {
        AzureDevOpsPullRequestClient service = new(
            new HttpClient(new FailingHandler(new TaskCanceledException("timeout", new TimeoutException()))),
            new FakeCredential(),
            NullLogger<AzureDevOpsPullRequestClient>.Instance);

        (string error, ResultStatus status) = await service.GetOpenAsync(Remote).Failed();

        Assert.Equal(ResultStatus.Unavailable, status);
        Assert.Contains("did not respond in time", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCallersOwnCancellationStillCancels()
    {
        // Cancelling is not a failure: whoever cancelled must be able to tell the two apart.
        (AzureDevOpsPullRequestClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("pullrequests", """{"count":0,"value":[]}""");

        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetOpenAsync(Remote, cancellation.Token));
    }

    [Fact]
    public async Task AMissingArgumentIsInvalid()
    {
        (AzureDevOpsPullRequestClient? service, _) = Create();

        (_, ResultStatus status) = await service.GetOpenAsync(null!).Failed();

        Assert.Equal(ResultStatus.Invalid, status);
    }

    [Fact]
    public async Task WithoutASignInNothingIsSent()
    {
        FakeAzureDevOps server = new();
        AzureDevOpsPullRequestClient service = new(
            server.CreateClient(),
            new PersonalAccessTokenCredential(() => null),
            NullLogger<AzureDevOpsPullRequestClient>.Instance);

        (string error, ResultStatus status) = await service.GetOpenAsync(Remote).Failed();

        Assert.Equal(ResultStatus.Unauthorized, status);
        Assert.Contains("access token", error, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    /// <summary>A transport that fails the way the network does.</summary>
    private sealed class FailingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(failure);
    }
}
