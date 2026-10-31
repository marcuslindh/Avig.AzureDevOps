using System.Text.Json;
using Avig.AzureDevOps.WorkItems;
using Microsoft.Extensions.Logging.Abstractions;

namespace Avig.AzureDevOps.Tests;

/// <summary>
/// Tests for work items in Azure DevOps.
/// </summary>
/// <remarks>
/// The same stance as for the pull requests: what can go wrong is the requests, not
/// logic. Four details in the API are easy to miss and hard to debug after the fact —
/// WIQL returns only numbers, the fields must be fetched in a second round, person fields
/// are objects and not text, and a new work item is accepted as json-patch with the type
/// in the address. All four are pinned down below.
/// </remarks>
public sealed class AzureDevOpsWorkItemTests
{
    private static readonly AzureDevOpsRemote Remote = new("contoso", "UmeMap och Geodatabas", "UmeMap");

    private static (AzureDevOpsWorkItemClient Service, FakeAzureDevOps Server) Create()
    {
        FakeAzureDevOps server = new();

        AzureDevOpsWorkItemClient service = new(
            server.CreateClient(),
            new FakeCredential(),
            NullLogger<AzureDevOpsWorkItemClient>.Instance);

        return (service, server);
    }

    private static JsonElement Json(string? body) => JsonSerializer.Deserialize<JsonElement>(body ?? "{}");

    /// <summary>A response with two numbers and the fields that belong to them.</summary>
    /// <param name="server">The server that is to answer.</param>
    private static void RespondWithTwo(FakeAzureDevOps server)
    {
        server.Respond("wiql", """{"workItems":[{"id":41},{"id":42}]}""");
        server.Respond(
            "workitemsbatch",
            """
            {"count":2,"value":[
              {"id":41,"fields":{
                "System.WorkItemType":"Bug",
                "System.Title":"The map is redrawn",
                "System.State":"Active",
                "System.AssignedTo":{"displayName":"Anna Andersson","uniqueName":"anna@contoso.se"},
                "System.IterationPath":"UmeMap\\Sprint 12",
                "System.AreaPath":"UmeMap\\Map",
                "System.Tags":"performance; map",
                "System.Parent":7,
                "System.ChangedDate":"2026-09-16T10:30:00Z"}},
              {"id":42,"fields":{
                "System.WorkItemType":"User Story",
                "System.Title":"Save the search",
                "System.State":"New",
                "System.IterationPath":"UmeMap\\Sprint 13",
                "System.AreaPath":"UmeMap",
                "System.ChangedDate":"2026-09-15T09:00:00Z"}}
            ]}
            """);
    }

    // ------------------------------------------------------------------ addresses

    [Fact]
    public async Task QueryGoesToTheProjectAndNotTheRepository()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        RespondWithTwo(server);

        await service.GetAsync(Remote, new WorkItemQuery()).Ok();

        RecordedRequest request = server.Last("wiql");

        // Work items belong to the project. An address under git/repositories would be the wrong way.
        Assert.Contains("UmeMap%20och%20Geodatabas/_apis/wit/wiql", request.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("git/repositories", request.Uri, StringComparison.Ordinal);
        Assert.Contains("api-version=7.1", request.Uri, StringComparison.Ordinal);
        Assert.Equal("Basic testtoken", request.Authorization);
    }

    [Fact]
    public async Task LimitIsSentAsTopOnTheQuery()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        RespondWithTwo(server);

        await service.GetAsync(Remote, new WorkItemQuery(Limit: 25)).Ok();

        Assert.Contains("$top=25", server.Last("wiql").Uri, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the query

    [Fact]
    public void OpenQueryLeavesOutTheFinishedStates()
    {
        string wiql = AzureDevOpsWorkItemClient.BuildWiql(includeClosed: false);

        Assert.Contains("[System.State] NOT IN ('Closed', 'Removed', 'Done')", wiql, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryWithClosedAsksAboutNoState()
    {
        string wiql = AzureDevOpsWorkItemClient.BuildWiql(includeClosed: true);

        Assert.DoesNotContain("System.State", wiql, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryLeavesOutTestArtefacts()
    {
        string wiql = AzureDevOpsWorkItemClient.BuildWiql(includeClosed: false);

        // Exclusion and not a list of what to include: an organization can have
        // its own types, and they must show.
        Assert.Contains("NOT IN ('Test Case'", wiql, StringComparison.Ordinal);
        Assert.DoesNotContain("[System.WorkItemType] IN (", wiql, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryUsesTheProjectMacroAndNoText()
    {
        // The project name is never put together with the query. The server fills it in from the address.
        Assert.Contains("[System.TeamProject] = @project", AzureDevOpsWorkItemClient.BuildWiql(false), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the fields

    [Fact]
    public async Task FieldsAreFetchedInASecondCallWithTheNumbersFromTheQuery()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        RespondWithTwo(server);

        await service.GetAsync(Remote, new WorkItemQuery()).Ok();

        JsonElement body = Json(server.Last("workitemsbatch").Body);

        Assert.Equal([41, 42], body.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32()));
        Assert.Contains(
            "System.AssignedTo",
            body.GetProperty("fields").EnumerateArray().Select(field => field.GetString()),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task PersonFieldIsReadAsTheDisplayName()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        RespondWithTwo(server);

        IReadOnlyList<WorkItemSummary> items = await service.GetAsync(Remote, new WorkItemQuery()).Ok();

        Assert.Equal("Anna Andersson", items[0].AssignedTo);
    }

    [Fact]
    public async Task MissingPersonFieldIsNobodyAndNotAFailure()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        RespondWithTwo(server);

        IReadOnlyList<WorkItemSummary> items = await service.GetAsync(Remote, new WorkItemQuery()).Ok();

        Assert.Equal(string.Empty, items[1].AssignedTo);
    }

    [Fact]
    public async Task TagsAreSplitOnSemicolons()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        RespondWithTwo(server);

        IReadOnlyList<WorkItemSummary> items = await service.GetAsync(Remote, new WorkItemQuery()).Ok();

        Assert.Equal(["performance", "map"], items[0].Tags);
    }

    [Fact]
    public async Task IterationIsTheNameWithoutThePath()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        RespondWithTwo(server);

        IReadOnlyList<WorkItemSummary> items = await service.GetAsync(Remote, new WorkItemQuery()).Ok();

        Assert.Equal("Sprint 12", items[0].Iteration);
        Assert.Equal("Sprint 13", items[1].Iteration);

        // A path without steps is its own name: the project's root.
        Assert.Equal("UmeMap", items[1].Area);
    }

    [Fact]
    public async Task OrderFromTheQueryIsKept()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();

        // Fetching the fields answers in reverse order. The order that applies is the query's.
        server.Respond("wiql", """{"workItems":[{"id":41},{"id":42}]}""");
        server.Respond(
            "workitemsbatch",
            """
            {"count":2,"value":[
              {"id":42,"fields":{"System.Title":"Second"}},
              {"id":41,"fields":{"System.Title":"First"}}
            ]}
            """);

        IReadOnlyList<WorkItemSummary> items = await service.GetAsync(Remote, new WorkItemQuery()).Ok();

        Assert.Equal([41, 42], items.Select(item => item.Id));
    }

    [Fact]
    public async Task EmptyQueryAsksNoFurtherQuestions()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("wiql", """{"workItems":[]}""");

        IReadOnlyList<WorkItemSummary> items = await service.GetAsync(Remote, new WorkItemQuery()).Ok();

        Assert.Empty(items);
        Assert.DoesNotContain(server.Requests, request => request.Uri.Contains("workitemsbatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddressToTheWebPointsAtTheWorkItem()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        RespondWithTwo(server);

        IReadOnlyList<WorkItemSummary> items = await service.GetAsync(Remote, new WorkItemQuery()).Ok();

        Assert.Equal(
            "https://dev.azure.com/contoso/UmeMap%20och%20Geodatabas/_workitems/edit/41",
            items[0].WebUri.AbsoluteUri);
    }

    // ------------------------------------------------------------------ current sprint

    [Fact]
    public async Task CurrentSprintIsAskedForByItsTimeframe()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("teamsettings/iterations", """{"count":0,"value":[]}""");

        await service.GetCurrentIterationAsync(Remote);

        RecordedRequest request = server.Last("teamsettings/iterations");

        // Without a team in the address the API answers for the project's default team, which is
        // the same sprint the web interface shows.
        Assert.Contains("UmeMap%20och%20Geodatabas/_apis/work/teamsettings/iterations", request.Uri, StringComparison.Ordinal);
        Assert.Contains("$timeframe=current", request.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentSprintCarriesItsNameAndDates()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        server.Respond(
            "teamsettings/iterations",
            """
            {"count":1,"value":[{
              "name":"Sprint 12",
              "path":"UmeMap och Geodatabas\\Sprint 12",
              "attributes":{"startDate":"2026-09-08T00:00:00Z","finishDate":"2026-09-21T00:00:00Z","timeFrame":"current"}
            }]}
            """);

        Result<WorkItemIteration> current = await service.GetCurrentIterationAsync(Remote);

        Assert.True(current.IsSuccess(out WorkItemIteration? sprint));
        Assert.Equal("Sprint 12", sprint.Name);
        Assert.Equal(new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc), sprint.StartsOn!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task ProjectWithoutAnOngoingSprintSaysSoQuietly()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("teamsettings/iterations", """{"count":0,"value":[]}""");

        Assert.Equal(ResultStatus.NotFound, (await service.GetCurrentIterationAsync(Remote)).Status);
    }

    [Fact]
    public async Task SprintThatCannotBeReadDoesNotBringDownTheView()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("teamsettings/iterations", """{"message":"nope"}""", System.Net.HttpStatusCode.Forbidden);

        // A token without access to the sprints should give a list without a quick filter, not
        // an error that brings down the whole work view.
        Assert.Equal(ResultStatus.Unavailable, (await service.GetCurrentIterationAsync(Remote)).Status);
    }

    // ------------------------------------------------------------------ the board

    private static void RespondWithBoard(FakeAzureDevOps server)
    {
        server.Respond("work/boards?", """{"count":2,"value":[{"id":"e1","name":"Epics"},{"id":"s1","name":"Stories"}]}""");
        server.Respond(
            "work/boards/s1",
            """
            {"name":"Stories","columns":[
              {"name":"Proposed","columnType":"incoming","itemLimit":0},
              {"name":"Active","columnType":"inProgress","itemLimit":10},
              {"name":"Testing","columnType":"inProgress","itemLimit":5},
              {"name":"Closed","columnType":"outgoing","itemLimit":0}
            ]}
            """);
    }

    [Fact]
    public async Task BoardForRequirementsIsTheOnePicked()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        RespondWithBoard(server);

        Result<BoardDefinition> found = await service.GetBoardAsync(Remote);

        // The requirements level and not the first board in the response: that is the one people work in.
        Assert.True(found.IsSuccess(out BoardDefinition? board));
        Assert.Equal("Stories", board.Name);
    }

    [Fact]
    public async Task BoardColumnsKeepTheirOrderAndLimits()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        RespondWithBoard(server);

        Assert.True((await service.GetBoardAsync(Remote)).IsSuccess(out BoardDefinition? board));

        Assert.Equal(["Proposed", "Active", "Testing", "Closed"], board.Columns.Select(column => column.Name));
        Assert.Equal(10, board.Columns[1].ItemLimit);
        Assert.True(board.Columns[0].IsIncoming);
        Assert.True(board.Columns[^1].IsOutgoing);
    }

    [Fact]
    public async Task ProjectWithoutABoardSaysSoQuietly()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("work/boards", """{"message":"nope"}""", System.Net.HttpStatusCode.Forbidden);

        Assert.Equal(ResultStatus.Unavailable, (await service.GetBoardAsync(Remote)).Status);
    }

    // ------------------------------------------------------------------ moving

    private static WorkItemSummary Card(int id, string type, string state, string column) => new(
        id,
        type,
        "A card",
        state,
        string.Empty,
        string.Empty,
        string.Empty,
        [],
        null,
        default,
        new Uri("https://dev.azure.com/x"),
        column);

    [Fact]
    public async Task MovingACardWritesTheBoardColumn()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("wit/workitems/7", """{"id":7,"fields":{"System.BoardColumn":"Testing"}}""");

        await service.MoveAsync(Remote, Card(7, "Bug", "Active", "Active"), "Testing").Ok();

        RecordedRequest request = server.Last("wit/workitems/7");

        Assert.Equal("PATCH", request.Method);
        Assert.Contains("System.BoardColumn", request.Body ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("Testing", request.Body ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MovingATaskWritesTheStateInstead()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("wit/workitems/8", """{"id":8,"fields":{"System.State":"Active"}}""");

        // A task is not on the kanban board and has no column to write. The state
        // is what there is, and its names coincide with the board's middle columns.
        await service.MoveAsync(Remote, Card(8, "Task", "New", column: string.Empty), "Active").Ok();

        string body = server.Last("wit/workitems/8").Body ?? string.Empty;

        Assert.Contains("System.State", body, StringComparison.Ordinal);
        Assert.DoesNotContain("System.BoardColumn", body, StringComparison.Ordinal);
    }

    [Fact]
    public void CardKnowsWhichLaneItBelongsIn()
    {
        Assert.Equal("Testing", Card(1, "Bug", "Active", "Testing").Lane);

        // Without a board column the card falls back on its state.
        Assert.Equal("Active", Card(2, "Task", "Active", string.Empty).Lane);
        Assert.True(Card(2, "Task", "Active", string.Empty).MovesByState);
        Assert.False(Card(1, "Bug", "Active", "Testing").MovesByState);
    }

    // ------------------------------------------------------------------ creating

    [Fact]
    public async Task NewWorkItemHasItsTypeInTheAddress()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("workitems/$", """{"id":99,"fields":{"System.Title":"New"}}""");

        await service.CreateAsync(Remote, new NewWorkItem("User Story", "New")).Ok();

        // The dollar sign must stay; the space in the type name must be encoded.
        Assert.Contains("/wit/workitems/$User%20Story", server.Last("workitems").Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewWorkItemIsSentAsJsonPatch()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("workitems/$", """{"id":99,"fields":{}}""");

        await service.CreateAsync(Remote, new NewWorkItem("Bug", "Something went wrong", "A description")).Ok();

        JsonElement patch = Json(server.Last("workitems").Body);

        Assert.Equal(JsonValueKind.Array, patch.ValueKind);
        Assert.Contains(
            patch.EnumerateArray(),
            operation => string.Equals(
                operation.GetProperty("path").GetString(),
                "/fields/System.Title",
                StringComparison.Ordinal)
                && string.Equals(
                    operation.GetProperty("op").GetString(),
                    "add",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task BugGetsItsStepsInTheFieldForThem()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("workitems/$", """{"id":99,"fields":{}}""");

        await service.CreateAsync(Remote, new NewWorkItem("Bug", "Error", ReproSteps: "<p>1. Click</p>")).Ok();

        Assert.Contains(
            "Microsoft.VSTS.TCM.ReproSteps",
            server.Last("workitems").Body ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyFieldsAreNotSentAtAll()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();
        server.Respond("workitems/$", """{"id":99,"fields":{}}""");

        await service.CreateAsync(Remote, new NewWorkItem("Bug", "Error", Description: "   ")).Ok();

        // Azure DevOps distinguishes between "no value" and "empty text". The latter makes the field
        // filled in with nothing.
        Assert.DoesNotContain(
            "System.Description",
            server.Last("workitems").Body ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkItemWithoutATitleIsRefusedBeforeItIsSent()
    {
        (AzureDevOpsWorkItemClient? service, FakeAzureDevOps? server) = Create();

        (string error, ResultStatus status) = await service.CreateAsync(Remote, new NewWorkItem("Bug", "  ")).Failed();

        Assert.Equal(ResultStatus.Invalid, status);
        Assert.Contains("title", error, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }
}
