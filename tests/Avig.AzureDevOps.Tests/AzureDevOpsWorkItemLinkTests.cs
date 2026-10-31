using System.Text.Json;
using Avig.AzureDevOps.WorkItems;
using Microsoft.Extensions.Logging.Abstractions;

namespace Avig.AzureDevOps.Tests;

/// <summary>
/// Tests for comments, links and the fields "item edit" can write besides the text.
/// </summary>
/// <remarks>
/// What can go wrong is the requests: the wrong api-version for the comments (they only
/// exist as a preview version), the wrong name for the link type, and — worst — a removal
/// that takes the wrong link because the indexes have shifted. All of that is pinned down
/// here.
/// </remarks>
public sealed class AzureDevOpsWorkItemLinkTests
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

    private static List<JsonElement> Patch(RecordedRequest request) =>
        [.. JsonSerializer.Deserialize<JsonElement>(request.Body ?? "[]").EnumerateArray()];

    private static string? Value(List<JsonElement> patch, string path) =>
        patch.Where(operation => string.Equals(operation.GetProperty("path").GetString(), path, StringComparison.Ordinal))
            .Select(operation => operation.GetProperty("value").GetString())
            .FirstOrDefault();

    /// <summary>A work item with a parent, a related item, a branch and a duplicate.</summary>
    private const string WithRelations =
        """
        {"id":42,"rev":7,"fields":{},"relations":[
          {"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://dev.azure.com/contoso/_apis/wit/workItems/10"},
          {"rel":"System.LinkTypes.Related","url":"https://dev.azure.com/contoso/_apis/wit/workItems/55"},
          {"rel":"ArtifactLink","url":"vstfs:///Git/Ref/a%2Fb%2FGBmain","attributes":{"name":"Branch"}},
          {"rel":"System.LinkTypes.Duplicate-Forward","url":"https://dev.azure.com/contoso/_apis/wit/workItems/55"}
        ]}
        """;

    // ------------------------------------------------------------------ comments

    [Fact]
    public async Task CommentIsPostedWithThePreviewApiVersion()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond(
            "workItems/42/comments",
            """{"id":3,"text":"<p>Fixed</p>","createdBy":{"displayName":"Marcus"},"createdDate":"2026-10-20T10:00:00Z"}""");

        WorkItemComment comment = await service.AddCommentAsync(Remote, 42, "<p>Fixed</p>").Ok();

        RecordedRequest request = server.Last("workItems/42/comments");

        Assert.Equal("POST", request.Method);
        Assert.Contains("api-version=7.1-preview.4", request.Uri, StringComparison.Ordinal);
        Assert.Equal("<p>Fixed</p>", JsonSerializer.Deserialize<JsonElement>(request.Body!).GetProperty("text").GetString());
        Assert.Equal(3, comment.Id);
        Assert.Equal("Marcus", comment.Author);
    }

    // ------------------------------------------------------------------ links

    [Fact]
    public async Task RelationsAreOnlyLinksToOtherWorkItems()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitems/42", WithRelations);

        IReadOnlyList<WorkItemRelation> relations = await service.GetRelationsAsync(Remote, 42).Ok();

        Assert.Contains("$expand=relations", server.Last("workitems/42").Uri, StringComparison.Ordinal);

        // The branch is not a link to a work item and must be left out.
        Assert.Equal(
            [
                new WorkItemRelation(WorkItemLinkKind.Parent, 10),
                new WorkItemRelation(WorkItemLinkKind.Related, 55),
                new WorkItemRelation(WorkItemLinkKind.Duplicate, 55),
            ],
            relations);
    }

    [Theory]
    [InlineData(WorkItemLinkKind.Related, "System.LinkTypes.Related")]
    [InlineData(WorkItemLinkKind.Parent, "System.LinkTypes.Hierarchy-Reverse")]
    [InlineData(WorkItemLinkKind.Child, "System.LinkTypes.Hierarchy-Forward")]
    [InlineData(WorkItemLinkKind.DuplicateOf, "System.LinkTypes.Duplicate-Reverse")]
    [InlineData(WorkItemLinkKind.Predecessor, "System.LinkTypes.Dependency-Reverse")]
    public async Task LinkIsAddedAsARelationWithTheRightType(WorkItemLinkKind kind, string rel)
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitems/42", """{"id":42,"fields":{}}""");

        await service.LinkAsync(Remote, 42, new WorkItemRelation(kind, 77)).Ok();

        RecordedRequest request = server.Last("workitems/42");
        JsonElement operation = Assert.Single(Patch(request));

        Assert.Equal("PATCH", request.Method);
        Assert.Equal("/relations/-", operation.GetProperty("path").GetString());
        Assert.Equal(rel, operation.GetProperty("value").GetProperty("rel").GetString());
        Assert.Equal(
            "https://dev.azure.com/contoso/_apis/wit/workItems/77",
            operation.GetProperty("value").GetProperty("url").GetString());
    }

    [Fact]
    public async Task UnlinkRemovesFromTheBackAndChecksTheRevision()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitems/42", WithRelations);

        int removed = await service.UnlinkAsync(Remote, 42, 55, kind: null).Ok();

        List<JsonElement> patch = Patch(server.Last("workitems/42"));

        Assert.Equal(2, removed);

        // The revision first, so that the server refuses if someone else has moved the links.
        Assert.Equal("test", patch[0].GetProperty("op").GetString());
        Assert.Equal(7, patch[0].GetProperty("value").GetInt32());

        // Index 3 before 1: otherwise the first removal moves the second.
        Assert.Equal(
            ["/relations/3", "/relations/1"],
            patch.Skip(1).Select(operation => operation.GetProperty("path").GetString()));
        Assert.All(patch.Skip(1), operation => Assert.Equal("remove", operation.GetProperty("op").GetString()));
    }

    [Fact]
    public async Task UnlinkWithAKindLeavesOtherLinksToTheSameItem()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitems/42", WithRelations);

        int removed = await service.UnlinkAsync(Remote, 42, 55, WorkItemLinkKind.Duplicate).Ok();

        Assert.Equal(1, removed);
        Assert.Equal("/relations/3", Patch(server.Last("workitems/42"))[1].GetProperty("path").GetString());
    }

    [Fact]
    public async Task UnlinkWithoutAMatchSendsNothing()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitems/42", WithRelations);

        int removed = await service.UnlinkAsync(Remote, 42, 999, kind: null).Ok();

        Assert.Equal(0, removed);
        Assert.DoesNotContain(server.Requests, request => string.Equals(request.Method, "PATCH", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the fields

    [Fact]
    public async Task EditWritesStateAssignmentTagsAndSprint()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitems/42", """{"id":42,"fields":{"System.Title":"Save"}}""");

        await service.UpdateAsync(
            Remote,
            42,
            new WorkItemEdit(
                "Save",
                State: "Active",
                AssignedTo: "anna@example.com",
                Tags: ["map", " search "],
                IterationPath: "UmeMap\\Sprint 12")).Ok();

        List<JsonElement> patch = Patch(server.Last("workitems/42"));

        Assert.Equal("Active", Value(patch, "/fields/System.State"));
        Assert.Equal("anna@example.com", Value(patch, "/fields/System.AssignedTo"));
        Assert.Equal("map; search", Value(patch, "/fields/System.Tags"));
        Assert.Equal("UmeMap\\Sprint 12", Value(patch, "/fields/System.IterationPath"));

        // The text fields were not given and must stay as they are.
        Assert.Null(Value(patch, "/fields/System.Description"));
    }

    [Fact]
    public async Task EmptyAssignmentAndTagsClearTheFields()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitems/42", """{"id":42,"fields":{"System.Title":"Save"}}""");

        await service.UpdateAsync(Remote, 42, new WorkItemEdit("Save", AssignedTo: string.Empty, Tags: [])).Ok();

        List<JsonElement> patch = Patch(server.Last("workitems/42"));

        // Empty means "clear" here, unlike the text fields where null means "leave as is".
        Assert.Equal(string.Empty, Value(patch, "/fields/System.AssignedTo"));
        Assert.Equal(string.Empty, Value(patch, "/fields/System.Tags"));
    }

    [Fact]
    public async Task FieldsThatAreNotGivenAreNotSent()
    {
        (AzureDevOpsWorkItemClient service, FakeAzureDevOps server) = Create();
        server.Respond("workitems/42", """{"id":42,"fields":{"System.Title":"Save"}}""");

        await service.UpdateAsync(Remote, 42, new WorkItemEdit("Save")).Ok();

        JsonElement only = Assert.Single(Patch(server.Last("workitems/42")));

        Assert.Equal("/fields/System.Title", only.GetProperty("path").GetString());
    }

    // ------------------------------------------------------------------ the query

    [Fact]
    public void MineIsAskedOfTheServer()
    {
        Assert.Contains(
            "[System.AssignedTo] = @Me",
            AzureDevOpsWorkItemClient.BuildWiql(includeClosed: false, assignedToMe: true),
            StringComparison.Ordinal);
        Assert.DoesNotContain("@Me", AzureDevOpsWorkItemClient.BuildWiql(includeClosed: false), StringComparison.Ordinal);
    }
}
