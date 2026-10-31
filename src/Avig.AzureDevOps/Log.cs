using Microsoft.Extensions.Logging;

namespace Avig.AzureDevOps;

/// <summary>
/// The package's log messages, as <c>[LoggerMessage]</c> methods.
/// </summary>
/// <remarks>
/// The numbers are the same as when the code lived in AI Code Review — pull requests and
/// sign-in in the 2600 series, work items and builds in 2900, the wiki in 3000 — so that a
/// filter or a search in an old log keeps matching. New messages continue in their series.
/// </remarks>
internal static partial class Log
{
    // --------------------------------------------------- pull requests and sign-in (2600-)

    [LoggerMessage(
        EventId = 2600,
        Level = LogLevel.Information,
        Message = "Published a thread on pull request {PullRequestId} as thread {ThreadId}.")]
    public static partial void PublishedThread(ILogger logger, int pullRequestId, int threadId);

    [LoggerMessage(
        EventId = 2601,
        Level = LogLevel.Warning,
        Message = "Azure DevOps is throttling the requests. Waiting {Seconds:0.#} seconds.")]
    public static partial void PullRequestThrottled(ILogger logger, double seconds);

    [LoggerMessage(
        EventId = 2602,
        Level = LogLevel.Information,
        Message = "The attachment {Url} could not be fetched ({StatusCode}). The link stays in the comment.")]
    public static partial void AttachmentUnavailable(ILogger logger, string url, int statusCode);

    [LoggerMessage(
        EventId = 2604,
        Level = LogLevel.Information,
        Message = "Created pull request {PullRequestId} from {SourceBranch} to {TargetBranch}.")]
    public static partial void PullRequestOpened(ILogger logger, int pullRequestId, string sourceBranch, string targetBranch);

    [LoggerMessage(
        EventId = 2605,
        Level = LogLevel.Information,
        Message = "Linked pull request {PullRequestId} to work item {WorkItemId}.")]
    public static partial void PullRequestLinked(ILogger logger, int pullRequestId, int workItemId);

    [LoggerMessage(
        EventId = 2606,
        Level = LogLevel.Information,
        Message = "Rewrote the title and description of pull request {PullRequestId}.")]
    public static partial void PullRequestUpdated(ILogger logger, int pullRequestId);

    [LoggerMessage(
        EventId = 2607,
        Level = LogLevel.Information,
        Message = "Linked the branch {Branch} to work item {WorkItemId}.")]
    public static partial void BranchLinked(ILogger logger, string branch, int workItemId);

    [LoggerMessage(
        EventId = 2610,
        Level = LogLevel.Information,
        Message = "Pull request {PullRequestId} has status {Status} (draft: {IsDraft}).")]
    public static partial void PullRequestStateChanged(ILogger logger, int pullRequestId, string status, bool isDraft);

    [LoggerMessage(
        EventId = 2611,
        Level = LogLevel.Information,
        Message = "The reviewer {Reviewer} on pull request {PullRequestId} has the vote {Vote}.")]
    public static partial void ReviewerSet(ILogger logger, int pullRequestId, string reviewer, int vote);

    [LoggerMessage(
        EventId = 2612,
        Level = LogLevel.Information,
        Message = "Removed the link from work item {WorkItemId} to {Artifact}.")]
    public static partial void ArtifactUnlinked(ILogger logger, int workItemId, string artifact);

    // ------------------------------------------------------- work items and builds (2900-)

    [LoggerMessage(
        EventId = 2900,
        Level = LogLevel.Information,
        Message = "Fetched {Count} work items from {Project}.")]
    public static partial void WorkItemsLoaded(ILogger logger, int count, string project);

    [LoggerMessage(
        EventId = 2901,
        Level = LogLevel.Information,
        Message = "Created {Type} {Id} in {Project}.")]
    public static partial void WorkItemCreated(ILogger logger, int id, string type, string project);

    [LoggerMessage(
        EventId = 2902,
        Level = LogLevel.Information,
        Message = "Fetched {Count} builds from {Project}.")]
    public static partial void BuildsLoaded(ILogger logger, int count, string project);

    [LoggerMessage(
        EventId = 2903,
        Level = LogLevel.Information,
        Message = "The current sprint in {Project} is {Iteration}.")]
    public static partial void CurrentIterationLoaded(ILogger logger, string iteration, string project);

    [LoggerMessage(
        EventId = 2904,
        Level = LogLevel.Information,
        Message = "No current sprint could be read in {Project}. The quick filter is shown disabled.")]
    public static partial void CurrentIterationUnavailable(ILogger logger, Exception exception, string project);

    [LoggerMessage(
        EventId = 2905,
        Level = LogLevel.Information,
        Message = "Read the board {Board} with {Columns} columns from {Project}.")]
    public static partial void BoardLoaded(ILogger logger, string board, int columns, string project);

    [LoggerMessage(
        EventId = 2906,
        Level = LogLevel.Information,
        Message = "No board could be read in {Project}. The work view shows only the list.")]
    public static partial void BoardUnavailable(ILogger logger, Exception exception, string project);

    [LoggerMessage(
        EventId = 2907,
        Level = LogLevel.Information,
        Message = "Moved work item {Id} to {Column} by writing {Field}.")]
    public static partial void WorkItemMoved(ILogger logger, int id, string column, string field);

    [LoggerMessage(
        EventId = 2908,
        Level = LogLevel.Information,
        Message = "Changed {Fields} fields on work item {Id}.")]
    public static partial void WorkItemUpdated(ILogger logger, int id, int fields);

    [LoggerMessage(
        EventId = 2909,
        Level = LogLevel.Information,
        Message = "Fetched {Count} work items under {ParentId}.")]
    public static partial void WorkItemChildrenLoaded(ILogger logger, int count, int parentId);

    [LoggerMessage(
        EventId = 2910,
        Level = LogLevel.Information,
        Message = "Wrote comment {CommentId} on work item {Id}.")]
    public static partial void WorkItemCommented(ILogger logger, int id, int commentId);

    [LoggerMessage(
        EventId = 2911,
        Level = LogLevel.Information,
        Message = "Linked work item {Id} to {TargetId} as {Kind}.")]
    public static partial void WorkItemLinked(ILogger logger, int id, int targetId, string kind);

    [LoggerMessage(
        EventId = 2912,
        Level = LogLevel.Information,
        Message = "Removed {Count} links from work item {Id} to {TargetId}.")]
    public static partial void WorkItemUnlinked(ILogger logger, int id, int targetId, int count);

    // ------------------------------------------------------------------------ wiki (3000-)

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Read the wiki page {Path} in {Wiki}.")]
    public static partial void WikiPageLoaded(ILogger logger, string path, string wiki);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "The wiki search returned {Count} hits in {Project}.")]
    public static partial void WikiSearched(ILogger logger, int count, string project);
}
