using System.Net;
using System.Text.Json;
using Avig.AzureDevOps.Wiki;
using Microsoft.Extensions.Logging.Abstractions;

namespace Avig.AzureDevOps.Tests;

/// <summary>
/// Tests for the wiki: the addresses, the page tree, a page that does not exist, and search.
/// </summary>
public sealed class AzureDevOpsWikiClientTests
{
    private static readonly AzureDevOpsRemote Remote = new("contoso", "UmeMap och Geodatabas", "UmeMap");

    private static (AzureDevOpsWikiClient Service, FakeAzureDevOps Server) Create()
    {
        FakeAzureDevOps server = new();

        AzureDevOpsWikiClient service = new(
            server.CreateClient(),
            new FakeCredential(),
            NullLogger<AzureDevOpsWikiClient>.Instance);

        return (service, server);
    }

    [Fact]
    public async Task WikisAreListedWithTheProjectWikiMarked()
    {
        (AzureDevOpsWikiClient service, FakeAzureDevOps server) = Create();
        server.Respond(
            "wiki/wikis",
            """{"count":2,"value":[{"id":"a1","name":"Code","type":"codeWiki"},{"id":"b2","name":"UmeMap.wiki","type":"projectWiki"}]}""");

        IReadOnlyList<WikiSummary> wikis = await service.GetWikisAsync(Remote).Ok();

        Assert.Equal([new WikiSummary("a1", "Code", false), new WikiSummary("b2", "UmeMap.wiki", true)], wikis);
    }

    [Fact]
    public async Task PageIsReadByPathWithItsContent()
    {
        (AzureDevOpsWikiClient service, FakeAzureDevOps server) = Create();
        server.Respond(
            "wiki/wikis/b2/pages",
            """
            {"id":12,"path":"/Utveckling/Kom igång","content":"# Kom igång\n\nKlona repot.",
             "remoteUrl":"https://dev.azure.com/contoso/UmeMap/_wiki/wikis/UmeMap.wiki/12/Kom-igång",
             "subPages":[{"path":"/Utveckling/Kom igång/Databasen"}]}
            """);

        Result<WikiPage> result = await service.GetPageAsync(Remote, "b2", "/Utveckling/Kom igång");

        Assert.True(result.IsSuccess(out WikiPage? page));
        Assert.Equal("# Kom igång\n\nKlona repot.", page.Markdown);
        Assert.Equal(12, page.Id);
        Assert.Equal(["/Utveckling/Kom igång/Databasen"], page.SubPages);
        Assert.NotNull(page.WebUri);

        string uri = server.Last("wiki/wikis/b2/pages").Uri;

        Assert.Contains("path=%2FUtveckling%2FKom%20ig%C3%A5ng", uri, StringComparison.Ordinal);
        Assert.Contains("includeContent=true", uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageIsReadById()
    {
        (AzureDevOpsWikiClient service, FakeAzureDevOps server) = Create();
        server.Respond("wiki/wikis/b2/pages/12", """{"id":12,"path":"/Start","content":"Hello"}""");

        Result<WikiPage> result = await service.GetPageByIdAsync(Remote, "b2", 12);

        Assert.True(result.IsSuccess(out WikiPage? page));
        Assert.Equal("/Start", page.Path);
    }

    [Fact]
    public async Task MissingPageIsAnAnswerNotAnError()
    {
        (AzureDevOpsWikiClient service, FakeAzureDevOps server) = Create();
        server.Respond(
            "wiki/wikis/b2/pages",
            """{"message":"The page '/Missing' specified in the add operation does not exist."}""",
            HttpStatusCode.NotFound);

        Result<WikiPage> result = await service.GetPageAsync(Remote, "b2", "/Missing");

        Assert.True(result.IsFailure(out string? error, out ResultStatus status, out _));
        Assert.Equal(ResultStatus.NotFound, status);
        Assert.Contains("/Missing", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TreeKeepsTheWikisOrder()
    {
        (AzureDevOpsWikiClient service, FakeAzureDevOps server) = Create();
        server.Respond(
            "wiki/wikis/b2/pages",
            """
            {"path":"/","subPages":[
              {"path":"/Operations","order":1,"subPages":[{"path":"/Operations/Servers","order":0}]},
              {"path":"/Start","order":0}
            ]}
            """);

        Result<WikiPageNode> result = await service.GetPageTreeAsync(Remote, "b2", "/");

        Assert.True(result.IsSuccess(out WikiPageNode? root));
        Assert.Equal(["/Start", "/Operations"], root.Children.Select(child => child.Path));
        Assert.Equal("Servers", root.Children[1].Children[0].Name);
        Assert.Contains("recursionLevel=full", server.Last("wiki/wikis/b2/pages").Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchGoesToTheSearchHostFilteredOnTheProject()
    {
        (AzureDevOpsWikiClient service, FakeAzureDevOps server) = Create();
        server.Respond(
            "wikisearchresults",
            """
            {"count":1,"results":[{"fileName":"Kom-igång.md","path":"/Utveckling/Kom-igång.md","wiki":{"name":"UmeMap.wiki"},
              "hits":[{"fieldReferenceName":"content","highlights":["Klona <highlighthit>repot</highlighthit> först"]}]}]}
            """);

        IReadOnlyList<WikiSearchHit> hits = await service.SearchAsync(Remote, "repot", 10).Ok();

        RecordedRequest request = server.Last("wikisearchresults");
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(request.Body!);

        Assert.StartsWith("https://almsearch.dev.azure.com/contoso/UmeMap%20och%20Geodatabas/", request.Uri, StringComparison.Ordinal);
        Assert.Equal("repot", body.GetProperty("searchText").GetString());
        Assert.Equal(10, body.GetProperty("$top").GetInt32());
        Assert.Equal("UmeMap och Geodatabas", body.GetProperty("filters").GetProperty("Project")[0].GetString());

        WikiSearchHit hit = Assert.Single(hits);

        Assert.Equal("/Utveckling/Kom igång", hit.Path);
        Assert.Equal("UmeMap.wiki", hit.WikiName);
        Assert.Equal(["Klona «repot» först"], hit.Highlights);
    }

    [Fact]
    public void HighlightsSplitAroundSwedishLettersAreJoined() =>
        Assert.Equal(
            "rätt «behörighet» till",
            AzureDevOpsWikiClient.Highlight(
                "rätt <highlighthit>beh</highlighthit><highlighthit>ö</highlighthit><highlighthit>righet</highlighthit> till"));

    [Theory]
    [InlineData("/Kom-igång.md", "/Kom igång")]
    [InlineData("/Operations/CI%2DCD.md", "/Operations/CI-CD")]
    [InlineData("Start.md", "/Start")]
    public void SearchPathBecomesThePagePath(string gitPath, string expected) =>
        Assert.Equal(expected, AzureDevOpsWikiClient.PathFromGitItem(gitPath));
}
