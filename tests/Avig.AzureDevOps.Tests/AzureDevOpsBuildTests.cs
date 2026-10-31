using Avig.AzureDevOps.Builds;
using Microsoft.Extensions.Logging.Abstractions;

namespace Avig.AzureDevOps.Tests;

/// <summary>
/// Tests for the builds in Azure DevOps.
/// </summary>
/// <remarks>
/// What is easy to get wrong here is reading the result without first reading the status.
/// A build that is running has no result at all, and one that is finished always has one —
/// read only one of them and every other row comes out wrong.
/// </remarks>
public sealed class AzureDevOpsBuildTests
{
    private static readonly AzureDevOpsRemote Remote = new("contoso", "UmeMap och Geodatabas", "UmeMap");

    private static (AzureDevOpsBuildClient Service, FakeAzureDevOps Server) Create()
    {
        FakeAzureDevOps server = new();

        AzureDevOpsBuildClient service = new(
            server.CreateClient(),
            new FakeCredential(),
            NullLogger<AzureDevOpsBuildClient>.Instance);

        return (service, server);
    }

    private static void Respond(FakeAzureDevOps server) => server.Respond(
        "build/builds",
        """
        {"count":2,"value":[
          {"id":900,"buildNumber":"20260917.3","status":"completed","result":"failed",
           "queueTime":"2026-09-17T07:00:00Z","startTime":"2026-09-17T07:01:00Z",
           "finishTime":"2026-09-17T07:06:00Z","sourceBranch":"refs/heads/feature/map",
           "definition":{"id":12,"name":"UmeMap CI"},
           "requestedFor":{"displayName":"Anna Andersson"},
           "repository":{"id":"abc-123","name":"UmeMap"}},
          {"id":901,"buildNumber":"20260917.4","status":"inProgress",
           "queueTime":"2026-09-17T08:00:00Z","startTime":"2026-09-17T08:00:30Z",
           "sourceBranch":"refs/heads/main",
           "definition":{"id":12,"name":"UmeMap CI"},
           "requestedFor":{"displayName":"Björn Bergström"}}
        ]}
        """);

    // ------------------------------------------------------------------ addresses

    [Fact]
    public async Task AddressAsksTheProjectForItsBuilds()
    {
        (AzureDevOpsBuildClient? service, FakeAzureDevOps? server) = Create();
        Respond(server);

        await service.GetAsync(Remote).Ok();

        RecordedRequest request = server.Last("build/builds");

        Assert.Contains("UmeMap%20och%20Geodatabas/_apis/build/builds", request.Uri, StringComparison.Ordinal);
        Assert.Contains("queryOrder=queueTimeDescending", request.Uri, StringComparison.Ordinal);
        Assert.Contains("api-version=7.1", request.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LimitIsSentAsTop()
    {
        (AzureDevOpsBuildClient? service, FakeAzureDevOps? server) = Create();
        Respond(server);

        await service.GetAsync(Remote, limit: 7).Ok();

        Assert.Contains("$top=7", server.Last("build/builds").Uri, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the outcome

    [Theory]
    [InlineData("completed", "succeeded", BuildOutcome.Succeeded)]
    [InlineData("completed", "partiallySucceeded", BuildOutcome.PartiallySucceeded)]
    [InlineData("completed", "failed", BuildOutcome.Failed)]
    [InlineData("completed", "canceled", BuildOutcome.Canceled)]
    [InlineData("inProgress", null, BuildOutcome.Running)]
    [InlineData("notStarted", null, BuildOutcome.Running)]
    public void StatusAndResultAreReadTogether(string status, string? result, BuildOutcome expected)
    {
        Assert.Equal(expected, AzureDevOpsBuildClient.ToOutcome(status, result));
    }

    [Fact]
    public void RunningBuildIsNotJudgedByAResultItDoesNotHave()
    {
        // A build that is running can carry an old result in the response. The status has the final
        // word.
        Assert.Equal(BuildOutcome.Running, AzureDevOpsBuildClient.ToOutcome("inProgress", "failed"));
    }

    // ------------------------------------------------------------------ the content

    [Fact]
    public async Task BranchIsTheShortName()
    {
        (AzureDevOpsBuildClient? service, FakeAzureDevOps? server) = Create();
        Respond(server);

        IReadOnlyList<BuildSummary> builds = await service.GetAsync(Remote).Ok();

        Assert.Equal("feature/map", builds[0].Branch);
    }

    [Fact]
    public async Task DurationIsTheTimeItRan()
    {
        (AzureDevOpsBuildClient? service, FakeAzureDevOps? server) = Create();
        Respond(server);

        IReadOnlyList<BuildSummary> builds = await service.GetAsync(Remote).Ok();

        Assert.Equal(TimeSpan.FromMinutes(5), builds[0].Duration);
    }

    [Fact]
    public async Task RunningBuildHasNoDuration()
    {
        (AzureDevOpsBuildClient? service, FakeAzureDevOps? server) = Create();
        Respond(server);

        IReadOnlyList<BuildSummary> builds = await service.GetAsync(Remote).Ok();

        Assert.Null(builds[1].Duration);
    }

    [Fact]
    public async Task AddressToTheWebPointsAtTheBuild()
    {
        (AzureDevOpsBuildClient? service, FakeAzureDevOps? server) = Create();
        Respond(server);

        IReadOnlyList<BuildSummary> builds = await service.GetAsync(Remote).Ok();

        Assert.Equal(
            "https://dev.azure.com/contoso/UmeMap%20och%20Geodatabas/_build/results?buildId=900",
            builds[0].WebUri.AbsoluteUri);
    }
}
