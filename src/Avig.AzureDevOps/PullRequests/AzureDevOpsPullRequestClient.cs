using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Avig.AzureDevOps.PullRequests;

/// <summary>
/// Pull requests in Azure DevOps, over the REST API.
/// </summary>
/// <remarks>
/// Three things in the API are easy to get wrong and hard to debug, and they are handled
/// here once and for all:
/// <list type="number">
/// <item><c>changeTrackingId</c> must be included, otherwise the comment ends up on the
/// overview tab instead of on the line in the file.</item>
/// <item>The path must have a leading slash.</item>
/// <item>The column counts from one, even though the documentation for 7.1 says zero.</item>
/// </list>
/// </remarks>
public sealed class AzureDevOpsPullRequestClient : IPullRequestClient
{
    /// <summary>No more changed files than this are fetched per call. The API's limit is 2000.</summary>
    private const int ChangePageSize = 2000;

    /// <summary>What is being looked for, for the error messages.</summary>
    private const string What = "the repository or pull request";

    /// <summary>Branch refs start like this; the pull request API wants the full ref.</summary>
    private const string HeadsPrefix = "refs/heads/";

    /// <summary>No more pull requests than this are listed in one call.</summary>
    private const int MaxListed = 500;

    private readonly AzureDevOpsHttp _http;
    private readonly IAzureDevOpsCredential _credential;
    private readonly ILogger<AzureDevOpsPullRequestClient> _logger;

    public AzureDevOpsPullRequestClient(
        HttpClient httpClient,
        IAzureDevOpsCredential credential,
        ILogger<AzureDevOpsPullRequestClient> logger)
    {
        _credential = credential;
        _logger = logger;
        _http = new AzureDevOpsHttp(httpClient, credential, logger);
    }

    public bool IsConnected => _credential.IsConfigured;

    public Task<Result<IReadOnlyList<PullRequestSummary>>> GetOpenAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<PullRequestSummary>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            ListResponse<PullRequestDto> response = await GetAsync<ListResponse<PullRequestDto>>(
                    remote,
                    "pullrequests?searchCriteria.status=active&$top=50",
                    cancellationToken)
                .ConfigureAwait(false);

            return [.. response.Value.Select(dto => ToSummary(remote, dto)).OrderByDescending(pr => pr.CreatedAt)];
        }, cancellationToken);

    public Task<Result<PullRequestSummary>> FindForBranchAsync(
        AzureDevOpsRemote remote,
        string branch,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunResultAsync<PullRequestSummary>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            if (string.IsNullOrWhiteSpace(branch))
            {
                return Result<PullRequestSummary>.NotFound("No branch to look for.");
            }

            // The search wants the full reference, not the short branch name.
            string reference = Uri.EscapeDataString($"{HeadsPrefix}{ShortBranch(branch)}");

            ListResponse<PullRequestDto> response = await GetAsync<ListResponse<PullRequestDto>>(
                    remote,
                    $"pullrequests?searchCriteria.sourceRefName={reference}&searchCriteria.status=active",
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.Value.Count == 0)
            {
                return Result<PullRequestSummary>.NotFound($"The branch {branch} has no open pull request.");
            }

            return ToSummary(remote, response.Value[0]);
        }, cancellationToken);

    public Task<Result<PullRequestSummary>> CreateAsync(
        AzureDevOpsRemote remote,
        NewPullRequest pullRequest,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<PullRequestSummary>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentNullException.ThrowIfNull(pullRequest);

            // The link is made in the same call. A work item that does not exist makes Azure DevOps
            // refuse the whole pull request, which is right: better nothing than a pull request without
            // the link someone asked for. Without work items the field is not sent at all.
            IReadOnlyList<ResourceRefDto>? workItemRefs = null;

            if (pullRequest.WorkItemIds is { Count: > 0 } ids)
            {
                workItemRefs = [.. ids.Distinct().Select(id => new ResourceRefDto { Id = id.ToString(CultureInfo.InvariantCulture) })];
            }

            PullRequestDto created = await PostAsync<CreatePullRequestDto, PullRequestDto>(
                    remote,
                    "pullrequests",
                    new CreatePullRequestDto
                    {
                        SourceRefName = $"{HeadsPrefix}{ShortBranch(pullRequest.SourceBranch)}",
                        TargetRefName = $"{HeadsPrefix}{ShortBranch(pullRequest.TargetBranch)}",
                        Title = pullRequest.Title,
                        Description = pullRequest.Description,
                        IsDraft = pullRequest.IsDraft,
                        WorkItemRefs = workItemRefs,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            Log.PullRequestOpened(_logger, created.PullRequestId, pullRequest.SourceBranch, pullRequest.TargetBranch);

            return ToSummary(remote, created);
        }, cancellationToken);

    public Task<Result<PullRequestContent>> GetContentAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<PullRequestContent>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            PullRequestDto pullRequest = await GetAsync<PullRequestDto>(
                    remote,
                    $"pullrequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}",
                    cancellationToken)
                .ConfigureAwait(false);

            return new PullRequestContent(pullRequest.Title ?? string.Empty, pullRequest.Description ?? string.Empty);
        }, cancellationToken);

    public Task<Result<PullRequestSummary>> UpdateContentAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        PullRequestContent content,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<PullRequestSummary>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentNullException.ThrowIfNull(content);

            // Plain JSON with PATCH, not json-patch as for work items: what is sent
            // is replaced and the rest of the pull request is left as it is.
            PullRequestDto updated = await _http.PostAsync<PullRequestDto>(
                    Route(remote, $"pullrequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}"),
                    JsonSerializer.Serialize(
                        new UpdatePullRequestDto { Title = content.Title, Description = content.Description },
                        AzureDevOpsJson.Options),
                    "application/json",
                    What,
                    cancellationToken,
                    HttpMethod.Patch)
                .ConfigureAwait(false);

            Log.PullRequestUpdated(_logger, pullRequestId);

            return ToSummary(remote, updated);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<int>>> GetLinkedWorkItemIdsAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<int>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            ListResponse<ResourceRefDto> response = await GetAsync<ListResponse<ResourceRefDto>>(
                    remote,
                    $"pullRequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}/workitems",
                    cancellationToken)
                .ConfigureAwait(false);

            return
            [
                .. response.Value
                    .Select(reference => WorkItemId(reference.Id))
                    .Where(id => id > 0)
                    .Distinct()
                    .Order(),
            ];
        }, cancellationToken);

    public Task<Result> LinkWorkItemAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int workItemId,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunVoidAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            // The link is set on the work item and points to the pull request by its artifact id,
            // vstfs:///Git/PullRequestId/{project}%2F{repo}%2F{number}. The pull request API
            // has no call for linking afterwards; this is what the web interface does.
            PullRequestDto pullRequest = await GetAsync<PullRequestDto>(
                    remote,
                    $"pullrequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}",
                    cancellationToken)
                .ConfigureAwait(false);

            string artifact = pullRequest.ArtifactId
                              ?? throw new AzureDevOpsException(
                                  $"Azure DevOps sent no artifact id for pull request {pullRequestId}, so it cannot be linked.");

            LinkOperationDto[] patch =
            [
                new()
                {
                    Value = new ArtifactLinkDto
                    {
                        Url = artifact,
                        Attributes = new ArtifactLinkAttributesDto(),
                    },
                },
            ];

            string id = workItemId.ToString(CultureInfo.InvariantCulture);

            await _http.PostAsync<IdDto>(
                    AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id}"),
                    JsonSerializer.Serialize(patch, AzureDevOpsJson.Options),
                    "application/json-patch+json",
                    $"work item {id}",
                    cancellationToken,
                    HttpMethod.Patch)
                .ConfigureAwait(false);

            Log.PullRequestLinked(_logger, pullRequestId, workItemId);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<int>>> GetBranchWorkItemIdsAsync(
        AzureDevOpsRemote remote,
        string branch,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<int>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            string artifact = await BranchArtifactAsync(remote, branch, cancellationToken).ConfigureAwait(false);

            // The branch itself does not know which work items point to it. The link lives on the work
            // item, and this is the query that goes the other way.
            ArtifactUriQueryResponseDto response = await _http.PostAsync<ArtifactUriQueryDto, ArtifactUriQueryResponseDto>(
                    AzureDevOpsHttp.ProjectRoute(remote, "wit/artifacturiquery"),
                    new ArtifactUriQueryDto { ArtifactUris = [artifact] },
                    "the branch's links",
                    cancellationToken)
                .ConfigureAwait(false);

            // The response is keyed on the address. The query was about a single one, so all values belong
            // to it, and it does not matter if the server spells the address differently in return.
            return
            [
                .. response.Result.Values
                    .SelectMany(references => references)
                    .Select(reference => reference.Id)
                    .Where(id => id > 0)
                    .Distinct()
                    .Order(),
            ];
        }, cancellationToken);

    public Task<Result> LinkBranchAsync(
        AzureDevOpsRemote remote,
        string branch,
        int workItemId,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunVoidAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            string name = ShortBranch(branch);

            // The filter matches the start of the name, so "dev/x" also gives "dev/x-2". Only an exact
            // match counts.
            ListResponse<RefDto> refs = await GetAsync<ListResponse<RefDto>>(
                    remote,
                    $"refs?filter={Uri.EscapeDataString($"heads/{name}")}",
                    cancellationToken)
                .ConfigureAwait(false);

            if (!refs.Value.Any(reference => string.Equals(reference.Name, $"{HeadsPrefix}{name}", StringComparison.Ordinal)))
            {
                throw new AzureDevOpsException(
                    $"The branch {name} does not exist in Azure DevOps. Push it first, otherwise the link leads nowhere.");
            }

            string artifact = await BranchArtifactAsync(remote, name, cancellationToken).ConfigureAwait(false);

            LinkOperationDto[] patch =
            [
                new()
                {
                    Value = new ArtifactLinkDto
                    {
                        Url = artifact,
                        Attributes = new ArtifactLinkAttributesDto { Name = "Branch" },
                    },
                },
            ];

            string id = workItemId.ToString(CultureInfo.InvariantCulture);

            await _http.PostAsync<IdDto>(
                    AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id}"),
                    JsonSerializer.Serialize(patch, AzureDevOpsJson.Options),
                    "application/json-patch+json",
                    $"work item {id}",
                    cancellationToken,
                    HttpMethod.Patch)
                .ConfigureAwait(false);

            Log.BranchLinked(_logger, name, workItemId);
        }, cancellationToken);

    /// <summary>
    /// The branch's artifact id, <c>vstfs:///Git/Ref/{project}%2F{repo}%2FGB{branch}</c>.
    /// </summary>
    /// <remarks>
    /// Unlike a pull request, a branch has no artifact id in the API, so it is built here the
    /// same way the web interface does it: the project's and the repo's ids and the branch
    /// name after <c>GB</c>, encoded as a single segment so that the slashes become
    /// <c>%2F</c>. The ids are GUIDs and must be fetched; the names will not do.
    /// </remarks>
    private async Task<string> BranchArtifactAsync(
        AzureDevOpsRemote remote,
        string branch,
        CancellationToken cancellationToken)
    {
        RepositoryDto repository = await _http.GetAsync<RepositoryDto>(
                AzureDevOpsHttp.ProjectRoute(remote, $"git/repositories/{Uri.EscapeDataString(remote.Repository)}"),
                What,
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(repository.Id) || string.IsNullOrEmpty(repository.Project?.Id))
        {
            throw new AzureDevOpsException(
                $"Azure DevOps sent no id for the repository {remote.Repository}, so the branch cannot be linked.");
        }

        return "vstfs:///Git/Ref/"
               + Uri.EscapeDataString($"{repository.Project.Id}/{repository.Id}/GB{ShortBranch(branch)}");
    }

    public Task<Result<PullRequestDetails>> GetDetailsAsync(
        AzureDevOpsRemote remote,
        PullRequestSummary pullRequest,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<PullRequestDetails>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentNullException.ThrowIfNull(pullRequest);

            ListResponse<IterationDto> iterations = await GetAsync<ListResponse<IterationDto>>(
                    remote,
                    $"pullRequests/{pullRequest.Id}/iterations",
                    cancellationToken)
                .ConfigureAwait(false);

            // Every push adds an iteration; the latest is the one shown.
            int iteration = 1;

            if (iterations.Value.Count > 0)
            {
                iteration = iterations.Value.Max(dto => dto.Id);
            }

            List<PullRequestFile> files = new();
            int skip = 0;

            while (true)
            {
                IterationChangesDto page = await GetAsync<IterationChangesDto>(
                        remote,
                        $"pullRequests/{pullRequest.Id}/iterations/{iteration}/changes"
                        + $"?$top={ChangePageSize}&$skip={skip}",
                        cancellationToken)
                    .ConfigureAwait(false);

                files.AddRange(page.ChangeEntries
                    .Where(entry => !string.IsNullOrEmpty(entry.Item?.Path))
                    .Select(entry => new PullRequestFile(
                        entry.Item!.Path!,
                        entry.ChangeTrackingId,
                        ApiEnum.Parse(entry.ChangeType, PullRequestChangeType.Edit))));

                if (page.NextSkip is not { } next || page.ChangeEntries.Count == 0)
                {
                    break;
                }

                skip = next;
            }

            return new PullRequestDetails(pullRequest, iteration, files);
        }, cancellationToken);

    public Task<Result<byte[]>> DownloadAttachmentAsync(
        AzureDevOpsRemote remote,
        Uri url,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunResultAsync<byte[]>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentNullException.ThrowIfNull(url);

            // Only the same Azure DevOps the review already talks to. A comment can
            // contain any link at all, and the tool should not fetch from a foreign
            // host just because someone wrote the address — nor may the sign-in go
            // along there.
            if (!url.IsAbsoluteUri
                || !url.Host.Equals(remote.ApiBaseUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                return Result<byte[]>.Forbidden($"{url} does not belong to the repository's Azure DevOps.");
            }

            using HttpRequestMessage request = new(HttpMethod.Get, url);

            // Without this the response describes the attachment instead of being it.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // An image that cannot be fetched is not worth aborting the whole fetch for.
                // The comment is readable anyway, and the link remains in it.
                Log.AttachmentUnavailable(_logger, url.AbsoluteUri, (int)response.StatusCode);

                return Result<byte[]>.Unavailable($"The server responded {(int)response.StatusCode} to {url}.");
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<PullRequestThread>>> GetThreadsAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int iteration,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<PullRequestThread>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            ListResponse<ThreadDto> response = await GetAsync<ListResponse<ThreadDto>>(
                    remote,
                    $"pullRequests/{pullRequestId}/threads?$iteration={iteration}",
                    cancellationToken)
                .ConfigureAwait(false);

            return
            [
                .. response.Value
                    .Where(thread => !thread.IsDeleted)
                    .Select(ToThread)
                    .Where(thread => thread.Comments.Count > 0),
            ];
        }, cancellationToken);

    public Task<Result<PullRequestThread>> CreateThreadAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int iteration,
        string text,
        PullRequestAnchor? anchor,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<PullRequestThread>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentException.ThrowIfNullOrWhiteSpace(text);

            // Without an anchor the thread becomes a general comment. With an anchor but without
            // changeTrackingId it ends up on the overview tab instead of on the line.
            ThreadContextDto? threadContext = null;
            PullRequestThreadContextDto? pullRequestContext = null;

            if (anchor is not null)
            {
                threadContext = ToContext(anchor);
            }

            if (anchor?.ChangeTrackingId is { } trackingId)
            {
                pullRequestContext = new PullRequestThreadContextDto
                {
                    ChangeTrackingId = trackingId,
                    IterationContext = new IterationContextDto
                    {
                        FirstComparingIteration = 1,
                        SecondComparingIteration = iteration,
                    },
                };
            }

            CreateThreadDto body = new()
            {
                Comments = [new CommentDto { ParentCommentId = 0, Content = text, CommentType = "text" }],
                Status = "active",
                ThreadContext = threadContext,
                PullRequestThreadContext = pullRequestContext,
            };

            ThreadDto created = await PostAsync<CreateThreadDto, ThreadDto>(
                    remote,
                    $"pullRequests/{pullRequestId}/threads",
                    body,
                    cancellationToken)
                .ConfigureAwait(false);

            Log.PublishedThread(_logger, pullRequestId, created.Id);

            return ToThread(created);
        }, cancellationToken);

    public Task<Result<PullRequestComment>> ReplyAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int threadId,
        string text,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<PullRequestComment>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentException.ThrowIfNullOrWhiteSpace(text);

            CommentDto created = await PostAsync<CommentDto, CommentDto>(
                    remote,
                    $"pullRequests/{pullRequestId}/threads/{threadId}/comments",
                    new CommentDto { ParentCommentId = 1, Content = text, CommentType = "text" },
                    cancellationToken)
                .ConfigureAwait(false);

            return ToComment(created);
        }, cancellationToken);

    public Task<Result> SetThreadStatusAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int threadId,
        PullRequestThreadStatus status,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunVoidAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            UpdateThreadDto body = new() { Status = ToApiStatus(status) };

            using HttpRequestMessage request = new(
                HttpMethod.Patch,
                Route(remote, $"pullRequests/{pullRequestId}/threads/{threadId}"))
            {
                Content = JsonContent.Create(body, options: AzureDevOpsJson.Options),
            };

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            await AzureDevOpsHttp.EnsureSuccessAsync(response, What, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<PullRequestOverview>>> ListAsync(
        AzureDevOpsRemote remote,
        PullRequestQuery query,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<PullRequestOverview>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentNullException.ThrowIfNull(query);

            string status = ApiEnum.Name(query.Status);

            string top = Math.Clamp(query.Top, 1, MaxListed).ToString(CultureInfo.InvariantCulture);
            string relative = $"pullrequests?searchCriteria.status={status}&$top={top}";

            if (!string.IsNullOrWhiteSpace(query.CreatorId))
            {
                relative += "&searchCriteria.creatorId=" + Uri.EscapeDataString(query.CreatorId);
            }

            if (!string.IsNullOrWhiteSpace(query.ReviewerId))
            {
                relative += "&searchCriteria.reviewerId=" + Uri.EscapeDataString(query.ReviewerId);
            }

            ListResponse<PullRequestDto> response = await GetAsync<ListResponse<PullRequestDto>>(remote, relative, cancellationToken)
                .ConfigureAwait(false);

            return [.. response.Value.Select(dto => ToOverview(remote, dto)).OrderByDescending(pr => pr.Summary.CreatedAt)];
        }, cancellationToken);

    public Task<Result<PullRequestOverview>> GetOverviewAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<PullRequestOverview>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            PullRequestDto dto = await GetAsync<PullRequestDto>(
                    remote,
                    $"pullrequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}",
                    cancellationToken)
                .ConfigureAwait(false);

            return ToOverview(remote, dto);
        }, cancellationToken);

    public Task<Result<PullRequestOverview>> UpdateAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        PullRequestUpdate update,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<PullRequestOverview>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentNullException.ThrowIfNull(update);

            PullRequestStateDto body = ToStateDto(update);

            PullRequestDto updated = await _http.PostAsync<PullRequestDto>(
                    Route(remote, $"pullrequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}"),
                    JsonSerializer.Serialize(body, AzureDevOpsJson.Options),
                    "application/json",
                    What,
                    cancellationToken,
                    HttpMethod.Patch)
                .ConfigureAwait(false);

            Log.PullRequestStateChanged(_logger, pullRequestId, updated.Status ?? "?", updated.IsDraft);

            return ToOverview(remote, updated);
        }, cancellationToken);

    /// <summary>
    /// The change in the form the server wants it. Whatever is <c>null</c> is not sent, and is
    /// therefore left as it is.
    /// </summary>
    private static PullRequestStateDto ToStateDto(PullRequestUpdate update)
    {
        string? status = null;
        CommitRefDto? lastMergeSourceCommit = null;
        CompletionOptionsDto? completionOptions = null;
        IdentityRef? autoCompleteSetBy = null;

        if (update.Status is { } newStatus)
        {
            status = ApiEnum.Name(newStatus);
        }

        if (update.LastMergeSourceCommitId is { } commit)
        {
            lastMergeSourceCommit = new CommitRefDto { CommitId = commit };
        }

        if (update.Completion is { } completion)
        {
            completionOptions = new CompletionOptionsDto
            {
                MergeStrategy = MergeStrategyName(completion.Strategy),
                DeleteSourceBranch = completion.DeleteSourceBranch,
                MergeCommitMessage = completion.MergeCommitMessage,
                TransitionWorkItems = completion.TransitionWorkItems,
            };
        }

        if (update.AutoCompleteSetById is { } setBy)
        {
            autoCompleteSetBy = new IdentityRef { Id = setBy };
        }

        return new PullRequestStateDto
        {
            Status = status,
            IsDraft = update.IsDraft,
            LastMergeSourceCommit = lastMergeSourceCommit,
            CompletionOptions = completionOptions,
            AutoCompleteSetBy = autoCompleteSetBy,
        };
    }

    public Task<Result<AzureDevOpsIdentity>> GetCurrentUserAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<AzureDevOpsIdentity>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            // At organization level, not under the project: the question concerns the sign-in.
            ConnectionDataDto data = await _http.GetAsync<ConnectionDataDto>(
                    new Uri(remote.ApiBaseUri, "_apis/connectionData?api-version=7.1-preview"),
                    "the sign-in",
                    cancellationToken)
                .ConfigureAwait(false);

            if (data.AuthenticatedUser is not { Id: { Length: > 0 } id } user)
            {
                throw new AzureDevOpsException("Azure DevOps did not say who the token belongs to.");
            }

            return new AzureDevOpsIdentity(
                id,
                user.ProviderDisplayName ?? string.Empty,
                Property(user.Properties, "Account").DataOr(string.Empty));
        }, cancellationToken);

    public Task<Result<IReadOnlyList<AzureDevOpsIdentity>>> FindIdentitiesAsync(
        AzureDevOpsRemote remote,
        string text,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<AzureDevOpsIdentity>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentException.ThrowIfNullOrWhiteSpace(text);

            // The identities live at vssps and not under dev.azure.com. It is the same
            // organization and the same sign-in, just another host.
            Uri route = new(
                $"https://vssps.dev.azure.com/{Uri.EscapeDataString(remote.Organization)}/_apis/identities"
                + $"?searchFilter=General&filterValue={Uri.EscapeDataString(text.Trim())}&queryMembership=None"
                + $"&api-version={AzureDevOpsJson.ApiVersion}");

            ListResponse<IdentityDto> response = await _http.GetAsync<ListResponse<IdentityDto>>(
                    route,
                    $"the identity \"{text}\"",
                    cancellationToken)
                .ConfigureAwait(false);

            return
            [
                .. response.Value
                    .Where(identity => identity.IsActive && !string.IsNullOrEmpty(identity.Id))
                    .Select(identity => new AzureDevOpsIdentity(
                        identity.Id!,
                        identity.ProviderDisplayName ?? string.Empty,
                        Property(identity.Properties, "Account")
                            .DataOr(Property(identity.Properties, "Mail").DataOr(string.Empty)))),
            ];
        }, cancellationToken);

    public Task<Result<PullRequestReviewer>> SetReviewerAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        string reviewerId,
        PullRequestVote vote,
        bool isRequired,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<PullRequestReviewer>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentException.ThrowIfNullOrWhiteSpace(reviewerId);

            // PUT replaces the reviewer entirely, so both vote and requirement are always sent — whatever
            // is left out is otherwise reset.
            using HttpRequestMessage request = new(HttpMethod.Put, ReviewerRoute(remote, pullRequestId, reviewerId))
            {
                Content = JsonContent.Create(
                    new SetReviewerDto { Vote = (int)vote, IsRequired = isRequired },
                    options: AzureDevOpsJson.Options),
            };

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            ReviewerDto reviewer = await AzureDevOpsHttp.ReadAsync<ReviewerDto>(response, What, cancellationToken).ConfigureAwait(false);

            Log.ReviewerSet(_logger, pullRequestId, reviewer.DisplayName ?? reviewerId, reviewer.Vote);

            return ToReviewer(reviewer);
        }, cancellationToken);

    public Task<Result> RemoveReviewerAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        string reviewerId,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunVoidAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentException.ThrowIfNullOrWhiteSpace(reviewerId);

            using HttpRequestMessage request = new(HttpMethod.Delete, ReviewerRoute(remote, pullRequestId, reviewerId));
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            await AzureDevOpsHttp.EnsureSuccessAsync(response, What, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static Uri ReviewerRoute(AzureDevOpsRemote remote, int pullRequestId, string reviewerId) => Route(
        remote,
        $"pullRequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}/reviewers/{Uri.EscapeDataString(reviewerId)}");

    public Task<Result<bool>> UnlinkWorkItemAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int workItemId,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<bool>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            PullRequestDto pullRequest = await GetAsync<PullRequestDto>(
                    remote,
                    $"pullrequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}",
                    cancellationToken)
                .ConfigureAwait(false);

            string artifact = pullRequest.ArtifactId
                              ?? throw new AzureDevOpsException(
                                  $"Azure DevOps sent no artifact id for pull request {pullRequestId}.");

            return await RemoveArtifactLinkAsync(remote, workItemId, artifact, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<Result<bool>> UnlinkBranchAsync(
        AzureDevOpsRemote remote,
        string branch,
        int workItemId,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<bool>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            string artifact = await BranchArtifactAsync(remote, branch, cancellationToken).ConfigureAwait(false);

            return await RemoveArtifactLinkAsync(remote, workItemId, artifact, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Removes the link from a work item to an artifact — a branch or a pull request.
    /// </summary>
    /// <remarks>
    /// The link lives on the work item and is removed by its index, so the revision is sent
    /// along: if someone else has managed to change the links in the meantime, the server
    /// refuses rather than removing the wrong link. The address is compared decoded, because
    /// the server does not always encode the slashes the same way as when the link was created.
    /// </remarks>
    private async Task<bool> RemoveArtifactLinkAsync(
        AzureDevOpsRemote remote,
        int workItemId,
        string artifact,
        CancellationToken cancellationToken)
    {
        string id = workItemId.ToString(CultureInfo.InvariantCulture);

        WorkItemWithRelationsDto workItem = await _http.GetAsync<WorkItemWithRelationsDto>(
                AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id}?$expand=relations"),
                $"work item {id}",
                cancellationToken)
            .ConfigureAwait(false);

        string wanted = Uri.UnescapeDataString(artifact);

        int[] indexes =
        [
            .. workItem.Relations
                .Select((relation, index) => (relation, index))
                .Where(pair =>
                    string.Equals(pair.relation.Rel, "ArtifactLink", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Uri.UnescapeDataString(pair.relation.Url), wanted, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.index)
                .OrderDescending(),
        ];

        if (indexes.Length == 0)
        {
            return false;
        }

        List<object> patch = [new { op = "test", path = "/rev", value = workItem.Rev }];
        patch.AddRange(indexes.Select(index =>
            new { op = "remove", path = "/relations/" + index.ToString(CultureInfo.InvariantCulture) }));

        await _http.PostAsync<IdDto>(
                AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id}"),
                JsonSerializer.Serialize(patch, AzureDevOpsJson.Options),
                "application/json-patch+json",
                $"work item {id}",
                cancellationToken,
                HttpMethod.Patch)
            .ConfigureAwait(false);

        Log.ArtifactUnlinked(_logger, workItemId, artifact);

        return true;
    }

    // ------------------------------------------------------------------ translation

    private static PullRequestOverview ToOverview(AzureDevOpsRemote remote, PullRequestDto dto)
    {
        // Auto-complete is turned off by setting the empty GUID, so an id
        // is not enough for it to be on.
        bool autoComplete = dto.AutoCompleteSetBy?.Id is { Length: > 0 } setBy
                            && (Guid.TryParse(setBy, out Guid setById) == false || setById != Guid.Empty);

        // A pull request that has never been closed has DateTime.MinValue, not null.
        DateTime? closedAt = null;

        if (dto.ClosedDate is { Year: > 1 } closed)
        {
            closedAt = closed;
        }

        return new PullRequestOverview(
            ToSummary(remote, dto),
            ApiEnum.Parse(dto.Status, PullRequestStatus.Active),
            dto.IsDraft,
            dto.Description ?? string.Empty,
            ApiEnum.Parse(dto.MergeStatus, PullRequestMergeStatus.NotSet),
            dto.LastMergeSourceCommit?.CommitId,
            [.. dto.Reviewers.Select(ToReviewer)],
            autoComplete,
            closedAt);
    }

    private static PullRequestReviewer ToReviewer(ReviewerDto dto) => new(
        new AzureDevOpsIdentity(
            dto.Id ?? string.Empty,
            dto.DisplayName ?? dto.UniqueName ?? AzureDevOpsIdentity.UnknownName,
            dto.UniqueName ?? string.Empty),
        ToVote(dto.Vote),
        dto.IsRequired,
        dto.IsContainer);

    /// <summary>The vote, or <see cref="PullRequestVote.None"/> for a value the API has added.</summary>
    private static PullRequestVote ToVote(int vote)
    {
        if (Enum.IsDefined(typeof(PullRequestVote), vote))
        {
            return (PullRequestVote)vote;
        }

        return PullRequestVote.None;
    }

    private static string MergeStrategyName(PullRequestMergeStrategy strategy) => strategy switch
    {
        PullRequestMergeStrategy.Squash => "squash",
        PullRequestMergeStrategy.Rebase => "rebase",
        PullRequestMergeStrategy.RebaseMerge => "rebaseMerge",
        _ => "noFastForward",
    };

    /// <summary>
    /// A value from an identity's properties, which have the form
    /// <c>{"Account": {"$type": "System.String", "$value": "…"}}</c>.
    /// </summary>
    private static Result<string> Property(JsonElement properties, string name)
    {
        if (properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Object
            && property.TryGetProperty("$value", out JsonElement value)
            && value.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        return Result<string>.NotFound($"The identity has no {name}.");
    }

    private static PullRequestSummary ToSummary(AzureDevOpsRemote remote, PullRequestDto dto) => new(
        dto.PullRequestId,
        dto.Title ?? $"Pull request {dto.PullRequestId}",
        dto.CreatedBy?.DisplayName ?? dto.CreatedBy?.UniqueName ?? AzureDevOpsIdentity.UnknownName,
        ShortBranch(dto.SourceRefName),
        ShortBranch(dto.TargetRefName),
        dto.CreationDate,
        new Uri($"{remote.WebUri.AbsoluteUri}/pullrequest/{dto.PullRequestId}"));

    private static PullRequestThread ToThread(ThreadDto dto) => new(
        dto.Id,
        dto.ThreadContext?.FilePath,
        dto.ThreadContext?.RightFileStart?.Line ?? dto.ThreadContext?.LeftFileStart?.Line,
        ApiEnum.Parse(dto.Status, PullRequestThreadStatus.Unknown),
        [
            .. dto.Comments
                // The system's own messages — merge attempts, votes, branch changes —
                // are not review and do not belong in the list.
                .Where(comment => !comment.IsDeleted
                                  && !string.Equals(comment.CommentType, "system", StringComparison.OrdinalIgnoreCase))
                .Select(ToComment),
        ]);

    private static PullRequestComment ToComment(CommentDto dto) => new(
        dto.Id,
        dto.Author?.DisplayName ?? dto.Author?.UniqueName ?? AzureDevOpsIdentity.UnknownName,
        dto.Content ?? string.Empty,
        dto.PublishedDate);

    private static ThreadContextDto ToContext(PullRequestAnchor anchor) => new()
    {
#pragma warning disable S1075
        // Azure DevOps wants the path from the repo root with a leading slash.
        FilePath = "/" + anchor.FilePath.Replace('\\', '/').TrimStart('/'),
#pragma warning restore S1075

        // The column counts from one. The whole line is marked by ending far out;
        // the server clips to the line's actual length.
        RightFileStart = new CommentPositionDto { Line = anchor.StartLine, Offset = 1 },
        RightFileEnd = new CommentPositionDto { Line = anchor.EndLine, Offset = 1 },
    };

    private static string ToApiStatus(PullRequestThreadStatus status)
    {
        if (status == PullRequestThreadStatus.Unknown || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A thread cannot be given an unknown status.");
        }

        return ApiEnum.Name(status);
    }

    private static string ShortBranch(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return string.Empty;
        }

        if (reference.StartsWith(HeadsPrefix, StringComparison.Ordinal))
        {
            return reference[HeadsPrefix.Length..];
        }

        return reference;
    }

    /// <summary>A work item's number from a reference, or <c>0</c> when it is not a number.</summary>
    private static int WorkItemId(string? reference)
    {
        if (int.TryParse(reference, CultureInfo.InvariantCulture, out int id))
        {
            return id;
        }

        return 0;
    }

    // ------------------------------------------------------------------ transport

    /// <summary>
    /// The address of an API under the repo.
    /// </summary>
    /// <remarks>
    /// The rest — sign-in, throttling and error messages — lives in
    /// <see cref="AzureDevOpsHttp"/> together with work items and builds. The only thing
    /// specific to this one is that the path goes through <c>git/repositories</c>.
    /// </remarks>
    private static Uri Route(AzureDevOpsRemote remote, string relative) => AzureDevOpsHttp.ProjectRoute(
        remote,
        $"git/repositories/{Uri.EscapeDataString(remote.Repository)}/{relative}");

    private Task<T> GetAsync<T>(AzureDevOpsRemote remote, string relative, CancellationToken cancellationToken) =>
        _http.GetAsync<T>(Route(remote, relative), What, cancellationToken);

    private Task<TResult> PostAsync<TBody, TResult>(
        AzureDevOpsRemote remote,
        string relative,
        TBody body,
        CancellationToken cancellationToken) =>
        _http.PostAsync<TBody, TResult>(Route(remote, relative), body, What, cancellationToken);
}
