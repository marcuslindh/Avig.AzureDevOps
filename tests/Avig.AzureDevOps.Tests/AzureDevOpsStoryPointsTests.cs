using System.Text.Json;
using Avig.AzureDevOps.WorkItems;
using Microsoft.Extensions.Logging.Abstractions;

namespace Avig.AzureDevOps.Tests;

/// <summary>
/// Tests for story points and the project's tags.
/// </summary>
/// <remarks>
/// What can go wrong is the field. Agile, Scrum and CMMI call the estimate different things,
/// and a write to a field the type does not have is rejected by the server. The field must
/// therefore be chosen from the type's own fields, never guessed.
/// </remarks>
public sealed class AzureDevOpsStoryPointsTests
{
    private const string StoryPoints = "Microsoft.VSTS.Scheduling.StoryPoints";
    private const string Effort = "Microsoft.VSTS.Scheduling.Effort";

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

    private static string TypeFields(params string[] fields) =>
        JsonSerializer.Serialize(new { value = fields.Select(field => new { referenceName = field }) });

    private static List<JsonElement> LastPatch(FakeAzureDevOps server)
    {
        RecordedRequest request = server.Requests.Last(request => string.Equals(request.Method, "PATCH", StringComparison.Ordinal));

        return [.. JsonSerializer.Deserialize<JsonElement>(request.Body ?? "[]").EnumerateArray()];
    }

    private static JsonElement Operation(List<JsonElement> patch, string field) =>
        patch.Single(operation => string.Equals(operation.GetProperty("path").GetString(), "/fields/" + field, StringComparison.Ordinal));

    [Fact]
    public async Task StoryPointsAreWrittenToTheFieldTheTypeHas()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitemtypes/", TypeFields("System.Title", StoryPoints));
        server.Respond(
            "workitems/42",
            $$$"""{"id":42,"fields":{"System.WorkItemType":"User Story","System.Title":"Search","{{{StoryPoints}}}":5}}""");

        WorkItemDetails updated = await service.UpdateAsync(Remote, 42, new WorkItemEdit("Search", StoryPoints: 5)).Ok();

        JsonElement operation = Operation(LastPatch(server), StoryPoints);

        Assert.Equal("add", operation.GetProperty("op").GetString());
        Assert.Equal(5, operation.GetProperty("value").GetDouble());
        Assert.Contains("workitemtypes/User%20Story/fields", server.Last("workitemtypes/").Uri, StringComparison.Ordinal);
        Assert.Equal(5, updated.Summary.StoryPoints);
    }

    [Fact]
    public async Task ScrumWritesEffort()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitemtypes/", TypeFields("System.Title", Effort));
        server.Respond("workitems/42", """{"id":42,"fields":{"System.WorkItemType":"Product Backlog Item"}}""");

        await service.UpdateAsync(Remote, 42, new WorkItemEdit("Search", StoryPoints: 0.5)).Ok();

        Assert.Equal(0.5, Operation(LastPatch(server), Effort).GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task ClearingRemovesTheValueWhenThereIsOne()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitemtypes/", TypeFields(StoryPoints));
        server.Respond("workitems/42", $$$"""{"id":42,"fields":{"System.WorkItemType":"Bug","{{{StoryPoints}}}":3}}""");

        await service.UpdateAsync(Remote, 42, new WorkItemEdit("Search", ClearStoryPoints: true)).Ok();

        Assert.Equal("remove", Operation(LastPatch(server), StoryPoints).GetProperty("op").GetString());
    }

    [Fact]
    public async Task ClearingNothingSendsNoRemoval()
    {
        // Removing a value that does not exist is an error to the server.
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitemtypes/", TypeFields(StoryPoints));
        server.Respond("workitems/42", """{"id":42,"fields":{"System.WorkItemType":"Bug"}}""");

        await service.UpdateAsync(Remote, 42, new WorkItemEdit("Search", ClearStoryPoints: true)).Ok();

        Assert.DoesNotContain(
            LastPatch(server),
            operation => string.Equals(operation.GetProperty("path").GetString(), "/fields/" + StoryPoints, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypeWithoutStoryPointsIsRefusedBeforeAnythingIsWritten()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitemtypes/", TypeFields("System.Title", "Microsoft.VSTS.Scheduling.RemainingWork"));
        server.Respond("workitems/42", """{"id":42,"fields":{"System.WorkItemType":"Task"}}""");

        (string error, _) = await service.UpdateAsync(Remote, 42, new WorkItemEdit("Search", StoryPoints: 2)).Failed();

        Assert.Contains("Task", error, StringComparison.Ordinal);
        Assert.DoesNotContain(server.Requests, request => string.Equals(request.Method, "PATCH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypeFieldsAreAskedForOnce()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitemtypes/", TypeFields(StoryPoints));
        server.Respond("workitems/42", """{"id":42,"fields":{"System.WorkItemType":"Bug"}}""");

        await service.UpdateAsync(Remote, 42, new WorkItemEdit("Search", StoryPoints: 1)).Ok();
        await service.UpdateAsync(Remote, 42, new WorkItemEdit("Search", StoryPoints: 2)).Ok();

        Assert.Single(server.Requests, request => request.Uri.Contains("workitemtypes/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateWritesStoryPointsToTheTypesField()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitemtypes/", TypeFields(StoryPoints));
        server.Respond("workitems/$Bug", """{"id":7,"fields":{"System.WorkItemType":"Bug"}}""");

        await service.CreateAsync(Remote, new NewWorkItem("Bug", "The map does not load", StoryPoints: 3)).Ok();

        RecordedRequest request = server.Last("workitems/$Bug");
        List<JsonElement> patch = [.. JsonSerializer.Deserialize<JsonElement>(request.Body!).EnumerateArray()];

        Assert.Equal(3, Operation(patch, StoryPoints).GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task EffortIsReadAsStoryPoints()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitems/42", $$$"""{"id":42,"fields":{"System.WorkItemType":"Product Backlog Item","{{{Effort}}}":8}}""");

        WorkItemDetails details = await service.GetDetailsAsync(Remote, 42).Ok();

        Assert.Equal(8, details.Summary.StoryPoints);
        Assert.Contains(Uri.EscapeDataString(Effort), server.Last("workitems/42").Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAnEstimateThereAreNoStoryPoints()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitems/42", """{"id":42,"fields":{"System.WorkItemType":"Bug"}}""");

        WorkItemDetails details = await service.GetDetailsAsync(Remote, 42).Ok();

        Assert.Null(details.Summary.StoryPoints);
    }

    [Fact]
    public async Task ProjectTagsAreReadWithThePreviewVersionInOrder()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("wit/tags", """{"count":3,"value":[{"name":"map"},{"name":"Bug"},{"name":"api"}]}""");

        IReadOnlyList<string> tags = await service.GetTagsAsync(Remote).Ok();

        Assert.Equal(["api", "Bug", "map"], tags);
        Assert.Contains("api-version=7.1-preview.1", server.Last("wit/tags").Uri, StringComparison.Ordinal);
    }
}
