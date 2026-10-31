using System.Globalization;
using System.Text.Json.Serialization;
using Avig.AzureDevOps.PullRequests;
using Microsoft.Extensions.Logging;

namespace Avig.AzureDevOps.Builds;

/// <summary>
/// Builds in Azure DevOps, over the REST API.
/// </summary>
/// <remarks>
/// All of the project's builds and not just the repo's. A build that breaks a delivery is
/// often started by a different pipeline than the one that touches the code being reviewed,
/// and whoever is looking for why something did not go out wants to see both.
/// </remarks>
/// <remarks>A client that sends its requests through <paramref name="httpClient"/>.</remarks>
/// <param name="httpClient">The typed client from the factory, with User-Agent and timeout set.</param>
/// <param name="credential">The sign-in every request is authorized with.</param>
/// <param name="logger">The log.</param>
public sealed class AzureDevOpsBuildClient(
    HttpClient httpClient,
    IAzureDevOpsCredential credential,
    ILogger<AzureDevOpsBuildClient> logger) : IBuildClient
{
    /// <summary>No more builds than this are fetched in one call.</summary>
    private const int MaxBuilds = 1000;

    private readonly AzureDevOpsHttp _http = new AzureDevOpsHttp(httpClient, credential, logger);
    private readonly IAzureDevOpsCredential _credential = credential;
    private readonly ILogger<AzureDevOpsBuildClient> _logger = logger;

    /// <summary>The message when no project is given.</summary>
    private const string NoProject =
        "No project was given. Pass the organization and the project, for example from AzureDevOpsRemote.TryParse.";

    /// <summary>Indicates whether the tool has a sign-in to work with.</summary>
    public bool IsConnected => _credential.IsConfigured;

    /// <summary>The builds in the project, most recently queued first.</summary>
    /// <param name="remote">The project the builds belong to.</param>
    /// <param name="limit">At most this many are fetched.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<IReadOnlyList<BuildSummary>>> GetAsync(
        AzureDevOpsRemote remote,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<BuildSummary>>.Failure(NoProject, ResultStatus.Invalid);
        }

        int top = Math.Clamp(limit, 1, MaxBuilds);

        Result<ListResponse<BuildDto>> response = await _http.GetAsync<ListResponse<BuildDto>>(
            AzureDevOpsHttp.ProjectRoute(
                remote,
                $"build/builds?$top={top.ToString(CultureInfo.InvariantCulture)}"
                    + "&queryOrder=queueTimeDescending"),
            "the project's builds",
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out ListResponse<BuildDto>? builds))
        {
            return response.WithoutValue().AsFailure<IReadOnlyList<BuildSummary>>();
        }

        Log.BuildsLoaded(_logger, builds.Value.Count, remote.Project);

        return builds.Value.Select(dto => ToSummary(remote, dto)).ToList();
    }

    /// <summary>A build from the API's answer, with its address in the web interface.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="dto">The item from the API.</param>
    private static BuildSummary ToSummary(AzureDevOpsRemote remote, BuildDto dto) => new(
        dto.Id,
        dto.BuildNumber ?? dto.Id.ToString(CultureInfo.InvariantCulture),
        dto.Definition?.Name ?? "unknown pipeline",
        dto.Definition?.Id ?? 0,
        ToOutcome(dto.Status, dto.Result),
        ApiEnum.Parse(dto.Status, BuildStatus.Unknown),
        ShortBranch(dto.SourceBranch),
        dto.Repository?.Id,
        dto.RequestedFor?.DisplayName ?? dto.RequestedFor?.UniqueName ?? AzureDevOpsIdentity.UnknownName,
        dto.QueueTime,
        dto.StartTime,
        dto.FinishTime,
        new Uri(
            remote.ApiBaseUri,
            $"{Uri.EscapeDataString(remote.Project)}/_build/results"
                + $"?buildId={dto.Id.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>
    /// What status and result mean together.
    /// </summary>
    /// <param name="status">The API's status, for example <c>inProgress</c>, or <c>null</c>.</param>
    /// <param name="result">The API's result, or <c>null</c> while the build runs.</param>
    /// <remarks>
    /// A build that is running has no result at all, and one that is finished always has one.
    /// Reading only one of the fields therefore gives the wrong answer in half the cases.
    /// </remarks>
    internal static BuildOutcome ToOutcome(string? status, string? result)
    {
        if (status is "inProgress" or "notStarted" or "postponed")
        {
            return BuildOutcome.Running;
        }

        return result switch
        {
            "succeeded" => BuildOutcome.Succeeded,
            "partiallySucceeded" => BuildOutcome.PartiallySucceeded,
            "failed" => BuildOutcome.Failed,
            "canceled" => BuildOutcome.Canceled,
            _ => BuildOutcome.Unknown,
        };
    }

    /// <summary>The branch without <c>refs/heads/</c>; other references are left as they are.</summary>
    /// <param name="reference">The reference, for example <c>refs/heads/main</c>.</param>
    private static string ShortBranch(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return string.Empty;
        }

        const string Prefix = "refs/heads/";

        if (reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return reference[Prefix.Length..];
        }

        return reference;
    }
}

// ---------------------------------------------------------------------- the shape of the responses

internal sealed record BuildDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("buildNumber")]
    public string? BuildNumber { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("queueTime")]
    public DateTime QueueTime { get; init; }

    [JsonPropertyName("startTime")]
    public DateTime? StartTime { get; init; }

    [JsonPropertyName("finishTime")]
    public DateTime? FinishTime { get; init; }

    [JsonPropertyName("sourceBranch")]
    public string? SourceBranch { get; init; }

    [JsonPropertyName("definition")]
    public BuildDefinitionDto? Definition { get; init; }

    [JsonPropertyName("requestedFor")]
    public IdentityRef? RequestedFor { get; init; }

    [JsonPropertyName("repository")]
    public BuildRepositoryDto? Repository { get; init; }
}

internal sealed record BuildDefinitionDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed record BuildRepositoryDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
