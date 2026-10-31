# Avig.AzureDevOps

Clients for the Azure DevOps REST API (`api-version=7.1`): pull requests, work items,
builds and wiki.

- **Pull requests** — list, create, edit, complete and abandon; threads and replies,
  including comments anchored to a line; reviewers and votes; links to work items and
  branches; attachments.
- **Work items** — queries, details, create and edit (Agile, Scrum and CMMI name the
  estimate differently; the client handles that), story points, tags, comments, links,
  board columns and the current sprint.
- **Builds** — the project's builds, newest first.
- **Wiki** — wikis, page trees, pages by path or id, and search.

The package does not store the token. The application decides where it is kept and gives
the package a function that returns it.

## Contents

- [Getting started](#getting-started)
- [Sign-in](#sign-in)
- [The repository](#the-repository)
- [Pull requests](#pull-requests)
- [Work items](#work-items)
- [Builds](#builds)
- [Wiki](#wiki)
- [Statuses](#statuses)
- [Results](#results)
- [Building](#building)

## Getting started

```powershell
dotnet add package Avig.AzureDevOps
```

```csharp
using Avig.AzureDevOps;

services.AddAvigAzureDevOps(
    () => Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT"),
    options =>
    {
        options.UserAgent = "MyApp/1.0";            // default: Avig.AzureDevOps/<version>
        options.Timeout = TimeSpan.FromSeconds(30); // default: 60 s
    });
```

This registers typed `HttpClient`s for `IPullRequestClient`, `IWorkItemClient`,
`IBuildClient` and `IWikiClient`. Inject the ones you need:

```csharp
public sealed class ReviewService(IPullRequestClient pullRequests, IWorkItemClient workItems)
{
    // ...
}
```

Every client has `IsConnected`, which is true when there is a token to send. Every call
takes an `AzureDevOpsRemote` that says which organization, project and repository it is
about, and an optional `CancellationToken`, and answers with a `Result` — no call throws;
see [Results](#results).

The examples below assume these:

```csharp
using Avig;                            // Result<T>
using Avig.AzureDevOps;
using Avig.AzureDevOps.Builds;
using Avig.AzureDevOps.PullRequests;
using Avig.AzureDevOps.Wiki;
using Avig.AzureDevOps.WorkItems;

AzureDevOpsRemote remote = new("contoso", "Webshop", "webshop-api");
```

## Sign-in

The sign-in is a personal access token, sent as HTTP Basic with an empty user name. You
supply it as a function that returns the token, or `null` when there is none. The function
is called on every request and every time `IsConnected` is read, so a token that is saved
or removed takes effect at once. Keep it cheap: hold the token in memory rather than
reading a file on every call.

### A token from anywhere

```csharp
// An environment variable, e.g. on a build agent or in a CLI.
services.AddAvigAzureDevOps(() => Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT"));

// Configuration: user secrets, appsettings, the Key Vault configuration provider.
services.AddAvigAzureDevOps(provider =>
    provider.GetRequiredService<IConfiguration>()["AzureDevOps:Token"]);
```

### A token store of your own

The overload that takes an `IServiceProvider` is for a token that lives in one of the
application's own services. It is called with the root provider, so resolve singletons
only.

```csharp
services.AddSingleton(new TokenStore(appDataFolder));
services.AddAvigAzureDevOps(provider => provider.GetRequiredService<TokenStore>().Token);

// Signing in and out is then the application's business:
tokenStore.Save(tokenFromTheSignInDialog);
tokenStore.Clear();
```

A store that keeps the token in a file, encrypted with DPAPI for the current Windows user
(needs the `System.Security.Cryptography.ProtectedData` package):

```csharp
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

[SupportedOSPlatform("windows")]
public sealed class TokenStore(string directory)
{
    // Extra data in the encryption, so that another application cannot decrypt the file.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MyApp.AzureDevOps.v1");

    private readonly string _path = Path.Combine(directory, "azure-devops.token");
    private readonly Lock _gate = new();
    private string? _token;
    private bool _loaded;

    public string? Token
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    _token = Load(_path);
                    _loaded = true;
                }

                return _token;
            }
        }
    }

    public void Save(string token)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(_path, ProtectedData.Protect(
                Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser));

            _token = token.Trim();
            _loaded = true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            File.Delete(_path);

            _token = null;
            _loaded = true;
        }
    }

    private static string? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // Another user's or another machine's file: sign in again.
            return null;
        }
    }
}
```

### Without dependency injection

`PersonalAccessTokenCredential` turns the function into an `IAzureDevOpsCredential`,
which every client takes in its constructor:

```csharp
using Microsoft.Extensions.Logging.Abstractions;

using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("MyTool/1.0");

PersonalAccessTokenCredential credential = new(() => Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT"));

AzureDevOpsBuildClient builds = new(http, credential, NullLogger<AzureDevOpsBuildClient>.Instance);
```

### Another way to sign in

To sign in some other way — Entra ID, or a token that has to be fetched asynchronously —
implement `IAzureDevOpsCredential`, register it, and use the overload without a token.
The package never replaces a credential that is already registered.

```csharp
using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;

public sealed class EntraCredential(TokenCredential azure) : IAzureDevOpsCredential
{
    // Azure DevOps' resource id in Entra ID.
    private static readonly string[] Scopes = ["499b84ac-1321-427f-aa17-267ca6975798/.default"];

    public bool IsConfigured => true;

    public async Task<Result> AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        try
        {
            AccessToken token = await azure.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            return Result.Success();
        }
        catch (AuthenticationFailedException exception)
        {
            // The message reaches the user as the call's error, and nothing is sent.
            return Result.Failure($"Could not sign in with Entra ID: {exception.Message}", ResultStatus.Unauthorized);
        }
    }
}

services.AddSingleton<IAzureDevOpsCredential>(new EntraCredential(new DefaultAzureCredential()));
services.AddAvigAzureDevOps(options => options.UserAgent = "MyApp/1.0");
```

## The repository

`AzureDevOpsRemote` names the organization, project and repository. `TryParse` reads it
from any of the git remote forms in use:

```csharp
string url = "https://contoso@dev.azure.com/contoso/Webshop/_git/webshop-api";

if (AzureDevOpsRemote.TryParse(url, out AzureDevOpsRemote? parsed))
{
    Console.WriteLine($"{parsed.Organization} / {parsed.Project} / {parsed.Repository}");
}

// Also understood:
//   https://dev.azure.com/contoso/Webshop/_git/webshop-api
//   git@ssh.dev.azure.com:v3/contoso/Webshop/webshop-api
//   https://contoso.visualstudio.com/Webshop/_git/webshop-api
```

Work items, builds and the wiki belong to the project, so for them only the organization
and the project matter.

## Pull requests

`IPullRequestClient`. Branch names are given without `refs/heads/`. Every call answers with
a `Result` — see [Results](#results) — and the examples stop at the first failure.

### Who am I, and who else is there

```csharp
Result<AzureDevOpsIdentity> signedIn = await pullRequests.GetCurrentUserAsync(remote);

if (!signedIn.IsSuccess(out AzureDevOpsIdentity? me))
{
    Console.WriteLine(signedIn.Error); // e.g. "Not signed in to Azure DevOps. Add a personal access token first."
    return;
}

Result<IReadOnlyList<AzureDevOpsIdentity>> people = await pullRequests.FindIdentitiesAsync(remote, "anna");
```

### Finding pull requests

```csharp
// The open ones, newest first — what a list view needs. An empty list on failure.
IReadOnlyList<PullRequestSummary> open = (await pullRequests.GetOpenAsync(remote)).DataOr([]);

// With status, reviewers and votes, filtered by status, creator and reviewer.
Result<IReadOnlyList<PullRequestOverview>> toReview = await pullRequests.ListAsync(
    remote,
    new PullRequestQuery(PullRequestStatusFilter.Active, ReviewerId: me.Id, Top: 20));

Result<IReadOnlyList<PullRequestOverview>> mergedByMe = await pullRequests.ListAsync(
    remote,
    new PullRequestQuery(PullRequestStatusFilter.Completed, CreatorId: me.Id));

// The open pull request for a branch. NotFound when there is none.
Result<PullRequestSummary> found = await pullRequests.FindForBranchAsync(remote, "feature/checkout");

if (found.IsSuccess(out PullRequestSummary? current))
{
    Console.WriteLine($"!{current.Id} {current.Title} → {current.WebUri}");
}

// One pull request, open or not, with everything.
Result<PullRequestOverview> loaded = await pullRequests.GetOverviewAsync(remote, 42);

if (loaded.IsSuccess(out PullRequestOverview? overview))
{
    Console.WriteLine($"{overview.Status}, draft: {overview.IsDraft}, merge: {overview.MergeStatus}");

    if (overview.MergeStatus == PullRequestMergeStatus.Conflicts)
    {
        Console.WriteLine("Resolve the conflicts before completing.");
    }

    foreach (PullRequestReviewer reviewer in overview.Reviewers)
    {
        Console.WriteLine($"{reviewer.Identity.Name}: {reviewer.Vote}, required: {reviewer.IsRequired}");
    }
}
```

### Creating and editing

```csharp
Result<PullRequestSummary> creating = await pullRequests.CreateAsync(
    remote,
    new NewPullRequest(
        SourceBranch: "feature/checkout",
        TargetBranch: "main",
        Title: "New checkout flow",
        Description: "Replaces the old three-step checkout.",
        IsDraft: true,
        WorkItemIds: [1234, 1235]));

if (!creating.IsSuccess(out PullRequestSummary? created))
{
    Console.WriteLine(creating.Error);
    return;
}

// Title and description.
Result<PullRequestContent> content = await pullRequests.GetContentAsync(remote, created.Id);

if (content.IsSuccess(out PullRequestContent? text))
{
    await pullRequests.UpdateContentAsync(
        remote,
        created.Id,
        text with { Description = text.Description + "\n\nTested in staging." });
}

// Out of draft.
Result<PullRequestOverview> published = await pullRequests.UpdateAsync(remote, created.Id, new PullRequestUpdate(IsDraft: false));
```

### Completing and abandoning

```csharp
Result<PullRequestOverview> loaded = await pullRequests.GetOverviewAsync(remote, 42);

if (!loaded.IsSuccess(out PullRequestOverview? pullRequest))
{
    Console.WriteLine(loaded.Error);
    return;
}

// Complete now. The server wants the commit being completed, so that nothing pushed in
// the meantime is merged unseen.
Result<PullRequestOverview> completed = await pullRequests.UpdateAsync(
    remote,
    pullRequest.Summary.Id,
    new PullRequestUpdate(
        Status: PullRequestStatus.Completed,
        LastMergeSourceCommitId: pullRequest.SourceCommitId,
        Completion: new PullRequestCompletion(
            PullRequestMergeStrategy.Squash,
            DeleteSourceBranch: true,
            MergeCommitMessage: $"Merged PR {pullRequest.Summary.Id}: {pullRequest.Summary.Title}")));

// A policy that stops the completion is a Conflict, with the server's reason in the message.
if (completed.IsFailure(out string? reason, out ResultStatus status) && status == ResultStatus.Conflict)
{
    Console.WriteLine(reason);
}

// Or complete automatically once the policies pass.
await pullRequests.UpdateAsync(
    remote,
    pullRequest.Summary.Id,
    new PullRequestUpdate(
        AutoCompleteSetById: me.Id,
        Completion: new PullRequestCompletion(PullRequestMergeStrategy.Merge)));

// Abandon, and bring it back.
await pullRequests.UpdateAsync(remote, pullRequest.Summary.Id, new PullRequestUpdate(Status: PullRequestStatus.Abandoned));
await pullRequests.UpdateAsync(remote, pullRequest.Summary.Id, new PullRequestUpdate(Status: PullRequestStatus.Active));
```

### Reviewers and votes

```csharp
Result<IReadOnlyList<AzureDevOpsIdentity>> candidates = await pullRequests.FindIdentitiesAsync(remote, "anna");

if (candidates.IsSuccess(out IReadOnlyList<AzureDevOpsIdentity>? matches) && matches.Count > 0)
{
    AzureDevOpsIdentity anna = matches[0];

    // Add a required reviewer.
    await pullRequests.SetReviewerAsync(remote, 42, anna.Id, PullRequestVote.None, isRequired: true);

    await pullRequests.RemoveReviewerAsync(remote, 42, anna.Id);
}

// Vote yourself.
Result<PullRequestReviewer> vote =
    await pullRequests.SetReviewerAsync(remote, 42, me.Id, PullRequestVote.ApprovedWithSuggestions, isRequired: false);
```

### Comments and threads

A comment on a line needs the iteration and the file's change-tracking id, which
`GetDetailsAsync` returns.

```csharp
Result<PullRequestDetails> fetched = await pullRequests.GetDetailsAsync(remote, summary);

if (!fetched.IsSuccess(out PullRequestDetails? details))
{
    Console.WriteLine(fetched.Error);
    return;
}

// A comment on the overview tab.
await pullRequests.CreateThreadAsync(remote, summary.Id, details.Iteration, "Looks good overall.", anchor: null);

// The files that were deleted cannot take a comment on the right-hand side. ChangeType is
// a flags value: a file that was moved and edited is Edit | Rename.
PullRequestFile? file = details.Files.FirstOrDefault(changed =>
    changed.Path == "/src/Checkout/Basket.cs" && !changed.ChangeType.HasFlag(PullRequestChangeType.Delete));

if (file is null)
{
    return;
}

// A comment on lines 12–14 of a changed file.
Result<PullRequestThread> created = await pullRequests.CreateThreadAsync(
    remote,
    summary.Id,
    details.Iteration,
    "This is null when the basket is empty.",
    new PullRequestAnchor(file.Path, StartLine: 12, EndLine: 14, file.ChangeTrackingId));

// Read the threads, reply and resolve.
IReadOnlyList<PullRequestThread> threads =
    (await pullRequests.GetThreadsAsync(remote, summary.Id, details.Iteration)).DataOr([]);

foreach (PullRequestThread active in threads.Where(candidate => candidate.Status == PullRequestThreadStatus.Active))
{
    PullRequestComment first = active.Comments[0];

    Console.WriteLine($"{active.FilePath}:{active.Line} — {first.Author}: {first.Text}");
}

if (created.IsSuccess(out PullRequestThread? thread))
{
    await pullRequests.ReplyAsync(remote, summary.Id, thread.Id, "Fixed in the latest push.");
    await pullRequests.SetThreadStatusAsync(remote, summary.Id, thread.Id, PullRequestThreadStatus.Fixed);
}
```

### Work items and branches

```csharp
// Work items on a pull request.
Result<IReadOnlyList<int>> linked = await pullRequests.GetLinkedWorkItemIdsAsync(remote, 42);

Result link = await pullRequests.LinkWorkItemAsync(remote, 42, 1236);
Result<bool> removed = await pullRequests.UnlinkWorkItemAsync(remote, 42, 1236); // false: was not linked

// Work items on a branch, before there is a pull request. The branch must have been pushed;
// otherwise the result is a failure that says so.
Result<IReadOnlyList<int>> onBranch = await pullRequests.GetBranchWorkItemIdsAsync(remote, "feature/checkout");

Result branchLink = await pullRequests.LinkBranchAsync(remote, "feature/checkout", 1234);
Result<bool> unlinked = await pullRequests.UnlinkBranchAsync(remote, "feature/checkout", 1234);
```

### Attachments

Images in pull request comments and work item fields point at the organization and need
the sign-in to download. An address outside the organization is refused
(`Forbidden`) rather than sent the token.

```csharp
Uri address = new("https://dev.azure.com/contoso/_apis/wit/attachments/0f1e2d3c-…?fileName=screenshot.png");

Result<byte[]> image = await pullRequests.DownloadAttachmentAsync(remote, address);

if (image.IsSuccess(out byte[]? bytes))
{
    await File.WriteAllBytesAsync("screenshot.png", bytes);
}
```

## Work items

`IWorkItemClient`. The estimate is called Story Points in Agile, Effort in Scrum and Size
in CMMI; the client reads and writes whichever the work item has, as `StoryPoints`.

### Querying

```csharp
// My open work items, most recently changed first.
Result<IReadOnlyList<WorkItemSummary>> query = await workItems.GetAsync(
    remote,
    new WorkItemQuery(IncludeClosed: false, Limit: 100, AssignedToMe: true));

if (!query.IsSuccess(out IReadOnlyList<WorkItemSummary>? mine))
{
    Console.WriteLine(query.Error);
    return;
}

foreach (WorkItemSummary item in mine)
{
    Console.WriteLine($"#{item.Id} [{item.Type}] {item.Title} — {item.Lane}, {item.Iteration}, {item.StoryPoints} sp");
}

// The tasks under a story, closed ones included.
Result<IReadOnlyList<WorkItemSummary>> children = await workItems.GetChildrenAsync(remote, 1234);

// The tags in the project, alphabetically.
IReadOnlyList<string> tags = (await workItems.GetTagsAsync(remote)).DataOr([]);
```

### The sprint and the board

Not every project has sprints or a board; then the result is `NotFound` or `Unavailable`.

```csharp
Result<WorkItemIteration> sprint = await workItems.GetCurrentIterationAsync(remote);

if (sprint.IsSuccess(out WorkItemIteration? current))
{
    Console.WriteLine($"{current.Name}: {current.StartsOn:d}–{current.EndsOn:d}");
}

Result<BoardDefinition> board = await workItems.GetBoardAsync(remote);

if (board.IsSuccess(out BoardDefinition? definition))
{
    foreach (BoardColumn column in definition.Columns)
    {
        Console.WriteLine($"{column.Name} (WIP limit {column.ItemLimit})");
    }
}

// Move a card. Writes the board column when the type has one and the state otherwise —
// item.MovesByState says which.
Result<WorkItemSummary> moved = await workItems.MoveAsync(remote, mine[0], "Code review");
```

### Details, creating and editing

```csharp
Result<WorkItemSummary> creating = await workItems.CreateAsync(
    remote,
    new NewWorkItem(
        Type: "Task",
        Title: "Validate the postcode",
        Description: "<p>Reject postcodes that are not five digits.</p>",
        AssignedTo: "anna@contoso.com",
        IterationPath: details.Summary.IterationPath,
        Tags: ["checkout"],
        ParentId: 1234,
        StoryPoints: 2));

if (!creating.IsSuccess(out WorkItemSummary? created))
{
    // An empty title is Invalid and nothing is sent.
    Console.WriteLine(creating.Error);
    return;
}

// null means "leave as is"; only the fields that are given are sent.
Result<WorkItemDetails> updated = await workItems.UpdateAsync(
    remote,
    created.Id,
    new WorkItemEdit(
        Title: created.Title,
        State: "Active",
        Tags: ["checkout", "validation"],
        StoryPoints: 3));

// An empty AssignedTo removes the assignment, and ClearStoryPoints the estimate.
await workItems.UpdateAsync(
    remote,
    created.Id,
    new WorkItemEdit(created.Title, AssignedTo: "", ClearStoryPoints: true));

// The text fields of one work item.
Result<WorkItemDetails> loaded = await workItems.GetDetailsAsync(remote, 1234);
```

### Comments

```csharp
Result<IReadOnlyList<WorkItemComment>> comments = await workItems.GetCommentsAsync(remote, 1234);

foreach (WorkItemComment comment in comments.DataOr([]))
{
    Console.WriteLine($"{comment.Author}, {comment.CreatedAt:g}: {WorkItemMarkdown.FromHtml(comment.Html)}");
}

Result<WorkItemComment> added = await workItems.AddCommentAsync(remote, 1234, "<p>Deployed to staging.</p>");
```

### Links

```csharp
Result<IReadOnlyList<WorkItemRelation>> relations = await workItems.GetRelationsAsync(remote, 1234);

await workItems.LinkAsync(remote, 1234, new WorkItemRelation(WorkItemLinkKind.Related, 1240));
await workItems.LinkAsync(remote, 1234, new WorkItemRelation(WorkItemLinkKind.Predecessor, 1200));

// Remove one kind of link, or every link to the target with kind: null.
// Both answer with how many links were removed.
Result<int> removedOne = await workItems.UnlinkAsync(remote, 1234, 1240, WorkItemLinkKind.Related);
Result<int> removedAll = await workItems.UnlinkAsync(remote, 1234, 1200, kind: null);
```

### HTML to markdown

Descriptions, repro steps, acceptance criteria and comments are HTML. `WorkItemMarkdown`
turns them into markdown: headings, lists, links, code, quotes and emphasis are kept, and
scripts and styles are dropped.

```csharp
string html = details.Description;

if (WorkItemMarkdown.HasTables(html) || WorkItemMarkdown.HasUnsupported(html))
{
    // The markdown loses something: show the HTML instead, or warn before writing it back.
}

// Download the images and point the markdown at the local files.
Dictionary<string, string> local = [];

foreach (MarkdownImage picture in WorkItemMarkdown.Images(html))
{
    Result<byte[]> downloaded = await pullRequests.DownloadAttachmentAsync(remote, new Uri(picture.Reference));

    if (downloaded.IsSuccess(out byte[]? bytes))
    {
        string path = Path.Combine(imageFolder, $"{local.Count}.png");
        await File.WriteAllBytesAsync(path, bytes);
        local[picture.Reference] = path;
    }
}

string markdown = WorkItemMarkdown.FromHtml(html, local);
```

## Builds

`IBuildClient`.

```csharp
Result<IReadOnlyList<BuildSummary>> fetched = await builds.GetAsync(remote, limit: 20);

if (!fetched.IsSuccess(out IReadOnlyList<BuildSummary>? recent))
{
    // A token without permission to read builds is Forbidden — the rest of the app can go on.
    Console.WriteLine(fetched.Error);
    return;
}

foreach (BuildSummary build in recent)
{
    Console.WriteLine($"{build.DefinitionName} {build.BuildNumber} on {build.Branch}: {build.Outcome} in {build.Duration}");
}

// The latest build of main, and whether it is green.
BuildSummary? latest = recent.FirstOrDefault(build => build.Branch == "main");
bool green = latest is { Outcome: BuildOutcome.Succeeded };

// Builds that are still running. Status says where a build is in its run, Outcome how it
// went — Outcome reads both, so a running build is Running whatever its partial result.
IEnumerable<BuildSummary> running = recent.Where(build => build.Outcome == BuildOutcome.Running);
IEnumerable<BuildSummary> waiting = recent.Where(build => build.Status == BuildStatus.NotStarted);
```

## Wiki

`IWikiClient`. Page paths start with `/` and are written as in the web interface, with
spaces: `/Development/Getting started`.

```csharp
Result<IReadOnlyList<WikiSummary>> wikis = await wiki.GetWikisAsync(remote);
WikiSummary? projectWiki = wikis.DataOr([]).FirstOrDefault(candidate => candidate.IsProjectWiki);

if (projectWiki is null)
{
    Console.WriteLine(wikis.Error ?? "The project has no wiki.");
    return;
}

// The page tree, from the root or from any page.
Result<WikiPageNode> tree = await wiki.GetPageTreeAsync(remote, projectWiki.Id, "/");

if (tree.IsSuccess(out WikiPageNode? root))
{
    Print(root, depth: 0);
}

static void Print(WikiPageNode node, int depth)
{
    Console.WriteLine($"{new string(' ', depth * 2)}{node.Name}");

    foreach (WikiPageNode child in node.Children)
    {
        Print(child, depth + 1);
    }
}

// A page by path, or by the number in its address (…/_wiki/wikis/Webshop.wiki/17/…).
Result<WikiPage> page = await wiki.GetPageAsync(remote, projectWiki.Id, "/Development/Getting started");
Result<WikiPage> byId = await wiki.GetPageByIdAsync(remote, projectWiki.Id, 17);

if (page.IsSuccess(out WikiPage? found))
{
    Console.WriteLine(found.Markdown);
    Console.WriteLine($"{found.SubPages.Count} sub-pages, {found.WebUri}");
}
else
{
    // NotFound: "The wiki page /Development/Getting started does not exist."
    Console.WriteLine(page.Error);
}

// Search. The hits are marked «like this» in the excerpts.
Result<IReadOnlyList<WikiSearchHit>> hits = await wiki.SearchAsync(remote, "deployment", limit: 10);

foreach (WikiSearchHit hit in hits.DataOr([]))
{
    Console.WriteLine($"{hit.WikiName} {hit.Path}: {string.Join(" … ", hit.Highlights)}");
}
```

## Statuses

Every value with a fixed set of names in the API is an enum, so there are no strings to
remember:

| Where | Enum |
|---|---|
| `PullRequestQuery.Status` | `PullRequestStatusFilter` — `Active`, `Completed`, `Abandoned`, `All` |
| `PullRequestUpdate.Status`, `PullRequestOverview.Status` | `PullRequestStatus` — `Active`, `Completed`, `Abandoned` |
| `PullRequestOverview.MergeStatus` | `PullRequestMergeStatus` |
| `PullRequestThread.Status`, `SetThreadStatusAsync` | `PullRequestThreadStatus` |
| `PullRequestFile.ChangeType` | `PullRequestChangeType` (flags) |
| `PullRequestReviewer.Vote`, `SetReviewerAsync` | `PullRequestVote` |
| `PullRequestCompletion.Strategy` | `PullRequestMergeStrategy` |
| `BuildSummary.Status`, `BuildSummary.Outcome` | `BuildStatus`, `BuildOutcome` |
| `WorkItemRelation.Kind` | `WorkItemLinkKind` |

A value the API adds later falls back to a neutral member (`Unknown`, `NotSet`, `Active`)
instead of failing. `PullRequestThreadStatus.Unknown` can be read but not set.

A work item's type, state and board column stay strings: the process template and the
team define them, so the names differ between projects — `Active` in Agile, `Committed` in
Scrum, `Doing` in Basic.

## Results

No call throws. Every method on the clients answers with a `Result` or `Result<T>` from
[Avig.Result](https://www.nuget.org/packages/Avig.Result): the value on success, and on
failure a message written to be shown to a user as it is, with a `ResultStatus` that says
what kind of failure it was.

| Status | When |
|---|---|
| `Unauthorized` | No token, or the server rejected it (401) |
| `Forbidden` | The token lacks a permission (403), or an attachment address outside the organization |
| `NotFound` | The item does not exist (404): a pull request, a work item, a wiki page, the branch's pull request |
| `Conflict` | The server refused because of the state, for example a branch policy (409) |
| `Invalid` | The request was wrong: a missing argument, an empty title, a status that cannot be set, or 400 |
| `Unavailable` | The network, a timeout, throttling or the server being down (429, 5xx), or no sprints or board |
| `Error` | Anything else the server said |

```csharp
Result<PullRequestOverview> result = await pullRequests.UpdateAsync(
    remote,
    42,
    new PullRequestUpdate(Status: PullRequestStatus.Completed));

if (result.IsFailure(out string? error, out ResultStatus status))
{
    string advice = status switch
    {
        ResultStatus.Unauthorized => "Sign in again.",
        ResultStatus.Conflict => "A policy stops the completion.",
        ResultStatus.NotFound => "No such pull request.",
        ResultStatus.Unavailable => "Try again in a moment.",
        _ => string.Empty,
    };

    Console.WriteLine($"{error} {advice}");
}

// When a fallback is enough:
IReadOnlyList<BuildSummary> list = (await builds.GetAsync(remote)).DataOr([]);
```

The only exception that comes out is `OperationCanceledException`, when the caller's own
`CancellationToken` is cancelled: cancelling is not a failure, and the caller must be able
to tell the two apart. A timeout from `HttpClient` is not a cancellation and comes as
`Unavailable`.

Requests that the server delays (`Retry-After` on a successful response) are delayed
accordingly; a 429 is retried once after the requested wait.

## Building

```powershell
dotnet build Avig.AzureDevOps.slnx
dotnet test Avig.AzureDevOps.slnx
./publish.ps1            # tests, packs and checks the package; changes and copies nothing
```

## Releasing

A release goes to nuget.org through a tag. `release.ps1` sets the version, tests, packs and
checks the package, commits and tags; pushing the tag `vX.Y.Z` runs
`.github/workflows/publish.yml`, which builds it again on a GitHub runner and pushes it with
Trusted Publishing — no API key is stored anywhere.

```powershell
./release.ps1 -DryRun    # which version it would be, nothing changed
./release.ps1            # verified, committed and tagged locally; nothing leaves the machine
./release.ps1 -Push      # the same, pushed: the workflow publishes it
```

The version counts one step per release, every digit 0–9: 1.0.0, 1.0.1 … 1.0.9, 1.1.0 …
1.9.9, 2.0.0. The first release is 1.0.0.
