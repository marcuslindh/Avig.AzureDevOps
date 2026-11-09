using Avig.AzureDevOps.Identities;
using Avig.AzureDevOps.PullRequests;
using Avig.AzureDevOps.WorkItems;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Avig.AzureDevOps.Tests;

/// <summary>
/// Profile pictures: where the address comes from in each answer, and that the picture is
/// fetched with the sign-in only from Azure DevOps itself.
/// </summary>
public sealed class AvatarTests
{
    private static readonly AzureDevOpsRemote Remote = new("contoso", "UmeMap", "UmeMap");

    private const string AvatarLink = "https://dev.azure.com/contoso/_apis/GraphProfile/MemberAvatars/aad.anna";

    private const string OldImageUrl = "https://dev.azure.com/contoso/_api/_common/identityImage?id=anna";

    // ------------------------------------------------------------------ addresses

    [Fact]
    public async Task WorkItemAssigneePrefersTheAvatarLink()
    {
        FakeAzureDevOps server = new();
        AzureDevOpsWorkItemClient service = new(
            server.CreateClient(),
            new FakeCredential(),
            NullLogger<AzureDevOpsWorkItemClient>.Instance);

        server.Respond("wiql", """{"workItems":[{"id":41},{"id":42}]}""");
        server.Respond(
            "workitemsbatch",
            $$$"""
            {"count":2,"value":[
              {"id":41,"fields":{"System.Title":"With picture","System.ChangedDate":"2026-09-16T10:30:00Z",
                "System.AssignedTo":{"displayName":"Anna","imageUrl":"{{{OldImageUrl}}}",
                  "_links":{"avatar":{"href":"{{{AvatarLink}}}" } } } }},
              {"id":42,"fields":{"System.Title":"Nobody","System.ChangedDate":"2026-09-15T10:30:00Z"}}
            ]}
            """);

        IReadOnlyList<WorkItemSummary> items = await service.GetAsync(Remote, new WorkItemQuery()).Ok();

        Assert.Equal(new Uri(AvatarLink), items[0].AssignedToImageUrl);
        Assert.Null(items[1].AssignedToImageUrl);
    }

    [Fact]
    public async Task WorkItemCommentAuthorFallsBackToImageUrl()
    {
        FakeAzureDevOps server = new();
        AzureDevOpsWorkItemClient service = new(
            server.CreateClient(),
            new FakeCredential(),
            NullLogger<AzureDevOpsWorkItemClient>.Instance);

        server.Respond(
            "comments",
            $$$"""
            {"totalCount":1,"count":1,"comments":[
              {"id":1,"text":"<p>Hi</p>","createdDate":"2026-09-16T10:30:00Z",
               "createdBy":{"displayName":"Anna","imageUrl":"{{{OldImageUrl}}}"}}
            ]}
            """);

        IReadOnlyList<WorkItemComment> comments = await service.GetCommentsAsync(Remote, 41).Ok();

        Assert.Equal(new Uri(OldImageUrl), Assert.Single(comments).AuthorImageUrl);
    }

    [Fact]
    public async Task PullRequestAuthorReviewerAndCommentAuthorHavePictures()
    {
        FakeAzureDevOps server = new();
        AzureDevOpsPullRequestClient service = new(
            server.CreateClient(),
            new FakeCredential(),
            NullLogger<AzureDevOpsPullRequestClient>.Instance);

        server.Respond(
            "threads",
            $$$"""
            {"count":1,"value":[{"id":1,"status":"active","isDeleted":false,
              "comments":[{"id":1,"content":"Extract this","commentType":"text","isDeleted":false,
                "publishedDate":"2026-08-01T10:00:00Z",
                "author":{"displayName":"Anna","_links":{"avatar":{"href":"{{{AvatarLink}}}" } } } }]}]}
            """);
        server.Respond(
            "pullrequests",
            $$$"""
            {"count":1,"value":[{
                "pullRequestId": 42, "title": "T", "status": "active",
                "createdBy": { "displayName": "Marcus", "imageUrl": "{{{OldImageUrl}}}" },
                "sourceRefName": "refs/heads/feature/x", "targetRefName": "refs/heads/main",
                "creationDate": "2026-08-01T09:00:00Z",
                "reviewers": [ { "id": "r1", "displayName": "Anna", "vote": 10,
                                 "_links": { "avatar": { "href": "{{{AvatarLink}}}" } } } ]
            }]}
            """);

        IReadOnlyList<PullRequestOverview> list = await service.ListAsync(Remote, new PullRequestQuery()).Ok();
        IReadOnlyList<PullRequestThread> threads = await service.GetThreadsAsync(Remote, pullRequestId: 42, iteration: 1).Ok();

        PullRequestOverview overview = Assert.Single(list);

        Assert.Equal(new Uri(OldImageUrl), overview.Summary.AuthorImageUrl);
        Assert.Equal(new Uri(AvatarLink), Assert.Single(overview.Reviewers).Identity.ImageUrl);
        Assert.Equal(new Uri(AvatarLink), Assert.Single(threads).Comments[0].AuthorImageUrl);
    }

    // ------------------------------------------------------------------ download

    [Fact]
    public async Task PictureIsFetchedWithTheSignIn()
    {
        (AzureDevOpsAvatarClient avatars, FakeAzureDevOps server) = CreateAvatars();
        byte[] png = [0x89, 0x50, 0x4E, 0x47];

        server.RespondBytes("MemberAvatars", png, "image/png");

        AzureDevOpsImage image = await avatars.DownloadAsync(new Uri(AvatarLink)).Ok();

        Assert.Equal(png, image.Content.ToArray());
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal("Basic testtoken", server.Last("MemberAvatars").Authorization);
    }

    [Theory]
    [InlineData("https://evil.example.com/avatar.png")]
    [InlineData("http://dev.azure.com/contoso/_apis/GraphProfile/MemberAvatars/aad.anna")]
    [InlineData("https://dev.azure.com.evil.example.com/avatar.png")]
    public async Task ForeignOrPlainAddressesAreNeverSentTheSignIn(string address)
    {
        (AzureDevOpsAvatarClient avatars, FakeAzureDevOps server) = CreateAvatars();

        server.RespondBytes("avatar", [1, 2, 3], "image/png");

        (_, ResultStatus status) = await avatars.DownloadAsync(new Uri(address)).Failed();

        Assert.Equal(ResultStatus.Invalid, status);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task OlderOrganizationHostIsAccepted()
    {
        (AzureDevOpsAvatarClient avatars, FakeAzureDevOps server) = CreateAvatars();

        server.RespondBytes("identityImage", [1, 2, 3], "image/jpeg");

        Uri address = new("https://contoso.visualstudio.com/_api/_common/identityImage?id=a");

        AzureDevOpsImage image = await avatars.DownloadAsync(address).Ok();

        Assert.Equal("image/jpeg", image.ContentType);
    }

    [Fact]
    public async Task PictureLargerThanTheLimitIsRefused()
    {
        (AzureDevOpsAvatarClient avatars, FakeAzureDevOps server) = CreateAvatars();

        server.RespondBytes("MemberAvatars", new byte[AzureDevOpsAvatarClient.MaxImageBytes + 1], "image/png");

        (_, ResultStatus status) = await avatars.DownloadAsync(new Uri(AvatarLink)).Failed();

        Assert.Equal(ResultStatus.Invalid, status);
    }

    [Fact]
    public async Task PictureExactlyAtTheLimitIsAccepted()
    {
        (AzureDevOpsAvatarClient avatars, FakeAzureDevOps server) = CreateAvatars();

        server.RespondBytes("MemberAvatars", new byte[AzureDevOpsAvatarClient.MaxImageBytes], "image/png");

        AzureDevOpsImage image = await avatars.DownloadAsync(new Uri(AvatarLink)).Ok();

        Assert.Equal(AzureDevOpsAvatarClient.MaxImageBytes, image.Content.Length);
    }

    [Fact]
    public async Task AnAnswerThatIsNotAPictureIsAnError()
    {
        (AzureDevOpsAvatarClient avatars, FakeAzureDevOps server) = CreateAvatars();

        // A token that is not accepted can give the sign-in page with a success status.
        server.RespondBytes("MemberAvatars", "<html></html>"u8.ToArray(), "text/html");

        (_, ResultStatus status) = await avatars.DownloadAsync(new Uri(AvatarLink)).Failed();

        Assert.Equal(ResultStatus.Error, status);
    }

    [Fact]
    public async Task ARefusalKeepsTheServersStatus()
    {
        (AzureDevOpsAvatarClient avatars, FakeAzureDevOps server) = CreateAvatars();

        server.Respond("MemberAvatars", """{"message":"nope"}""", System.Net.HttpStatusCode.Unauthorized);

        (_, ResultStatus status) = await avatars.DownloadAsync(new Uri(AvatarLink)).Failed();

        Assert.Equal(ResultStatus.Unauthorized, status);
    }

    [Fact]
    public void TheAvatarClientIsRegistered()
    {
        ServiceCollection services = new();

        services.AddAvigAzureDevOps(() => "secret");

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<AzureDevOpsAvatarClient>(provider.GetRequiredService<IAvatarClient>());
    }

    private static (AzureDevOpsAvatarClient Avatars, FakeAzureDevOps Server) CreateAvatars()
    {
        FakeAzureDevOps server = new();

        AzureDevOpsAvatarClient avatars = new(
            server.CreateClient(),
            new FakeCredential(),
            NullLogger<AzureDevOpsAvatarClient>.Instance);

        return (avatars, server);
    }
}
