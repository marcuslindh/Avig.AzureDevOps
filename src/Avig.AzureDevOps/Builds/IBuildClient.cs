namespace Avig.AzureDevOps.Builds;

/// <summary>Where a build is in its run.</summary>
/// <remarks>
/// Not how it went — that is <see cref="BuildOutcome"/>, which reads this and the result
/// together.
/// </remarks>
public enum BuildStatus
{
    /// <summary>Queued, not started yet.</summary>
    NotStarted = 0,

    /// <summary>Running.</summary>
    InProgress = 1,

    /// <summary>Put on hold, for example waiting for an agent or an approval.</summary>
    Postponed = 2,

    /// <summary>Being cancelled.</summary>
    Cancelling = 3,

    /// <summary>Done, successfully or not.</summary>
    Completed = 4,

    /// <summary>A status the package does not know, or none at all.</summary>
    Unknown = 5,
}

/// <summary>How a build went.</summary>
public enum BuildOutcome
{
    /// <summary>Ran to completion without errors.</summary>
    Succeeded = 0,

    /// <summary>Ran to completion, but some step complained.</summary>
    PartiallySucceeded = 1,

    /// <summary>Failed.</summary>
    Failed = 2,

    /// <summary>Canceled by someone.</summary>
    Canceled = 3,

    /// <summary>Queued or running right now, and therefore has no result yet.</summary>
    Running = 4,

    /// <summary>Something else, or a result we do not recognize.</summary>
    Unknown = 5,
}

/// <summary>A build in the project.</summary>
/// <param name="Id">The number in Azure DevOps.</param>
/// <param name="BuildNumber">The build number, the one shown in the list.</param>
/// <param name="DefinitionName">The pipeline that ran.</param>
/// <param name="DefinitionId">The pipeline's number.</param>
/// <param name="Outcome">How it went.</param>
/// <param name="Status">Where the build is: queued, running, completed…</param>
/// <param name="Branch">The branch that was built, without <c>refs/heads/</c>.</param>
/// <param name="RepositoryId">The repo that was built, as Azure DevOps identifies it.</param>
/// <param name="RequestedBy">Whoever started the build.</param>
/// <param name="QueuedAt">When it was queued.</param>
/// <param name="StartedAt">When it started running, or <c>null</c>.</param>
/// <param name="FinishedAt">When it finished, or <c>null</c> if it is still running.</param>
/// <param name="WebUri">The address of the build in the web interface.</param>
public sealed record BuildSummary(
    int Id,
    string BuildNumber,
    string DefinitionName,
    int DefinitionId,
    BuildOutcome Outcome,
    BuildStatus Status,
    string Branch,
    string? RepositoryId,
    string RequestedBy,
    DateTime QueuedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    Uri WebUri)
{
    /// <summary>How long the build took, or <c>null</c> when it could not be measured.</summary>
    public TimeSpan? Duration
    {
        get
        {
            if (StartedAt is { } started && FinishedAt is { } finished && finished > started)
            {
                return finished - started;
            }

            return null;
        }
    }
}

/// <summary>
/// Reads builds from Azure DevOps.
/// </summary>
/// <remarks>
/// Every method answers with a <see cref="Result"/> or <see cref="Result{T}"/> and never
/// throws: a failure carries a message that can be shown to the user as it is, and a
/// <see cref="ResultStatus"/> that says what kind it was — <see cref="ResultStatus.Unauthorized"/>,
/// <see cref="ResultStatus.Forbidden"/>, <see cref="ResultStatus.NotFound"/>,
/// <see cref="ResultStatus.Conflict"/>, <see cref="ResultStatus.Invalid"/>,
/// <see cref="ResultStatus.Unavailable"/> or <see cref="ResultStatus.Error"/>. Only the
/// caller's own cancellation comes as <see cref="OperationCanceledException"/>.
///
/// Read-only. Restarting a build is an action with consequences out in the world, and
/// it belongs in Azure DevOps' own interface until someone asks for it here.
/// </remarks>
public interface IBuildClient
{
    /// <summary>Indicates whether the tool has a sign-in to work with.</summary>
    bool IsConnected { get; }

    /// <summary>The builds in the project, most recently queued first.</summary>
    /// <param name="remote">The project the builds belong to.</param>
    /// <param name="limit">At most this many are fetched.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<Result<IReadOnlyList<BuildSummary>>> GetAsync(
        AzureDevOpsRemote remote,
        int limit = 100,
        CancellationToken cancellationToken = default);
}
