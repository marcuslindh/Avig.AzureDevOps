using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Avig.AzureDevOps.Identities;
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
/// <remarks>A client that sends its requests through <paramref name="httpClient"/>.</remarks>
/// <param name="httpClient">The typed client from the factory, with User-Agent and timeout set.</param>
/// <param name="credential">The sign-in every request is authorized with.</param>
/// <param name="logger">The log.</param>
public sealed class AzureDevOpsPullRequestClient(
    HttpClient httpClient,
    IAzureDevOpsCredential credential,
    ILogger<AzureDevOpsPullRequestClient> logger) : IPullRequestClient
{
    /// <summary>No more changed files than this are fetched per call. The API's limit is 2000.</summary>
    private const int ChangePageSize = 2000;

    /// <summary>What is being looked for, for the error messages.</summary>
    private const string What = "the repository or pull request";

    /// <summary>Branch refs start like this; the pull request API wants the full ref.</summary>
    private const string HeadsPrefix = "refs/heads/";

    /// <summary>No more pull requests than this are listed in one call.</summary>
    private const int MaxListed = 500;

    /// <summary>The media type a work item's links are changed with.</summary>
    private const string JsonPatch = "application/json-patch+json";

    /// <summary>The media type a pull request is changed with.</summary>
    private const string Json = "application/json";

    /// <summary>The answer when no repository is given.</summary>
    private const string NoRepository =
        "No repository was given. Pass the organization, project and repository, for example from AzureDevOpsRemote.TryParse.";

    /// <summary>The answer when no reviewer is given.</summary>
    private const string NoReviewer = "No reviewer was given. Pass the reviewer's id, from AzureDevOpsIdentity.Id.";

    private readonly AzureDevOpsHttp _http = new AzureDevOpsHttp(httpClient, credential, logger);
    private readonly IAzureDevOpsCredential _credential = credential;
    private readonly ILogger<AzureDevOpsPullRequestClient> _logger = logger;

    /// <summary>Whether the tool has a sign-in to work with.</summary>
    public bool IsConnected => _credential.IsConfigured;

    /// <summary>Open pull requests in the repo, newest first.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<IReadOnlyList<PullRequestSummary>>> GetOpenAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<PullRequestSummary>>.Invalid(NoRepository);
        }

        Result<ListResponse<PullRequestDto>> response = await GetAsync<ListResponse<PullRequestDto>>(
            remote,
            "pullrequests?searchCriteria.status=active&$top=50",
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out ListResponse<PullRequestDto>? list))
        {
            return response.WithoutValue().AsFailure<IReadOnlyList<PullRequestSummary>>();
        }

        return Result<IReadOnlyList<PullRequestSummary>>.Success(
            [.. list.Value.Select(dto => ToSummary(remote, dto)).OrderByDescending(pr => pr.CreatedAt)]);
    }

    /// <summary>
    /// The open pull request for a branch, or <see cref="ResultStatus.NotFound"/> when the branch
    /// has none, and <see cref="ResultStatus.Invalid"/> when no branch is given.
    /// </summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="branch">The branch, with or without <c>refs/heads/</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<PullRequestSummary>> FindForBranchAsync(
        AzureDevOpsRemote remote,
        string branch,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<PullRequestSummary>.Invalid(NoRepository);
        }

        // No branch is a mistake in the call, not an answer from the server: Invalid, not NotFound.
        if (string.IsNullOrWhiteSpace(branch))
        {
            return Result<PullRequestSummary>.Failure(
                "No branch name was given, so there is no pull request to look for. "
                    + "Pass the branch the pull request comes from, for example feature/checkout.",
                ResultStatus.Invalid);
        }

        // The search wants the full reference, not the short branch name.
        string reference = Uri.EscapeDataString($"{HeadsPrefix}{ShortBranch(branch)}");

        Result<ListResponse<PullRequestDto>> response = await GetAsync<ListResponse<PullRequestDto>>(
            remote,
            $"pullrequests?searchCriteria.sourceRefName={reference}&searchCriteria.status=active",
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out ListResponse<PullRequestDto>? list))
        {
            return response.WithoutValue().AsFailure<PullRequestSummary>();
        }

        if (list.Value.Count == 0)
        {
            return Result<PullRequestSummary>.NotFound($"The branch {branch} has no open pull request.");
        }

        return ToSummary(remote, list.Value[0]);
    }

    /// <summary>Creates a pull request and returns it as it was created.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequest">The pull request to create.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<PullRequestSummary>> CreateAsync(
        AzureDevOpsRemote remote,
        NewPullRequest pullRequest,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<PullRequestSummary>.Invalid(NoRepository);
        }

        if (pullRequest is null)
        {
            return Result<PullRequestSummary>.Invalid(
                "No pull request was given. Pass the source and target branches, the title and the description.");
        }

        // The link is made in the same call. A work item that does not exist makes Azure DevOps
        // refuse the whole pull request, which is right: better nothing than a pull request without
        // the link someone asked for. Without work items the field is not sent at all.
        IReadOnlyList<ResourceRefDto>? workItemRefs = null;

        if (pullRequest.WorkItemIds is { Count: > 0 } ids)
        {
            workItemRefs = [.. ids.Distinct().Select(id => new ResourceRefDto { Id = id.ToString(CultureInfo.InvariantCulture) })];
        }

        Result<PullRequestDto> response = await PostAsync<CreatePullRequestDto, PullRequestDto>(
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

        if (!response.IsSuccess(out PullRequestDto? created))
        {
            return response.WithoutValue().AsFailure<PullRequestSummary>();
        }

        Log.PullRequestOpened(_logger, created.PullRequestId, pullRequest.SourceBranch, pullRequest.TargetBranch);

        return ToSummary(remote, created);
    }

    /// <summary>The title and the description as they currently stand in Azure DevOps.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<PullRequestContent>> GetContentAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<PullRequestContent>.Invalid(NoRepository);
        }

        Result<PullRequestDto> response = await GetPullRequestAsync(remote, pullRequestId, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out PullRequestDto? pullRequest))
        {
            return response.WithoutValue().AsFailure<PullRequestContent>();
        }

        return new PullRequestContent(pullRequest.Title ?? string.Empty, pullRequest.Description ?? string.Empty);
    }

    /// <summary>Writes a new title and description and returns the resulting pull request.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="content">The new title and description.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<PullRequestSummary>> UpdateContentAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        PullRequestContent content,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<PullRequestSummary>.Invalid(NoRepository);
        }

        if (content is null)
        {
            return Result<PullRequestSummary>.Invalid(
                "No title and description were given. Pass the pull request's new content.");
        }

        // Plain JSON with PATCH, not json-patch as for work items: what is sent
        // is replaced and the rest of the pull request is left as it is.
        Result<PullRequestDto> response = await _http.PostAsync<PullRequestDto>(
            Route(remote, $"pullrequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}"),
            JsonSerializer.Serialize(
                new UpdatePullRequestDto { Title = content.Title, Description = content.Description },
                AzureDevOpsJson.Options),
            Json,
            What,
            cancellationToken,
            HttpMethod.Patch)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out PullRequestDto? updated))
        {
            return response.WithoutValue().AsFailure<PullRequestSummary>();
        }

        Log.PullRequestUpdated(_logger, pullRequestId);

        return ToSummary(remote, updated);
    }

    /// <summary>The numbers of the work items the pull request is linked to.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<IReadOnlyList<int>>> GetLinkedWorkItemIdsAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<int>>.Invalid(NoRepository);
        }

        Result<ListResponse<ResourceRefDto>> response = await GetAsync<ListResponse<ResourceRefDto>>(
            remote,
            $"pullRequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}/workitems",
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out ListResponse<ResourceRefDto>? list))
        {
            return response.WithoutValue().AsFailure<IReadOnlyList<int>>();
        }

        return Result<IReadOnlyList<int>>.Success(
        [
            .. list.Value
                .Select(reference => WorkItemId(reference.Id))
                .Where(id => id > 0)
                .Distinct()
                .Order(),
        ]);
    }

    /// <summary>
    /// Links the pull request to a work item, so that it appears under <em>Development</em>
    /// in the work item, and the work item appears in the pull request.
    /// </summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="workItemId">The work item's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result> LinkWorkItemAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int workItemId,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result.Invalid(NoRepository);
        }

        // The link is set on the work item and points to the pull request by its artifact id,
        // vstfs:///Git/PullRequestId/{project}%2F{repo}%2F{number}. The pull request API
        // has no call for linking afterwards; this is what the web interface does.
        Result<string> artifact = await PullRequestArtifactAsync(
            remote,
            pullRequestId,
            $"Azure DevOps sent no artifact id for pull request {pullRequestId}, so it cannot be linked.",
            cancellationToken)
            .ConfigureAwait(false);

        if (!artifact.IsSuccess(out string? url))
        {
            return artifact.WithoutValue();
        }

        Result linked = await AddArtifactLinkAsync(remote, workItemId, new ArtifactLinkAttributesDto(), url, cancellationToken)
            .ConfigureAwait(false);

        if (linked.IsFailure(out _, out _))
        {
            return linked;
        }

        Log.PullRequestLinked(_logger, pullRequestId, workItemId);

        return Result.Success();
    }

    /// <summary>The numbers of the work items a branch is linked to.</summary>
    /// <param name="remote">The repo the branch is in.</param>
    /// <param name="branch">The branch, with or without <c>refs/heads/</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<IReadOnlyList<int>>> GetBranchWorkItemIdsAsync(
        AzureDevOpsRemote remote,
        string branch,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<int>>.Invalid(NoRepository);
        }

        Result<string> artifact = await BranchArtifactAsync(remote, branch, cancellationToken).ConfigureAwait(false);

        if (!artifact.IsSuccess(out string? url))
        {
            return artifact.WithoutValue().AsFailure<IReadOnlyList<int>>();
        }

        // The branch itself does not know which work items point to it. The link lives on the work
        // item, and this is the query that goes the other way.
        Result<ArtifactUriQueryResponseDto> response = await _http.PostAsync<ArtifactUriQueryDto, ArtifactUriQueryResponseDto>(
            AzureDevOpsHttp.ProjectRoute(remote, "wit/artifacturiquery"),
            new ArtifactUriQueryDto { ArtifactUris = [url] },
            "the branch's links",
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out ArtifactUriQueryResponseDto? links))
        {
            return response.WithoutValue().AsFailure<IReadOnlyList<int>>();
        }

        // The response is keyed on the address. The query was about a single one, so all values belong
        // to it, and it does not matter if the server spells the address differently in return.
        return Result<IReadOnlyList<int>>.Success(
        [
            .. links.Result.Values
                .SelectMany(references => references)
                .Select(reference => reference.Id)
                .Where(id => id > 0)
                .Distinct()
                .Order(),
        ]);
    }

    /// <summary>
    /// Links a branch to a work item, so that the branch appears under <em>Development</em>
    /// in the work item.
    /// </summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="branch">The branch, with or without <c>refs/heads/</c>.</param>
    /// <param name="workItemId">The work item's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// The branch must exist in Azure DevOps. A branch that exists only locally can be linked,
    /// but the link leads nowhere, so the implementation refuses instead — with a
    /// message saying that the branch needs to be pushed.
    /// </remarks>
    public async Task<Result> LinkBranchAsync(
        AzureDevOpsRemote remote,
        string branch,
        int workItemId,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result.Invalid(NoRepository);
        }

        string name = ShortBranch(branch);

        // The filter matches the start of the name, so "dev/x" also gives "dev/x-2". Only an exact
        // match counts.
        Result<ListResponse<RefDto>> refs = await GetAsync<ListResponse<RefDto>>(
            remote,
            $"refs?filter={Uri.EscapeDataString($"heads/{name}")}",
            cancellationToken)
            .ConfigureAwait(false);

        if (!refs.IsSuccess(out ListResponse<RefDto>? found))
        {
            return refs.WithoutValue();
        }

        if (!found.Value.Any(reference => string.Equals(reference.Name, $"{HeadsPrefix}{name}", StringComparison.Ordinal)))
        {
            return Result.Failure(
                $"The branch {name} does not exist in Azure DevOps. Push it first, otherwise the link leads nowhere.",
                ResultStatus.Error);
        }

        Result<string> artifact = await BranchArtifactAsync(remote, name, cancellationToken).ConfigureAwait(false);

        if (!artifact.IsSuccess(out string? url))
        {
            return artifact.WithoutValue();
        }

        Result linked = await AddArtifactLinkAsync(
            remote,
            workItemId,
            new ArtifactLinkAttributesDto { Name = "Branch" },
            url,
            cancellationToken)
            .ConfigureAwait(false);

        if (linked.IsFailure(out _, out _))
        {
            return linked;
        }

        Log.BranchLinked(_logger, name, workItemId);

        return Result.Success();
    }

    /// <summary>Adds a link from a work item to an artifact — a branch or a pull request.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="workItemId">The work item's number.</param>
    /// <param name="attributes">The link's attributes; a branch's link is named <c>Branch</c>.</param>
    /// <param name="artifact">The artifact's address, as the link stores it: <c>vstfs:///Git/…</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Success, or why the work item did not take the link.</returns>
    private async Task<Result> AddArtifactLinkAsync(
        AzureDevOpsRemote remote,
        int workItemId,
        ArtifactLinkAttributesDto attributes,
        string artifact,
        CancellationToken cancellationToken)
    {
        LinkOperationDto[] patch =
        [
            new()
            {
                Value = new ArtifactLinkDto
                {
                    Url = artifact,
                    Attributes = attributes,
                },
            },
        ];

        string id = workItemId.ToString(CultureInfo.InvariantCulture);

        Result<IdDto> response = await _http.PostAsync<IdDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id}"),
            JsonSerializer.Serialize(patch, AzureDevOpsJson.Options),
            JsonPatch,
            $"work item {id}",
            cancellationToken,
            HttpMethod.Patch)
            .ConfigureAwait(false);

        return response.WithoutValue();
    }

    /// <summary>
    /// The branch's artifact id, <c>vstfs:///Git/Ref/{project}%2F{repo}%2FGB{branch}</c>.
    /// </summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="branch">The branch, with or without <c>refs/heads/</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The artifact id, or why there is none: the repository could not be read, or Azure DevOps
    /// sent no id for the repository or its project.
    /// </returns>
    /// <remarks>
    /// Unlike a pull request, a branch has no artifact id in the API, so it is built here the
    /// same way the web interface does it: the project's and the repo's ids and the branch
    /// name after <c>GB</c>, encoded as a single segment so that the slashes become
    /// <c>%2F</c>. The ids are GUIDs and must be fetched; the names will not do.
    /// </remarks>
    private async Task<Result<string>> BranchArtifactAsync(
        AzureDevOpsRemote remote,
        string branch,
        CancellationToken cancellationToken)
    {
        Result<RepositoryDto> response = await _http.GetAsync<RepositoryDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"git/repositories/{Uri.EscapeDataString(remote.Repository)}"),
            What,
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out RepositoryDto? repository))
        {
            return response.WithoutValue().AsFailure<string>();
        }

        if (string.IsNullOrEmpty(repository.Id) || string.IsNullOrEmpty(repository.Project?.Id))
        {
            return Result<string>.Failure(
                $"Azure DevOps sent no id for the repository {remote.Repository}, so the branch cannot be linked.",
                ResultStatus.Error);
        }

        return "vstfs:///Git/Ref/"
            + Uri.EscapeDataString($"{repository.Project.Id}/{repository.Id}/GB{ShortBranch(branch)}");
    }

    /// <summary>The pull request's artifact id, which a work item's link to it points at.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="missing">The message when Azure DevOps sends no artifact id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The artifact id, or why there is none.</returns>
    private async Task<Result<string>> PullRequestArtifactAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        string missing,
        CancellationToken cancellationToken)
    {
        Result<PullRequestDto> response = await GetPullRequestAsync(remote, pullRequestId, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out PullRequestDto? pullRequest))
        {
            return response.WithoutValue().AsFailure<string>();
        }

        if (pullRequest.ArtifactId is not { } artifact)
        {
            return Result<string>.Failure(missing, ResultStatus.Error);
        }

        return artifact;
    }

    /// <summary>The iteration and the changed files, which the comments are to attach to.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequest">The pull request, as the list gives it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<PullRequestDetails>> GetDetailsAsync(
        AzureDevOpsRemote remote,
        PullRequestSummary pullRequest,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<PullRequestDetails>.Invalid(NoRepository);
        }

        if (pullRequest is null)
        {
            return Result<PullRequestDetails>.Invalid(
                "No pull request was given. Pass one from GetOpenAsync, ListAsync or FindForBranchAsync.");
        }

        Result<ListResponse<IterationDto>> iterations = await GetAsync<ListResponse<IterationDto>>(
            remote,
            $"pullRequests/{pullRequest.Id}/iterations",
            cancellationToken)
            .ConfigureAwait(false);

        if (!iterations.IsSuccess(out ListResponse<IterationDto>? iterationList))
        {
            return iterations.WithoutValue().AsFailure<PullRequestDetails>();
        }

        // Every push adds an iteration; the latest is the one shown.
        int iteration = 1;

        if (iterationList.Value.Count > 0)
        {
            iteration = iterationList.Value.Max(dto => dto.Id);
        }

        List<PullRequestFile> files = new();
        int skip = 0;

        while (true)
        {
            Result<IterationChangesDto> changes = await GetAsync<IterationChangesDto>(
                remote,
                $"pullRequests/{pullRequest.Id}/iterations/{iteration}/changes"
                    + $"?$top={ChangePageSize}&$skip={skip}",
                cancellationToken)
                .ConfigureAwait(false);

            if (!changes.IsSuccess(out IterationChangesDto? page))
            {
                return changes.WithoutValue().AsFailure<PullRequestDetails>();
            }

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
    }

    /// <summary>
    /// Downloads an attachment that a comment points to — usually a pasted screenshot.
    /// </summary>
    /// <param name="remote">The repo the attachment belongs to.</param>
    /// <param name="url">The address that was in the comment.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// The content. <see cref="ResultStatus.Forbidden"/> when the address does not belong to the
    /// same Azure DevOps, <see cref="ResultStatus.Unavailable"/> when the server does not hand it
    /// over.
    /// </returns>
    /// <remarks>
    /// Attachments sit behind the same sign-in as everything else, so an image in a pull request
    /// cannot be fetched with an ordinary web request. Addresses on other hosts are not fetched
    /// at all: the tool should not make calls out into the world because someone wrote a
    /// link in a comment.
    /// </remarks>
    public async Task<Result<byte[]>> DownloadAttachmentAsync(
        AzureDevOpsRemote remote,
        Uri url,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<byte[]>.Invalid(NoRepository);
        }

        if (url is null)
        {
            return Result<byte[]>.Invalid("No attachment address was given. Pass the address from the comment.");
        }

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

        Result<HttpResponseMessage> sent = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!sent.IsSuccess(out HttpResponseMessage? received))
        {
            return sent.WithoutValue().AsFailure<byte[]>();
        }

        using HttpResponseMessage response = received;

        if (!response.IsSuccessStatusCode)
        {
            // An image that cannot be fetched is not worth aborting the whole fetch for.
            // The comment is readable anyway, and the link remains in it.
            Log.AttachmentUnavailable(_logger, url.AbsoluteUri, (int)response.StatusCode);

            return Result<byte[]>.Unavailable($"The server responded {(int)response.StatusCode} to {url}.");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The threads on a pull request. The system's own messages are left out.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="iteration">The iteration, from <see cref="PullRequestDetails.Iteration"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<IReadOnlyList<PullRequestThread>>> GetThreadsAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int iteration,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<PullRequestThread>>.Invalid(NoRepository);
        }

        Result<ListResponse<ThreadDto>> response = await GetAsync<ListResponse<ThreadDto>>(
            remote,
            $"pullRequests/{pullRequestId}/threads?$iteration={iteration}",
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out ListResponse<ThreadDto>? list))
        {
            return response.WithoutValue().AsFailure<IReadOnlyList<PullRequestThread>>();
        }

        return Result<IReadOnlyList<PullRequestThread>>.Success(
        [
            .. list.Value
                .Where(thread => !thread.IsDeleted)
                .Select(ToThread)
                .Where(thread => thread.Comments.Count > 0),
        ]);
    }

    /// <summary>
    /// Creates a thread. Without <paramref name="anchor"/> it becomes a general comment on
    /// the pull request instead of a comment on a line.
    /// </summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="iteration">The iteration, from <see cref="PullRequestDetails.Iteration"/>.</param>
    /// <param name="text">The comment, as markdown.</param>
    /// <param name="anchor">The file and lines the comment is attached to, or <c>null</c> for a comment on the overview tab.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<PullRequestThread>> CreateThreadAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int iteration,
        string text,
        PullRequestAnchor? anchor,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<PullRequestThread>.Invalid(NoRepository);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Result<PullRequestThread>.Invalid("The comment has no text. Pass the comment as markdown.");
        }

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

        Result<ThreadDto> response = await PostAsync<CreateThreadDto, ThreadDto>(
            remote,
            $"pullRequests/{pullRequestId}/threads",
            body,
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out ThreadDto? created))
        {
            return response.WithoutValue().AsFailure<PullRequestThread>();
        }

        Log.PublishedThread(_logger, pullRequestId, created.Id);

        return ToThread(created);
    }

    /// <summary>Replies in a thread.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="threadId">The thread's number in the pull request.</param>
    /// <param name="text">The reply, as markdown.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<PullRequestComment>> ReplyAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int threadId,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<PullRequestComment>.Invalid(NoRepository);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Result<PullRequestComment>.Invalid("The reply has no text. Pass the reply as markdown.");
        }

        Result<CommentDto> response = await PostAsync<CommentDto, CommentDto>(
            remote,
            $"pullRequests/{pullRequestId}/threads/{threadId}/comments",
            new CommentDto { ParentCommentId = 1, Content = text, CommentType = "text" },
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out CommentDto? created))
        {
            return response.WithoutValue().AsFailure<PullRequestComment>();
        }

        return ToComment(created);
    }

    /// <summary>Sets the thread's status, for example <see cref="PullRequestThreadStatus.Fixed"/> when it has been addressed.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="threadId">The thread's number in the pull request.</param>
    /// <param name="status">The new status; <see cref="PullRequestThreadStatus.Unknown"/> cannot be set.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result> SetThreadStatusAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int threadId,
        PullRequestThreadStatus status,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result.Invalid(NoRepository);
        }

        Result<string> name = ToApiStatus(status);

        if (!name.IsSuccess(out string? apiStatus))
        {
            return name.WithoutValue();
        }

        UpdateThreadDto body = new() { Status = apiStatus };

        using HttpRequestMessage request = new(
            HttpMethod.Patch,
            Route(remote, $"pullRequests/{pullRequestId}/threads/{threadId}"))
        {
            Content = JsonContent.Create(body, options: AzureDevOpsJson.Options),
        };

        return await SendAndCheckAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Pull requests in the repo, newest first.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="query">Which pull requests to list.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<IReadOnlyList<PullRequestOverview>>> ListAsync(
        AzureDevOpsRemote remote,
        PullRequestQuery query,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<PullRequestOverview>>.Invalid(NoRepository);
        }

        if (query is null)
        {
            return Result<IReadOnlyList<PullRequestOverview>>.Invalid(
                "No query was given. Pass a PullRequestQuery, for example new PullRequestQuery(PullRequestStatusFilter.Active).");
        }

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

        Result<ListResponse<PullRequestDto>> response = await GetAsync<ListResponse<PullRequestDto>>(remote, relative, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out ListResponse<PullRequestDto>? list))
        {
            return response.WithoutValue().AsFailure<IReadOnlyList<PullRequestOverview>>();
        }

        return Result<IReadOnlyList<PullRequestOverview>>.Success(
            [.. list.Value.Select(dto => ToOverview(remote, dto)).OrderByDescending(pr => pr.Summary.CreatedAt)]);
    }

    /// <summary>A pull request with status and reviewers, open or not.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<PullRequestOverview>> GetOverviewAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<PullRequestOverview>.Invalid(NoRepository);
        }

        Result<PullRequestDto> response = await GetPullRequestAsync(remote, pullRequestId, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out PullRequestDto? dto))
        {
            return response.WithoutValue().AsFailure<PullRequestOverview>();
        }

        return ToOverview(remote, dto);
    }

    /// <summary>Changes status, draft or completion and returns the result.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="update">What to change; anything that is <c>null</c> is left as it is.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<PullRequestOverview>> UpdateAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        PullRequestUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<PullRequestOverview>.Invalid(NoRepository);
        }

        if (update is null)
        {
            return Result<PullRequestOverview>.Invalid("No update was given. Pass a PullRequestUpdate with what is to change.");
        }

        PullRequestStateDto body = ToStateDto(update);

        Result<PullRequestDto> response = await _http.PostAsync<PullRequestDto>(
            Route(remote, $"pullrequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}"),
            JsonSerializer.Serialize(body, AzureDevOpsJson.Options),
            Json,
            What,
            cancellationToken,
            HttpMethod.Patch)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out PullRequestDto? updated))
        {
            return response.WithoutValue().AsFailure<PullRequestOverview>();
        }

        Log.PullRequestStateChanged(_logger, pullRequestId, updated.Status ?? "?", updated.IsDraft);

        return ToOverview(remote, updated);
    }

    /// <summary>
    /// The change in the form the server wants it. Whatever is <c>null</c> is not sent, and is
    /// therefore left as it is.
    /// </summary>
    /// <param name="update">What to change; anything that is <c>null</c> is left as it is.</param>
    /// <returns>The body of the PATCH.</returns>
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

    /// <summary>The person the token belongs to.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<AzureDevOpsIdentity>> GetCurrentUserAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<AzureDevOpsIdentity>.Invalid(NoRepository);
        }

        // At organization level, not under the project: the question concerns the sign-in.
        Result<ConnectionDataDto> response = await _http.GetAsync<ConnectionDataDto>(
            new Uri(remote.ApiBaseUri, "_apis/connectionData?api-version=7.1-preview"),
            "the sign-in",
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out ConnectionDataDto? data))
        {
            return response.WithoutValue().AsFailure<AzureDevOpsIdentity>();
        }

        if (data.AuthenticatedUser is not { Id: { Length: > 0 } id } user)
        {
            return Result<AzureDevOpsIdentity>.Failure("Azure DevOps did not say who the token belongs to.", ResultStatus.Error);
        }

        return new AzureDevOpsIdentity(
            id,
            user.ProviderDisplayName ?? string.Empty,
            Property(user.Properties, "Account").DataOr(string.Empty),
            IdentityImage.ForId(remote, id).Data);
    }

    /// <summary>People and groups whose name or email matches the text.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="text">Part of a name or an email address.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<IReadOnlyList<AzureDevOpsIdentity>>> FindIdentitiesAsync(
        AzureDevOpsRemote remote,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<AzureDevOpsIdentity>>.Invalid(NoRepository);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Result<IReadOnlyList<AzureDevOpsIdentity>>.Invalid(
                "No text to search for was given. Pass part of a name or an email address.");
        }

        // The identities live at vssps and not under dev.azure.com. It is the same
        // organization and the same sign-in, just another host.
        Uri route = new(
            $"https://vssps.dev.azure.com/{Uri.EscapeDataString(remote.Organization)}/_apis/identities"
                + $"?searchFilter=General&filterValue={Uri.EscapeDataString(text.Trim())}&queryMembership=None"
                + $"&api-version={AzureDevOpsJson.ApiVersion}");

        Result<ListResponse<IdentityDto>> response = await _http.GetAsync<ListResponse<IdentityDto>>(
            route,
            $"the identity \"{text}\"",
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out ListResponse<IdentityDto>? list))
        {
            return response.WithoutValue().AsFailure<IReadOnlyList<AzureDevOpsIdentity>>();
        }

        return Result<IReadOnlyList<AzureDevOpsIdentity>>.Success(
        [
            .. list.Value
                .Where(identity => identity.IsActive && !string.IsNullOrEmpty(identity.Id))
                .Select(identity => new AzureDevOpsIdentity(
                    identity.Id!,
                    identity.ProviderDisplayName ?? string.Empty,
                    Property(identity.Properties, "Account")
                        .DataOr(Property(identity.Properties, "Mail").DataOr(string.Empty)),
                    IdentityImage.ForId(remote, identity.Id!).Data)),
        ]);
    }

    /// <summary>
    /// Adds a reviewer, or changes the vote or the requirement for one who is already there.
    /// </summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="reviewerId">The reviewer's identity, from <see cref="AzureDevOpsIdentity.Id"/>.</param>
    /// <param name="vote">The vote to set; <see cref="PullRequestVote.None"/> adds a reviewer without a vote.</param>
    /// <param name="isRequired">Whether the vote is required for the pull request to be completed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<PullRequestReviewer>> SetReviewerAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        string reviewerId,
        PullRequestVote vote,
        bool isRequired,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<PullRequestReviewer>.Invalid(NoRepository);
        }

        if (string.IsNullOrWhiteSpace(reviewerId))
        {
            return Result<PullRequestReviewer>.Invalid(NoReviewer);
        }

        // PUT replaces the reviewer entirely, so both vote and requirement are always sent — whatever
        // is left out is otherwise reset.
        using HttpRequestMessage request = new(HttpMethod.Put, ReviewerRoute(remote, pullRequestId, reviewerId))
        {
            Content = JsonContent.Create(
                new SetReviewerDto { Vote = (int)vote, IsRequired = isRequired },
                options: AzureDevOpsJson.Options),
        };

        Result<HttpResponseMessage> sent = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!sent.IsSuccess(out HttpResponseMessage? received))
        {
            return sent.WithoutValue().AsFailure<PullRequestReviewer>();
        }

        using HttpResponseMessage response = received;

        Result<ReviewerDto> read = await AzureDevOpsHttp.ReadAsync<ReviewerDto>(response, What, cancellationToken)
            .ConfigureAwait(false);

        if (!read.IsSuccess(out ReviewerDto? reviewer))
        {
            return read.WithoutValue().AsFailure<PullRequestReviewer>();
        }

        Log.ReviewerSet(_logger, pullRequestId, reviewer.DisplayName ?? reviewerId, reviewer.Vote);

        return ToReviewer(reviewer);
    }

    /// <summary>Removes a reviewer.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="reviewerId">The reviewer's identity, from <see cref="AzureDevOpsIdentity.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result> RemoveReviewerAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        string reviewerId,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result.Invalid(NoRepository);
        }

        if (string.IsNullOrWhiteSpace(reviewerId))
        {
            return Result.Invalid(NoReviewer);
        }

        using HttpRequestMessage request = new(HttpMethod.Delete, ReviewerRoute(remote, pullRequestId, reviewerId));

        return await SendAndCheckAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The address of one reviewer on a pull request.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="reviewerId">The reviewer's identity, from <see cref="AzureDevOpsIdentity.Id"/>.</param>
    /// <returns>The reviewer's address in the API.</returns>
    private static Uri ReviewerRoute(AzureDevOpsRemote remote, int pullRequestId, string reviewerId) => Route(
        remote,
        $"pullRequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}/reviewers/{Uri.EscapeDataString(reviewerId)}");

    /// <summary>Removes the link between a pull request and a work item.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="workItemId">The work item's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when there was a link to remove.</returns>
    public async Task<Result<bool>> UnlinkWorkItemAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int workItemId,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<bool>.Invalid(NoRepository);
        }

        Result<string> artifact = await PullRequestArtifactAsync(
            remote,
            pullRequestId,
            $"Azure DevOps sent no artifact id for pull request {pullRequestId}.",
            cancellationToken)
            .ConfigureAwait(false);

        if (!artifact.IsSuccess(out string? url))
        {
            return artifact.WithoutValue().AsFailure<bool>();
        }

        return await RemoveArtifactLinkAsync(remote, workItemId, url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the link between a branch and a work item.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="branch">The branch, with or without <c>refs/heads/</c>.</param>
    /// <param name="workItemId">The work item's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when there was a link to remove.</returns>
    public async Task<Result<bool>> UnlinkBranchAsync(
        AzureDevOpsRemote remote,
        string branch,
        int workItemId,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<bool>.Invalid(NoRepository);
        }

        Result<string> artifact = await BranchArtifactAsync(remote, branch, cancellationToken).ConfigureAwait(false);

        if (!artifact.IsSuccess(out string? url))
        {
            return artifact.WithoutValue().AsFailure<bool>();
        }

        return await RemoveArtifactLinkAsync(remote, workItemId, url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the link from a work item to an artifact — a branch or a pull request.
    /// </summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="workItemId">The work item's number.</param>
    /// <param name="artifact">The artifact's address, as the link stores it: <c>vstfs:///Git/…</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when there was a link to remove, false when there was none, or why it could not be removed.</returns>
    /// <remarks>
    /// The link lives on the work item and is removed by its index, so the revision is sent
    /// along: if someone else has managed to change the links in the meantime, the server
    /// refuses rather than removing the wrong link. The address is compared decoded, because
    /// the server does not always encode the slashes the same way as when the link was created.
    /// </remarks>
    private async Task<Result<bool>> RemoveArtifactLinkAsync(
        AzureDevOpsRemote remote,
        int workItemId,
        string artifact,
        CancellationToken cancellationToken)
    {
        string id = workItemId.ToString(CultureInfo.InvariantCulture);

        Result<WorkItemWithRelationsDto> response = await _http.GetAsync<WorkItemWithRelationsDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id}?$expand=relations"),
            $"work item {id}",
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out WorkItemWithRelationsDto? workItem))
        {
            return response.WithoutValue().AsFailure<bool>();
        }

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

        Result<IdDto> removed = await _http.PostAsync<IdDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id}"),
            JsonSerializer.Serialize(patch, AzureDevOpsJson.Options),
            JsonPatch,
            $"work item {id}",
            cancellationToken,
            HttpMethod.Patch)
            .ConfigureAwait(false);

        if (!removed.IsSuccess(out _))
        {
            return removed.WithoutValue().AsFailure<bool>();
        }

        Log.ArtifactUnlinked(_logger, workItemId, artifact);

        return true;
    }

    // ------------------------------------------------------------------ translation

    /// <summary>A pull request with status, reviewers and completion, from the API's answer.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="dto">The pull request from the API.</param>
    /// <returns>The pull request as the package describes it.</returns>
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

    /// <summary>A reviewer and their vote, from the API's answer.</summary>
    /// <param name="dto">The reviewer from the API.</param>
    /// <returns>The reviewer as the package describes it.</returns>
    private static PullRequestReviewer ToReviewer(ReviewerDto dto) => new(
        new AzureDevOpsIdentity(
            dto.Id ?? string.Empty,
            dto.DisplayName ?? dto.UniqueName ?? AzureDevOpsIdentity.UnknownName,
            dto.UniqueName ?? string.Empty,
            IdentityImage.From(dto.ImageUrl, dto.Links).Data),
        ToVote(dto.Vote),
        dto.IsRequired,
        dto.IsContainer);

    /// <summary>The vote, or <see cref="PullRequestVote.None"/> for a value the API has added.</summary>
    /// <param name="vote">The vote, as the API's number.</param>
    /// <returns>The vote.</returns>
    private static PullRequestVote ToVote(int vote)
    {
        if (Enum.IsDefined(typeof(PullRequestVote), vote))
        {
            return (PullRequestVote)vote;
        }

        return PullRequestVote.None;
    }

    /// <summary>The API's name for a merge strategy; a plain merge is <c>noFastForward</c>.</summary>
    /// <param name="strategy">The strategy.</param>
    /// <returns>The name the API wants.</returns>
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
    /// <param name="properties">The identity's properties.</param>
    /// <param name="name">The property's name, for example <c>Account</c>.</param>
    /// <returns>The value, or <see cref="ResultStatus.NotFound"/> when the identity has none.</returns>
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

    /// <summary>A pull request as the list view needs it, with its address in the web interface.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="dto">The item from the API.</param>
    /// <returns>The pull request as the package describes it.</returns>
    private static PullRequestSummary ToSummary(AzureDevOpsRemote remote, PullRequestDto dto) => new(
        dto.PullRequestId,
        dto.Title ?? $"Pull request {dto.PullRequestId}",
        dto.CreatedBy?.DisplayName ?? dto.CreatedBy?.UniqueName ?? AzureDevOpsIdentity.UnknownName,
        ShortBranch(dto.SourceRefName),
        ShortBranch(dto.TargetRefName),
        dto.CreationDate,
        new Uri($"{remote.WebUri.AbsoluteUri}/pullrequest/{dto.PullRequestId}"),
        IdentityImage.From(dto.CreatedBy?.ImageUrl, dto.CreatedBy?.Links).Data);

    /// <summary>A thread from the API's answer, without the system's own messages and deleted comments.</summary>
    /// <param name="dto">The thread from the API.</param>
    /// <returns>The thread as the package describes it.</returns>
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

    /// <summary>A comment from the API's answer.</summary>
    /// <param name="dto">The comment from the API.</param>
    /// <returns>The comment as the package describes it.</returns>
    private static PullRequestComment ToComment(CommentDto dto) => new(
        dto.Id,
        dto.Author?.DisplayName ?? dto.Author?.UniqueName ?? AzureDevOpsIdentity.UnknownName,
        dto.Content ?? string.Empty,
        dto.PublishedDate,
        IdentityImage.From(dto.Author?.ImageUrl, dto.Author?.Links).Data);

    /// <summary>Where a new thread is attached: the file and the lines on the right-hand side.</summary>
    /// <param name="anchor">The file and lines the comment is attached to, or <c>null</c> for a comment on the overview tab.</param>
    /// <returns>The thread's context as the API wants it.</returns>
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

    /// <summary>The API's name for a thread status.</summary>
    /// <param name="status">The status to set.</param>
    /// <returns>
    /// The name, or <see cref="ResultStatus.Invalid"/> for <see cref="PullRequestThreadStatus.Unknown"/>
    /// and values that are not a status, which the server refuses.
    /// </returns>
    private static Result<string> ToApiStatus(PullRequestThreadStatus status)
    {
        if (status == PullRequestThreadStatus.Unknown || !Enum.IsDefined(status))
        {
            return Result<string>.Invalid(
                "A thread cannot be given an unknown status. Pass one of the statuses Azure DevOps has, "
                    + "for example PullRequestThreadStatus.Fixed.");
        }

        return ApiEnum.Name(status);
    }

    /// <summary>The branch without <c>refs/heads/</c>; other references are left as they are.</summary>
    /// <param name="reference">The reference, for example <c>refs/heads/main</c>.</param>
    /// <returns>The short name, or an empty string for no reference.</returns>
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
    /// <param name="reference">The work item's number as text, as a link carries it.</param>
    /// <returns>The number, or <c>0</c>.</returns>
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
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="relative">The path after <c>_apis/</c>, with any query string.</param>
    /// <returns>The absolute address.</returns>
    /// <remarks>
    /// The rest — sign-in, throttling and error messages — lives in
    /// <see cref="AzureDevOpsHttp"/> together with work items and builds. The only thing
    /// specific to this one is that the path goes through <c>git/repositories</c>.
    /// </remarks>
    private static Uri Route(AzureDevOpsRemote remote, string relative) => AzureDevOpsHttp.ProjectRoute(
        remote,
        $"git/repositories/{Uri.EscapeDataString(remote.Repository)}/{relative}");

    /// <summary>Fetches one pull request.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The pull request from the API, or why there is none.</returns>
    private Task<Result<PullRequestDto>> GetPullRequestAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        CancellationToken cancellationToken) =>
        GetAsync<PullRequestDto>(
            remote,
            $"pullrequests/{pullRequestId.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);

    /// <summary>Sends a request whose answer has no content to read, and checks that it succeeded.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Success, or why the server refused or could not be reached.</returns>
    private async Task<Result> SendAndCheckAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Result<HttpResponseMessage> sent = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!sent.IsSuccess(out HttpResponseMessage? received))
        {
            return sent.WithoutValue();
        }

        using HttpResponseMessage response = received;

        return await AzureDevOpsHttp.EnsureSuccessAsync(response, What, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches from the repository's API and parses the answer.</summary>
    /// <typeparam name="T">The type the response is read as.</typeparam>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="relative">The path after <c>_apis/</c>, with any query string.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The parsed answer, or why there is none.</returns>
    private Task<Result<T>> GetAsync<T>(AzureDevOpsRemote remote, string relative, CancellationToken cancellationToken) =>
        _http.GetAsync<T>(Route(remote, relative), What, cancellationToken);

    /// <summary>Sends JSON to the repository's API and parses the answer.</summary>
    /// <typeparam name="TBody">The type of the object sent.</typeparam>
    /// <typeparam name="TResult">The type the response is read as.</typeparam>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="relative">The path after <c>_apis/</c>, with any query string.</param>
    /// <param name="body">The object sent as JSON.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The parsed answer, or why there is none.</returns>
    private Task<Result<TResult>> PostAsync<TBody, TResult>(
        AzureDevOpsRemote remote,
        string relative,
        TBody body,
        CancellationToken cancellationToken) =>
        _http.PostAsync<TBody, TResult>(Route(remote, relative), body, What, cancellationToken);
}
