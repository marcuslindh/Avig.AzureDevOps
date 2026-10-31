using System.Globalization;
using System.Text.Json.Serialization;
using Avig.AzureDevOps.PullRequests;
using Microsoft.Extensions.Logging;

namespace Avig.AzureDevOps.Wiki;

/// <summary>
/// The wiki in Azure DevOps, over the REST API.
/// </summary>
/// <remarks>
/// The pages are reached by path or by number. Search goes to a separate service,
/// <c>almsearch.dev.azure.com</c>, and not to the project's address.
/// </remarks>
/// <remarks>A client that sends its requests through <paramref name="httpClient"/>.</remarks>
/// <param name="httpClient">The typed client from the factory, with User-Agent and timeout set.</param>
/// <param name="credential">The sign-in every request is authorized with.</param>
/// <param name="logger">The log.</param>
public sealed class AzureDevOpsWikiClient(
    HttpClient httpClient,
    IAzureDevOpsCredential credential,
    ILogger<AzureDevOpsWikiClient> logger) : IWikiClient
{
    /// <summary>The wiki's root, and what every page's path starts with.</summary>
    private const string RootPath = "/";

    /// <summary>The file extension of the pages in the wiki's repo.</summary>
    private const string MarkdownExtension = ".md";

    /// <summary>No more search hits than this are fetched in one call.</summary>
    private const int MaxSearchHits = 1000;

    /// <summary>The message when no project is given.</summary>
    private const string NoProject =
        "No project was given. Pass the organization and the project, for example from AzureDevOpsRemote.TryParse.";

    /// <summary>The message when no wiki is given.</summary>
    private const string NoWiki =
        "No wiki was given. Pass the wiki's id or name, for example WikiSummary.Id from GetWikisAsync.";

    private readonly AzureDevOpsHttp _http = new AzureDevOpsHttp(httpClient, credential, logger);
    private readonly IAzureDevOpsCredential _credential = credential;
    private readonly ILogger<AzureDevOpsWikiClient> _logger = logger;

    /// <summary>Whether the tool has a sign-in to work with.</summary>
    public bool IsConnected => _credential.IsConfigured;

    /// <summary>Wikis in the project.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<IReadOnlyList<WikiSummary>>> GetWikisAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<WikiSummary>>.Failure(NoProject, ResultStatus.Invalid);
        }

        Result<ListResponse<WikiDto>> response = await _http.GetAsync<ListResponse<WikiDto>>(
            AzureDevOpsHttp.ProjectRoute(remote, "wiki/wikis"),
            "the project's wikis",
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out ListResponse<WikiDto>? wikis))
        {
            return response.WithoutValue().AsFailure<IReadOnlyList<WikiSummary>>();
        }

        return wikis.Value
            .Select(dto => new WikiSummary(
                dto.Id ?? dto.Name ?? string.Empty,
                dto.Name ?? dto.Id ?? string.Empty,
                string.Equals(dto.Type, "projectWiki", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>The pages under a path, with all levels below it.</summary>
    /// <param name="remote">The project.</param>
    /// <param name="wikiId">The wiki's id or name, from <see cref="WikiSummary.Id"/>.</param>
    /// <param name="path">The path the tree starts at; <c>/</c> for the whole wiki.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page at the path with everything under it, or <see cref="ResultStatus.NotFound"/>.</returns>
    public async Task<Result<WikiPageNode>> GetPageTreeAsync(
        AzureDevOpsRemote remote,
        string wikiId,
        string path,
        CancellationToken cancellationToken = default)
    {
        Result arguments = Check(remote, wikiId, path);

        if (arguments.IsFailure(out _, out _))
        {
            return arguments.AsFailure<WikiPageNode>();
        }

        Result<WikiPageDto> page = await GetDtoAsync(
            remote,
            PagesRoute(wikiId, $"pages?path={Uri.EscapeDataString(path)}&recursionLevel=full"),
            path,
            cancellationToken)
            .ConfigureAwait(false);

        if (!page.IsSuccess(out WikiPageDto? dto))
        {
            return page.WithoutValue().AsFailure<WikiPageNode>();
        }

        return ToNode(dto);
    }

    /// <summary>A page with text, by path.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="wikiId">The wiki's id or name, from <see cref="WikiSummary.Id"/>.</param>
    /// <param name="path">The page's path, for example <c>/Development/Getting started</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page, or <see cref="ResultStatus.NotFound"/> when it does not exist.</returns>
    public async Task<Result<WikiPage>> GetPageAsync(
        AzureDevOpsRemote remote,
        string wikiId,
        string path,
        CancellationToken cancellationToken = default)
    {
        Result arguments = Check(remote, wikiId, path);

        if (arguments.IsFailure(out _, out _))
        {
            return arguments.AsFailure<WikiPage>();
        }

        return await ReadPageAsync(
            remote,
            wikiId,
            PagesRoute(wikiId, $"pages?path={Uri.EscapeDataString(path)}&includeContent=true&recursionLevel=oneLevel"),
            path,
            cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A page with text, by number.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="wikiId">The wiki's id or name, from <see cref="WikiSummary.Id"/>.</param>
    /// <param name="id">The page's number, from its address or <see cref="WikiPage.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page, or <see cref="ResultStatus.NotFound"/> when it does not exist.</returns>
    public async Task<Result<WikiPage>> GetPageByIdAsync(
        AzureDevOpsRemote remote,
        string wikiId,
        int id,
        CancellationToken cancellationToken = default)
    {
        Result arguments = Check(remote, wikiId, RootPath);

        if (arguments.IsFailure(out _, out _))
        {
            return arguments.AsFailure<WikiPage>();
        }

        string number = id.ToString(CultureInfo.InvariantCulture);

        return await ReadPageAsync(
            remote,
            wikiId,
            PagesRoute(wikiId, $"pages/{number}?includeContent=true&recursionLevel=oneLevel"),
            $"with id {number}",
            cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Searches the project's wikis.</summary>
    /// <param name="remote">The project.</param>
    /// <param name="text">What is searched for.</param>
    /// <param name="limit">At most this many hits.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<IReadOnlyList<WikiSearchHit>>> SearchAsync(
        AzureDevOpsRemote remote,
        string text,
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<WikiSearchHit>>.Failure(NoProject, ResultStatus.Invalid);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Result<IReadOnlyList<WikiSearchHit>>.Failure(
                "No search text was given. Pass a word or a phrase to look for in the wiki.",
                ResultStatus.Invalid);
        }

        // Search has its own host. The project is in both the address and the filter;
        // without the filter it searches the whole organization.
        Uri route = new(
            $"https://almsearch.dev.azure.com/{Uri.EscapeDataString(remote.Organization)}"
                + $"/{Uri.EscapeDataString(remote.Project)}/_apis/search/wikisearchresults"
                + $"?api-version={AzureDevOpsJson.ApiVersion}");

        Result<WikiSearchResponseDto> response = await _http.PostAsync<WikiSearchRequestDto, WikiSearchResponseDto>(
            route,
            new WikiSearchRequestDto
            {
                SearchText = text,
                Top = Math.Clamp(limit, 1, MaxSearchHits),
                Filters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["Project"] = [remote.Project],
                },
            },
            "the wiki search",
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out WikiSearchResponseDto? found))
        {
            return response.WithoutValue().AsFailure<IReadOnlyList<WikiSearchHit>>();
        }

        Log.WikiSearched(_logger, found.Results.Count, remote.Project);

        return found.Results
            .Select(result => new WikiSearchHit(
                result.Wiki?.Name ?? string.Empty,
                PathFromGitItem(result.Path ?? result.FileName ?? string.Empty),
                [.. result.Hits.SelectMany(hit => hit.Highlights).Select(Highlight).Distinct(StringComparer.Ordinal)]))
            .ToList();
    }

    /// <summary>The arguments every page lookup needs: a project, a wiki and a path.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="wikiId">The wiki's id or name.</param>
    /// <param name="path">The page's path.</param>
    /// <returns>Success, or <see cref="ResultStatus.Invalid"/> with what is missing.</returns>
    private static Result Check(AzureDevOpsRemote? remote, string? wikiId, string? path)
    {
        if (remote is null)
        {
            return Result.Failure(NoProject, ResultStatus.Invalid);
        }

        if (string.IsNullOrWhiteSpace(wikiId))
        {
            return Result.Failure(NoWiki, ResultStatus.Invalid);
        }

        if (path is null)
        {
            return Result.Failure(
                "No page path was given. Pass the page's path, for example /Development/Getting started, or / for the root.",
                ResultStatus.Invalid);
        }

        return Result.Success();
    }

    /// <summary>
    /// A page with its content, or <see cref="ResultStatus.NotFound"/> when it does not exist.
    /// </summary>
    /// <param name="remote">The project.</param>
    /// <param name="wikiId">The wiki the page is in.</param>
    /// <param name="route">The path after <c>_apis/</c> that asks for the page.</param>
    /// <param name="what">How the page is named in the messages: its path, or <c>with id 17</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async Task<Result<WikiPage>> ReadPageAsync(
        AzureDevOpsRemote remote,
        string wikiId,
        string route,
        string what,
        CancellationToken cancellationToken)
    {
        Result<WikiPageDto> page = await GetDtoAsync(remote, route, what, cancellationToken).ConfigureAwait(false);

        if (!page.IsSuccess(out WikiPageDto? dto))
        {
            return page.WithoutValue().AsFailure<WikiPage>();
        }

        Log.WikiPageLoaded(_logger, dto.Path ?? what, wikiId);

        // A page without an address in the web interface is still a page.
        _ = Uri.TryCreate(dto.RemoteUrl, UriKind.Absolute, out Uri? web);

        return new WikiPage(
            wikiId,
            dto.Path ?? RootPath,
            dto.Id,
            dto.Content ?? string.Empty,
            web,
            [.. dto.SubPages.Select(sub => sub.Path ?? string.Empty).Where(subPath => subPath.Length > 0)]);
    }

    /// <summary>
    /// The page, or <see cref="ResultStatus.NotFound"/> when the server says it does not exist.
    /// </summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="relative">The path after <c>_apis/</c>, with any query string.</param>
    /// <param name="what">The page's path, or <c>with id 17</c>, for the messages.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// A page that does not exist is an answer and not an error: whoever wrote the path from
    /// memory should be told so, and whoever copied an address can be tried with another form.
    /// Everything else — sign-in, network — is passed on with its own message and status.
    /// </remarks>
    private async Task<Result<WikiPageDto>> GetDtoAsync(
        AzureDevOpsRemote remote,
        string relative,
        string what,
        CancellationToken cancellationToken)
    {
        Result<WikiPageDto> page = await _http.GetAsync<WikiPageDto>(
            AzureDevOpsHttp.ProjectRoute(remote, relative),
            $"the wiki page {what}",
            cancellationToken)
            .ConfigureAwait(false);

        if (page.Status == ResultStatus.NotFound)
        {
            return Result<WikiPageDto>.NotFound($"The wiki page {what} does not exist.");
        }

        return page;
    }

    /// <summary>The path after <c>_apis/</c> for a wiki's pages.</summary>
    /// <param name="wikiId">The wiki's id or name, from <see cref="WikiSummary.Id"/>.</param>
    /// <param name="relative">What follows the wiki in the address: <c>pages?path=…</c>.</param>
    private static string PagesRoute(string wikiId, string relative) =>
        $"wiki/wikis/{Uri.EscapeDataString(wikiId)}/{relative}";

    /// <summary>A page and the pages under it, in the order the wiki shows them.</summary>
    /// <param name="dto">The page from the API.</param>
    private static WikiPageNode ToNode(WikiPageDto dto) => new(
        dto.Path ?? RootPath,
        dto.Id,
        [.. dto.SubPages.OrderBy(sub => sub.Order).Select(ToNode)]);

    /// <summary>
    /// The page's path from the file's path in the wiki's repo.
    /// </summary>
    /// <param name="gitPath">The file's path in the wiki's repo, for example <c>/Development/Getting-started.md</c>.</param>
    /// <remarks>
    /// Search returns the file — <c>/Utveckling/Kom-igång.md</c> — and not the page.
    /// Spaces appear there as hyphens and hyphens as <c>%2D</c>, the same encoding as in the
    /// web addresses.
    /// </remarks>
    internal static string PathFromGitItem(string gitPath)
    {
        string path = gitPath;

        if (path.EndsWith(MarkdownExtension, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^MarkdownExtension.Length];
        }

        path = path.Replace('-', ' ').Replace("%2D", "-", StringComparison.OrdinalIgnoreCase);

        if (!path.StartsWith('/'))
        {
            path = RootPath + path;
        }

        return Uri.UnescapeDataString(path);
    }

    /// <summary>
    /// An excerpt with the hit marked as «hit» instead of with tags.
    /// </summary>
    /// <param name="html">An excerpt from the search, with the hits inside <c>&lt;highlighthit&gt;</c> tags.</param>
    /// <remarks>
    /// Search marks a word in pieces around åäö — «beh»«ö»«righet» — so markings that sit
    /// edge to edge are merged into one.
    /// </remarks>
    internal static string Highlight(string html) =>
        html.Replace("<highlighthit>", "«", StringComparison.OrdinalIgnoreCase)
            .Replace("</highlighthit>", "»", StringComparison.OrdinalIgnoreCase)
            .Replace("»«", string.Empty, StringComparison.Ordinal)
            .Trim();
}

// ---------------------------------------------------------------------- the shape of the responses

internal sealed record WikiDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary><c>projectWiki</c> or <c>codeWiki</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

internal sealed record WikiPageDto
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("order")]
    public int Order { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("remoteUrl")]
    public string? RemoteUrl { get; init; }

    [JsonPropertyName("subPages")]
    public IReadOnlyList<WikiPageDto> SubPages { get; init; } = [];
}

internal sealed record WikiSearchRequestDto
{
    [JsonPropertyName("searchText")]
    public string SearchText { get; init; } = string.Empty;

    [JsonPropertyName("$skip")]
    public int Skip { get; init; }

    [JsonPropertyName("$top")]
    public int Top { get; init; } = 25;

    [JsonPropertyName("filters")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Filters { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    [JsonPropertyName("includeFacets")]
    public bool IncludeFacets { get; init; }
}

internal sealed record WikiSearchResponseDto
{
    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<WikiSearchResultDto> Results { get; init; } = [];
}

internal sealed record WikiSearchResultDto
{
    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("wiki")]
    public WikiDto? Wiki { get; init; }

    [JsonPropertyName("hits")]
    public IReadOnlyList<WikiSearchHitDto> Hits { get; init; } = [];
}

internal sealed record WikiSearchHitDto
{
    [JsonPropertyName("highlights")]
    public IReadOnlyList<string> Highlights { get; init; } = [];
}
