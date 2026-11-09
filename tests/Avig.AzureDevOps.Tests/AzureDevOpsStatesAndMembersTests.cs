using System.Net;
using Avig.AzureDevOps.WorkItems;
using Microsoft.Extensions.Logging.Abstractions;

namespace Avig.AzureDevOps.Tests;

/// <summary>
/// Tests for the states of a work item type and the members of the project's default team.
/// </summary>
/// <remarks>
/// Both feed choices in a user interface: the states a work item can be moved to and the
/// people it can be assigned to. The order and the filtering are what matter.
/// </remarks>
public sealed class AzureDevOpsStatesAndMembersTests
{
    private static readonly AzureDevOpsRemote Remote = new("contoso", "UmeMap och Geodatabas", "UmeMap");

    /// <summary>A client against a fake server.</summary>
    /// <returns>The client and the server.</returns>
    private static (AzureDevOpsWorkItemClient Service, FakeAzureDevOps Server) Create()
    {
        FakeAzureDevOps server = new();

        AzureDevOpsWorkItemClient service = new(
            server.CreateClient(),
            new FakeCredential(),
            NullLogger<AzureDevOpsWorkItemClient>.Instance);

        return (service, server);
    }

    [Fact]
    public async Task StatesKeepTheServersOrderAndCategories()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond(
            "/states",
            """
            {"count":5,"value":[
              {"name":"New","color":"b2b2b2","category":"Proposed"},
              {"name":"Active","color":"007acc","category":"InProgress"},
              {"name":"Resolved","color":"ff9d00","category":"Resolved"},
              {"name":"Closed","color":"339933","category":"Completed"},
              {"name":"Parked","category":"SomethingNew"}
            ]}
            """);

        IReadOnlyList<WorkItemState> states = await service.GetStatesAsync(Remote, "User Story").Ok();

        Assert.Equal(["New", "Active", "Resolved", "Closed", "Parked"], states.Select(state => state.Name));
        Assert.Equal(WorkItemStateCategory.InProgress, states[1].Category);
        Assert.Equal(WorkItemStateCategory.Completed, states[3].Category);
        Assert.Equal(WorkItemStateCategory.Unknown, states[4].Category);
        Assert.Equal("007acc", states[1].Color);
        Assert.Equal(string.Empty, states[4].Color);
        Assert.Contains("/workitemtypes/User%20Story/states", server.Last("/states").Uri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankTypeIsInvalidAndSendsNothing(string type)
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();

        (string _, ResultStatus status) = await service.GetStatesAsync(Remote, type).Failed();

        Assert.Equal(ResultStatus.Invalid, status);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task TeamMembersComeFromTheDefaultTeamSortedWithoutGroups()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond(
            "/members",
            """
            {"count":3,"value":[
              {"identity":{"id":"b","displayName":"Ylva Berg","uniqueName":"ylva@example.com",
                "imageUrl":"https://dev.azure.com/contoso/_api/_common/identityImage?id=b",
                "_links":{"avatar":{"href":"https://dev.azure.com/contoso/_apis/GraphProfile/MemberAvatars/aad.b"}}}},
              {"identity":{"id":"g","displayName":"[UmeMap]\\Contributors","isContainer":true}},
              {"identity":{"id":"a","displayName":"anna Ek","uniqueName":"anna@example.com",
                "imageUrl":"https://dev.azure.com/contoso/_api/_common/identityImage?id=a"}}
            ]}
            """);
        server.Respond(
            "Geodatabas?api-version",
            """{"id":"p1","name":"UmeMap och Geodatabas","defaultTeam":{"id":"team-1","name":"UmeMap Team"}}""");

        IReadOnlyList<AzureDevOpsIdentity> members = await service.GetTeamMembersAsync(Remote).Ok();

        Assert.Equal(["anna Ek", "Ylva Berg"], members.Select(member => member.Name));
        Assert.Equal("anna@example.com", members[0].UniqueName);
        Assert.Equal(
            new Uri("https://dev.azure.com/contoso/_api/_common/identityImage?id=a"),
            members[0].ImageUrl);
        Assert.Equal(
            new Uri("https://dev.azure.com/contoso/_apis/GraphProfile/MemberAvatars/aad.b"),
            members[1].ImageUrl);
        Assert.Contains(
            "/_apis/projects/UmeMap%20och%20Geodatabas/teams/team-1/members?api-version=7.1",
            server.Last("/members").Uri,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingProjectIsPassedOnWithoutAskingForMembers()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("Geodatabas?api-version", """{"message":"The project does not exist."}""", HttpStatusCode.NotFound);

        (string _, ResultStatus status) = await service.GetTeamMembersAsync(Remote).Failed();

        Assert.Equal(ResultStatus.NotFound, status);
        Assert.DoesNotContain(server.Requests, request => request.Uri.Contains("/members", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectWithoutDefaultTeamIsNotFound()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps _) = Create();

        (string _, ResultStatus status) = await service.GetTeamMembersAsync(Remote).Failed();

        Assert.Equal(ResultStatus.NotFound, status);
    }
}
