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

    /// <summary>A thread was published on a pull request.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="threadId">The thread's number.</param>
    [LoggerMessage(
        EventId = 2600,
        Level = LogLevel.Information,
        Message = "Published a thread on pull request {PullRequestId} as thread {ThreadId}.")]
    public static partial void PublishedThread(ILogger logger, int pullRequestId, int threadId);

    /// <summary>Azure DevOps asked for a pause; the request waits before it is retried.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="seconds">How long it waits, in seconds.</param>
    [LoggerMessage(
        EventId = 2601,
        Level = LogLevel.Warning,
        Message = "Azure DevOps is throttling the requests. Waiting {Seconds:0.#} seconds.")]
    public static partial void PullRequestThrottled(ILogger logger, double seconds);

    /// <summary>An attachment could not be downloaded.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="url">The attachment's address.</param>
    /// <param name="statusCode">The server's status code.</param>
    [LoggerMessage(
        EventId = 2602,
        Level = LogLevel.Information,
        Message = "The attachment {Url} could not be fetched ({StatusCode}). The link stays in the comment.")]
    public static partial void AttachmentUnavailable(ILogger logger, string url, int statusCode);

    /// <summary>A pull request was created.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="sourceBranch">The branch it comes from.</param>
    /// <param name="targetBranch">The branch it goes into.</param>
    [LoggerMessage(
        EventId = 2604,
        Level = LogLevel.Information,
        Message = "Created pull request {PullRequestId} from {SourceBranch} to {TargetBranch}.")]
    public static partial void PullRequestOpened(ILogger logger, int pullRequestId, string sourceBranch, string targetBranch);

    /// <summary>A pull request was linked to a work item.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="workItemId">The work item's number.</param>
    [LoggerMessage(
        EventId = 2605,
        Level = LogLevel.Information,
        Message = "Linked pull request {PullRequestId} to work item {WorkItemId}.")]
    public static partial void PullRequestLinked(ILogger logger, int pullRequestId, int workItemId);

    /// <summary>A pull request's title and description were changed.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    [LoggerMessage(
        EventId = 2606,
        Level = LogLevel.Information,
        Message = "Rewrote the title and description of pull request {PullRequestId}.")]
    public static partial void PullRequestUpdated(ILogger logger, int pullRequestId);

    /// <summary>A branch was linked to a work item.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="workItemId">The work item's number.</param>
    [LoggerMessage(
        EventId = 2607,
        Level = LogLevel.Information,
        Message = "Linked the branch {Branch} to work item {WorkItemId}.")]
    public static partial void BranchLinked(ILogger logger, string branch, int workItemId);

    /// <summary>A pull request's status or draft state was changed.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="status">The status, as the API names it.</param>
    /// <param name="isDraft">Whether it is a draft.</param>
    [LoggerMessage(
        EventId = 2610,
        Level = LogLevel.Information,
        Message = "Pull request {PullRequestId} has status {Status} (draft: {IsDraft}).")]
    public static partial void PullRequestStateChanged(ILogger logger, int pullRequestId, string status, bool isDraft);

    /// <summary>A reviewer was added or their vote changed.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="reviewer">The reviewer's display name, or their id when the server gave none.</param>
    /// <param name="vote">The vote, as the API's number.</param>
    [LoggerMessage(
        EventId = 2611,
        Level = LogLevel.Information,
        Message = "The reviewer {Reviewer} on pull request {PullRequestId} has the vote {Vote}.")]
    public static partial void ReviewerSet(ILogger logger, int pullRequestId, string reviewer, int vote);

    /// <summary>A link from a work item to a branch or pull request was removed.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="workItemId">The work item's number.</param>
    /// <param name="artifact">The address of the branch or pull request the link pointed at.</param>
    [LoggerMessage(
        EventId = 2612,
        Level = LogLevel.Information,
        Message = "Removed the link from work item {WorkItemId} to {Artifact}.")]
    public static partial void ArtifactUnlinked(ILogger logger, int workItemId, string artifact);

    /// <summary>A profile picture could not be fetched.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="url">The picture's address.</param>
    /// <param name="status">The server's HTTP status code.</param>
    [LoggerMessage(
        EventId = 2613,
        Level = LogLevel.Warning,
        Message = "Could not fetch the profile picture {Url}: the server responded {Status}.")]
    public static partial void AvatarUnavailable(ILogger logger, string url, int status);

    // ------------------------------------------------------- work items and builds (2900-)

    /// <summary>Work items were fetched.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="count">How many.</param>
    /// <param name="project">The project's name.</param>
    [LoggerMessage(
        EventId = 2900,
        Level = LogLevel.Information,
        Message = "Fetched {Count} work items from {Project}.")]
    public static partial void WorkItemsLoaded(ILogger logger, int count, string project);

    /// <summary>A work item was created.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="type">The work item's type.</param>
    /// <param name="project">The project's name.</param>
    [LoggerMessage(
        EventId = 2901,
        Level = LogLevel.Information,
        Message = "Created {Type} {Id} in {Project}.")]
    public static partial void WorkItemCreated(ILogger logger, int id, string type, string project);

    /// <summary>Builds were fetched.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="count">How many.</param>
    /// <param name="project">The project's name.</param>
    [LoggerMessage(
        EventId = 2902,
        Level = LogLevel.Information,
        Message = "Fetched {Count} builds from {Project}.")]
    public static partial void BuildsLoaded(ILogger logger, int count, string project);

    /// <summary>The current sprint was read.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="iteration">The sprint's name.</param>
    /// <param name="project">The project's name.</param>
    [LoggerMessage(
        EventId = 2903,
        Level = LogLevel.Information,
        Message = "The current sprint in {Project} is {Iteration}.")]
    public static partial void CurrentIterationLoaded(ILogger logger, string iteration, string project);

    /// <summary>The current sprint could not be read; many projects have none.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="project">The project's name.</param>
    /// <param name="reason">Why it could not be read: the failure's message.</param>
    [LoggerMessage(
        EventId = 2904,
        Level = LogLevel.Information,
        Message = "No current sprint could be read in {Project}: {Reason} The quick filter is shown disabled.")]
    public static partial void CurrentIterationUnavailable(ILogger logger, string project, string reason);

    /// <summary>The board was read.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="board">The board's name.</param>
    /// <param name="columns">How many columns the board has.</param>
    /// <param name="project">The project's name.</param>
    [LoggerMessage(
        EventId = 2905,
        Level = LogLevel.Information,
        Message = "Read the board {Board} with {Columns} columns from {Project}.")]
    public static partial void BoardLoaded(ILogger logger, string board, int columns, string project);

    /// <summary>The board could not be read.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="project">The project's name.</param>
    /// <param name="reason">Why it could not be read: the failure's message.</param>
    [LoggerMessage(
        EventId = 2906,
        Level = LogLevel.Information,
        Message = "No board could be read in {Project}: {Reason} The work view shows only the list.")]
    public static partial void BoardUnavailable(ILogger logger, string project, string reason);

    /// <summary>A work item was moved to another column or state.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="column">The column or state it was moved to.</param>
    /// <param name="field">The field that was written: the board column or the state.</param>
    [LoggerMessage(
        EventId = 2907,
        Level = LogLevel.Information,
        Message = "Moved work item {Id} to {Column} by writing {Field}.")]
    public static partial void WorkItemMoved(ILogger logger, int id, string column, string field);

    /// <summary>Fields on a work item were changed.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="fields">How many fields were written.</param>
    [LoggerMessage(
        EventId = 2908,
        Level = LogLevel.Information,
        Message = "Changed {Fields} fields on work item {Id}.")]
    public static partial void WorkItemUpdated(ILogger logger, int id, int fields);

    /// <summary>The work items under another one were fetched.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="count">How many.</param>
    /// <param name="parentId">The number of the work item they sit under.</param>
    [LoggerMessage(
        EventId = 2909,
        Level = LogLevel.Information,
        Message = "Fetched {Count} work items under {ParentId}.")]
    public static partial void WorkItemChildrenLoaded(ILogger logger, int count, int parentId);

    /// <summary>A comment was written on a work item.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="commentId">The comment's number.</param>
    [LoggerMessage(
        EventId = 2910,
        Level = LogLevel.Information,
        Message = "Wrote comment {CommentId} on work item {Id}.")]
    public static partial void WorkItemCommented(ILogger logger, int id, int commentId);

    /// <summary>A work item was linked to another.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="targetId">The other work item's number.</param>
    /// <param name="kind">How they are linked.</param>
    [LoggerMessage(
        EventId = 2911,
        Level = LogLevel.Information,
        Message = "Linked work item {Id} to {TargetId} as {Kind}.")]
    public static partial void WorkItemLinked(ILogger logger, int id, int targetId, string kind);

    /// <summary>Links between two work items were removed.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="targetId">The other work item's number.</param>
    /// <param name="count">How many.</param>
    [LoggerMessage(
        EventId = 2912,
        Level = LogLevel.Information,
        Message = "Removed {Count} links from work item {Id} to {TargetId}.")]
    public static partial void WorkItemUnlinked(ILogger logger, int id, int targetId, int count);

    // ------------------------------------------------------------------------ wiki (3000-)

    /// <summary>A wiki page was read.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="path">The page's path.</param>
    /// <param name="wiki">The wiki's id or name.</param>
    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Read the wiki page {Path} in {Wiki}.")]
    public static partial void WikiPageLoaded(ILogger logger, string path, string wiki);

    /// <summary>The wiki was searched.</summary>
    /// <param name="logger">The log to write to.</param>
    /// <param name="count">How many.</param>
    /// <param name="project">The project's name.</param>
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "The wiki search returned {Count} hits in {Project}.")]
    public static partial void WikiSearched(ILogger logger, int count, string project);
}
