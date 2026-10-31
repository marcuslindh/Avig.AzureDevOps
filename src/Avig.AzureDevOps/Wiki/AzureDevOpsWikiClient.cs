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
public sealed class AzureDevOpsWikiClient : IWikiClient
{
    /// <summary>The wiki's root, and what every page's path starts with.</summary>
    private const string RootPath = "/";

    /// <summary>The file extension of the pages in the wiki's repo.</summary>
    private const string MarkdownExtension = ".md";

    /// <summary>No more search hits than this are fetched in one call.</summary>
    private const int MaxSearchHits = 1000;

    private readonly AzureDevOpsHttp _http;
    private readonly IAzureDevOpsCredential _credential;
    private readonly ILogger<AzureDevOpsWikiClient> _logger;

    public AzureDevOpsWikiClient(
        HttpClient httpClient,
        IAzureDevOpsCredential credential,
        ILogger<AzureDevOpsWikiClient> logger)
    {
        _credential = credential;
        _logger = logger;
        _http = new AzureDevOpsHttp(httpClient, credential, logger);
    }

    public bool IsConnected => _credential.IsConfigured;

    public Task<Result<IReadOnlyList<WikiSummary>>> GetWikisAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<WikiSummary>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            ListResponse<WikiDto> response = await _http.GetAsync<ListResponse<WikiDto>>(
                    AzureDevOpsHttp.ProjectRoute(remote, "wiki/wikis"),
                    "the project's wikis",
                    cancellationToken)
                .ConfigureAwait(false);

            return
            [
                .. response.Value.Select(dto => new WikiSummary(
                    dto.Id ?? dto.Name ?? string.Empty,
                    dto.Name ?? dto.Id ?? string.Empty,
                    string.Equals(dto.Type, "projectWiki", StringComparison.OrdinalIgnoreCase))),
            ];
        }, cancellationToken);

    public Task<Result<WikiPageNode>> GetPageTreeAsync(
        AzureDevOpsRemote remote,
        string wikiId,
        string path,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunResultAsync<WikiPageNode>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentException.ThrowIfNullOrWhiteSpace(wikiId);
            ArgumentNullException.ThrowIfNull(path);

            Result<WikiPageDto> page = await GetDtoAsync(
                    remote,
                    PagesRoute(wikiId, $"pages?path={Uri.EscapeDataString(path)}&recursionLevel=full"),
                    path,
                    cancellationToken)
                .ConfigureAwait(false);

            if (page.IsSuccess(out WikiPageDto? dto))
            {
                return ToNode(dto);
            }

            return Result<WikiPageNode>.NotFound(page.Error ?? $"The wiki page {path} does not exist.");
        }, cancellationToken);

    public Task<Result<WikiPage>> GetPageAsync(
        AzureDevOpsRemote remote,
        string wikiId,
        string path,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunResultAsync<WikiPage>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentException.ThrowIfNullOrWhiteSpace(wikiId);
            ArgumentNullException.ThrowIfNull(path);

            return await ReadPageAsync(
                    remote,
                    wikiId,
                    PagesRoute(wikiId, $"pages?path={Uri.EscapeDataString(path)}&includeContent=true&recursionLevel=oneLevel"),
                    path,
                    cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken);

    public Task<Result<WikiPage>> GetPageByIdAsync(
        AzureDevOpsRemote remote,
        string wikiId,
        int id,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunResultAsync<WikiPage>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentException.ThrowIfNullOrWhiteSpace(wikiId);

            string number = id.ToString(CultureInfo.InvariantCulture);

            return await ReadPageAsync(
                    remote,
                    wikiId,
                    PagesRoute(wikiId, $"pages/{number}?includeContent=true&recursionLevel=oneLevel"),
                    $"with id {number}",
                    cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<WikiSearchHit>>> SearchAsync(
        AzureDevOpsRemote remote,
        string text,
        int limit = 25,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<WikiSearchHit>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentException.ThrowIfNullOrWhiteSpace(text);

            // Search has its own host. The project is in both the address and the filter;
            // without the filter it searches the whole organization.
            Uri route = new(
                $"https://almsearch.dev.azure.com/{Uri.EscapeDataString(remote.Organization)}"
                + $"/{Uri.EscapeDataString(remote.Project)}/_apis/search/wikisearchresults"
                + $"?api-version={AzureDevOpsJson.ApiVersion}");

            WikiSearchResponseDto response = await _http.PostAsync<WikiSearchRequestDto, WikiSearchResponseDto>(
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

            Log.WikiSearched(_logger, response.Results.Count, remote.Project);

            return
            [
                .. response.Results.Select(result => new WikiSearchHit(
                    result.Wiki?.Name ?? string.Empty,
                    PathFromGitItem(result.Path ?? result.FileName ?? string.Empty),
                    [.. result.Hits.SelectMany(hit => hit.Highlights).Select(Highlight).Distinct(StringComparer.Ordinal)])),
            ];
        }, cancellationToken);

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
            return Result<WikiPage>.NotFound(page.Error ?? $"The wiki page {what} does not exist.");
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
    /// <remarks>
    /// A page that does not exist is an answer and not an error: whoever wrote the path from
    /// memory should be told so, and whoever copied an address can be tried with another form.
    /// Everything else — sign-in, network — fails the call as usual.
    /// </remarks>
    private async Task<Result<WikiPageDto>> GetDtoAsync(
        AzureDevOpsRemote remote,
        string relative,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetAsync<WikiPageDto>(
                    AzureDevOpsHttp.ProjectRoute(remote, relative),
                    $"the wiki page {what}",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AzureDevOpsException exception) when (exception.IsNotFound)
        {
            return Result<WikiPageDto>.NotFound($"The wiki page {what} does not exist.");
        }
    }

    /// <summary>The path after <c>_apis/</c> for a wiki's pages.</summary>
    private static string PagesRoute(string wikiId, string relative) =>
        $"wiki/wikis/{Uri.EscapeDataString(wikiId)}/{relative}";

    private static WikiPageNode ToNode(WikiPageDto dto) => new(
        dto.Path ?? RootPath,
        dto.Id,
        [.. dto.SubPages.OrderBy(sub => sub.Order).Select(ToNode)]);

    /// <summary>
    /// The page's path from the file's path in the wiki's repo.
    /// </summary>
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
