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
public sealed class AzureDevOpsBuildClient : IBuildClient
{
    /// <summary>No more builds than this are fetched in one call.</summary>
    private const int MaxBuilds = 1000;

    private readonly AzureDevOpsHttp _http;
    private readonly IAzureDevOpsCredential _credential;
    private readonly ILogger<AzureDevOpsBuildClient> _logger;

    public AzureDevOpsBuildClient(
        HttpClient httpClient,
        IAzureDevOpsCredential credential,
        ILogger<AzureDevOpsBuildClient> logger)
    {
        _credential = credential;
        _logger = logger;
        _http = new AzureDevOpsHttp(httpClient, credential, logger);
    }

    public bool IsConnected => _credential.IsConfigured;

    public Task<Result<IReadOnlyList<BuildSummary>>> GetAsync(
        AzureDevOpsRemote remote,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<BuildSummary>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            int top = Math.Clamp(limit, 1, MaxBuilds);

            ListResponse<BuildDto> response = await _http.GetAsync<ListResponse<BuildDto>>(
                    AzureDevOpsHttp.ProjectRoute(
                        remote,
                        $"build/builds?$top={top.ToString(CultureInfo.InvariantCulture)}"
                        + "&queryOrder=queueTimeDescending"),
                    "the project's builds",
                    cancellationToken)
                .ConfigureAwait(false);

            Log.BuildsLoaded(_logger, response.Value.Count, remote.Project);

            return [.. response.Value.Select(dto => ToSummary(remote, dto))];
        }, cancellationToken);

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
