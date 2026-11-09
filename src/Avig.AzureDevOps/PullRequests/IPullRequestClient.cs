
namespace Avig.AzureDevOps.PullRequests;

/// <summary>A pull request, with as much as the list view needs.</summary>
/// <param name="Id">The number in Azure DevOps.</param>
/// <param name="Title">The title.</param>
/// <param name="Author">The person who created it.</param>
/// <param name="SourceBranch">The branch being proposed, without <c>refs/heads/</c>.</param>
/// <param name="TargetBranch">The branch it is to go into, without <c>refs/heads/</c>.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="WebUri">Its address in the web interface.</param>
/// <param name="AuthorImageUrl">The address of the author's profile picture, or <c>null</c>. Fetch it with <see cref="Identities.IAvatarClient"/>.</param>
public sealed record PullRequestSummary(
    int Id,
    string Title,
    string Author,
    string SourceBranch,
    string TargetBranch,
    DateTime CreatedAt,
    Uri WebUri,
    Uri? AuthorImageUrl = null);

/// <summary>
/// A changed file in a pull request, with what is needed to attach a comment
/// to a line in it.
/// </summary>
/// <param name="Path">The path Azure DevOps uses, with a leading slash.</param>
/// <param name="ChangeType">What happened to the file; several flags at once for a file that was, say, both moved and edited.</param>
/// <param name="ChangeTrackingId">
/// The number that ties the file to the iteration. Without it, the comment ends up on the
/// overview tab instead of on the line.
/// </param>
public sealed record PullRequestFile(string Path, int ChangeTrackingId, PullRequestChangeType ChangeType);

/// <summary>A pull request with the iteration and the files the comments are to attach to.</summary>
/// <param name="Summary">The basic details.</param>
/// <param name="Iteration">The latest iteration — each push adds one.</param>
/// <param name="Files">The files that differ in that iteration.</param>
public sealed record PullRequestDetails(
    PullRequestSummary Summary,
    int Iteration,
    IReadOnlyList<PullRequestFile> Files);

/// <summary>A comment in a thread on a pull request.</summary>
/// <param name="Id">The comment's number in the thread.</param>
/// <param name="Author">The display name of the person who wrote it.</param>
/// <param name="Text">The text.</param>
/// <param name="PublishedAt">When it was written.</param>
/// <param name="AuthorImageUrl">The address of the author's profile picture, or <c>null</c>. Fetch it with <see cref="Identities.IAvatarClient"/>.</param>
public sealed record PullRequestComment(int Id, string Author, string Text, DateTime PublishedAt, Uri? AuthorImageUrl = null);

/// <summary>A comment thread on a pull request.</summary>
/// <param name="Id">The thread's number.</param>
/// <param name="FilePath">The file the thread is attached to, or <c>null</c> for a general comment.</param>
/// <param name="Line">The line on the right-hand side, or <c>null</c>.</param>
/// <param name="Status">The thread's status.</param>
/// <param name="Comments">The comments, in order.</param>
public sealed record PullRequestThread(
    int Id,
    string? FilePath,
    int? Line,
    PullRequestThreadStatus Status,
    IReadOnlyList<PullRequestComment> Comments);

/// <summary>Where a comment is to be attached in a pull request.</summary>
/// <param name="FilePath">The path, relative to the repo root, with or without a leading slash.</param>
/// <param name="StartLine">The first line, 1-based.</param>
/// <param name="EndLine">The last line, 1-based.</param>
/// <param name="ChangeTrackingId">
/// The file's number in the iteration. See <see cref="PullRequestFile.ChangeTrackingId"/>.
/// </param>
public sealed record PullRequestAnchor(string FilePath, int StartLine, int EndLine, int? ChangeTrackingId);

/// <summary>A pull request to be created.</summary>
/// <param name="SourceBranch">The branch with the changes, without <c>refs/heads/</c>.</param>
/// <param name="TargetBranch">The branch it is to go into, without <c>refs/heads/</c>.</param>
/// <param name="Title">The title.</param>
/// <param name="Description">The description as markdown. May be empty.</param>
/// <param name="IsDraft">Whether it is created as a draft, which does not ask for review yet.</param>
/// <param name="WorkItemIds">Work items the pull request is linked to, or <c>null</c>.</param>
public sealed record NewPullRequest(
    string SourceBranch,
    string TargetBranch,
    string Title,
    string Description,
    bool IsDraft = false,
    IReadOnlyList<int>? WorkItemIds = null);

/// <summary>The title and the description of a pull request.</summary>
/// <param name="Title">The title.</param>
/// <param name="Description">The description as markdown. Can be empty.</param>
public sealed record PullRequestContent(string Title, string Description);

/// <summary>A reviewer's vote, with the API's values.</summary>
public enum PullRequestVote
{
    /// <summary>Rejected.</summary>
    Rejected = -10,

    /// <summary>Waiting for the author.</summary>
    WaitingForAuthor = -5,

    /// <summary>No vote.</summary>
    None = 0,

    /// <summary>Approved with suggestions.</summary>
    ApprovedWithSuggestions = 5,

    /// <summary>Approved.</summary>
    Approved = 10,
}

/// <summary>A reviewer on a pull request.</summary>
/// <param name="Identity">Who.</param>
/// <param name="Vote">The vote.</param>
/// <param name="IsGroup">Whether the reviewer is a group rather than a person.</param>
/// <param name="IsRequired">
/// Whether the vote is required for the pull request to be completed.
/// </param>
public sealed record PullRequestReviewer(
    AzureDevOpsIdentity Identity,
    PullRequestVote Vote,
    bool IsRequired,
    bool IsGroup);

/// <summary>A pull request with its status, reviewers and what is needed to complete it.</summary>
/// <param name="Summary">The basic details.</param>
/// <param name="Status">Open, completed or abandoned.</param>
/// <param name="IsDraft">Whether it is a draft.</param>
/// <param name="Description">The description as markdown.</param>
/// <param name="MergeStatus">How the server's attempt to merge it went.</param>
/// <param name="Reviewers">The reviewers.</param>
/// <param name="AutoComplete">Whether it completes on its own once the conditions are met.</param>
/// <param name="ClosedAt">When it was completed or abandoned, otherwise <c>null</c>.</param>
/// <param name="SourceCommitId">
/// The latest commit on the source branch that the server has seen, or <c>null</c>.
/// </param>
public sealed record PullRequestOverview(
    PullRequestSummary Summary,
    PullRequestStatus Status,
    bool IsDraft,
    string Description,
    PullRequestMergeStatus MergeStatus,
    string? SourceCommitId,
    IReadOnlyList<PullRequestReviewer> Reviewers,
    bool AutoComplete,
    DateTime? ClosedAt)
{
    /// <summary>Whether it is still open.</summary>
    public bool IsActive => Status == PullRequestStatus.Active;
}

/// <summary>Which pull requests to list.</summary>
/// <param name="Status">Which statuses to include.</param>
/// <param name="CreatorId">Only those created by this identity, or <c>null</c>.</param>
/// <param name="ReviewerId">Only those where this identity is a reviewer, or <c>null</c>.</param>
/// <param name="Top">At most this many.</param>
public sealed record PullRequestQuery(
    PullRequestStatusFilter Status = PullRequestStatusFilter.Active,
    string? CreatorId = null,
    string? ReviewerId = null,
    int Top = 50);

/// <summary>How the changes are brought into the target branch.</summary>
public enum PullRequestMergeStrategy
{
    /// <summary>A merge commit, without fast-forwarding.</summary>
    Merge = 0,

    /// <summary>All commits are squashed into one.</summary>
    Squash = 1,

    /// <summary>The commits are rebased onto the target branch and fast-forwarded in.</summary>
    Rebase = 2,

    /// <summary>The commits are rebased and brought in with a merge commit.</summary>
    RebaseMerge = 3,
}

/// <summary>How a pull request is to be completed.</summary>
/// <param name="Strategy">How the changes are brought in.</param>
/// <param name="DeleteSourceBranch">Whether the source branch is deleted afterwards.</param>
/// <param name="TransitionWorkItems">Whether linked work items are moved to their next state.</param>
/// <param name="MergeCommitMessage">
/// The message on the commit, or <c>null</c> for the server's suggestion.
/// </param>
public sealed record PullRequestCompletion(
    PullRequestMergeStrategy Strategy,
    bool DeleteSourceBranch = false,
    string? MergeCommitMessage = null,
    bool TransitionWorkItems = true);

/// <summary>
/// A change to a pull request's state. Anything that is <c>null</c> is left as it is.
/// </summary>
/// <param name="Status">The new status, or <c>null</c> to leave it.</param>
/// <param name="IsDraft">Draft or not.</param>
/// <param name="Completion">How it is completed.</param>
/// <param name="LastMergeSourceCommitId">
/// The commit to be completed. Required by the server for <see cref="PullRequestStatus.Completed"/>, so that what is
/// brought in is what someone has actually seen.
/// </param>
/// <param name="AutoCompleteSetById">
/// The person who turns on auto-complete. The id <see cref="Guid.Empty"/> turns it off.
/// </param>
public sealed record PullRequestUpdate(
    PullRequestStatus? Status = null,
    bool? IsDraft = null,
    string? LastMergeSourceCommitId = null,
    PullRequestCompletion? Completion = null,
    string? AutoCompleteSetById = null);

/// <summary>
/// Pull requests in Azure DevOps: lists, lifecycle, reviewers, threads and links.
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
/// An interface and not just the class, so that an application can swap out
/// the client in its tests.
/// </remarks>
public interface IPullRequestClient
{
    /// <summary>Whether the tool has a sign-in to work with.</summary>
    bool IsConnected { get; }

    /// <summary>Open pull requests in the repo, newest first.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<IReadOnlyList<PullRequestSummary>>> GetOpenAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The open pull request for a branch, or <see cref="ResultStatus.NotFound"/> when the branch
    /// has none, and <see cref="ResultStatus.Invalid"/> when no branch is given.
    /// </summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="branch">The branch, with or without <c>refs/heads/</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PullRequestSummary>> FindForBranchAsync(
        AzureDevOpsRemote remote,
        string branch,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a pull request and returns it as it was created.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequest">The pull request to create.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PullRequestSummary>> CreateAsync(
        AzureDevOpsRemote remote,
        NewPullRequest pullRequest,
        CancellationToken cancellationToken = default);

    /// <summary>The title and the description as they currently stand in Azure DevOps.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PullRequestContent>> GetContentAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a new title and description and returns the resulting pull request.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="content">The new title and description.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PullRequestSummary>> UpdateContentAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        PullRequestContent content,
        CancellationToken cancellationToken = default);

    /// <summary>The numbers of the work items the pull request is linked to.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<IReadOnlyList<int>>> GetLinkedWorkItemIdsAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Links the pull request to a work item, so that it appears under <em>Development</em>
    /// in the work item, and the work item appears in the pull request.
    /// </summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="workItemId">The work item's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> LinkWorkItemAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int workItemId,
        CancellationToken cancellationToken = default);

    /// <summary>The numbers of the work items a branch is linked to.</summary>
    /// <param name="remote">The repo the branch is in.</param>
    /// <param name="branch">The branch, with or without <c>refs/heads/</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<Result<IReadOnlyList<int>>> GetBranchWorkItemIdsAsync(
        AzureDevOpsRemote remote,
        string branch,
        CancellationToken cancellationToken = default);

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
    Task<Result> LinkBranchAsync(
        AzureDevOpsRemote remote,
        string branch,
        int workItemId,
        CancellationToken cancellationToken = default);

    /// <summary>The iteration and the changed files, which the comments are to attach to.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequest">The pull request, as the list gives it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PullRequestDetails>> GetDetailsAsync(
        AzureDevOpsRemote remote,
        PullRequestSummary pullRequest,
        CancellationToken cancellationToken = default);

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
    Task<Result<byte[]>> DownloadAttachmentAsync(
        AzureDevOpsRemote remote,
        Uri url,
        CancellationToken cancellationToken = default);

    /// <summary>The threads on a pull request. The system's own messages are left out.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="iteration">The iteration, from <see cref="PullRequestDetails.Iteration"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<IReadOnlyList<PullRequestThread>>> GetThreadsAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int iteration,
        CancellationToken cancellationToken = default);

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
    Task<Result<PullRequestThread>> CreateThreadAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int iteration,
        string text,
        PullRequestAnchor? anchor,
        CancellationToken cancellationToken = default);

    /// <summary>Replies in a thread.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="threadId">The thread's number in the pull request.</param>
    /// <param name="text">The reply, as markdown.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PullRequestComment>> ReplyAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int threadId,
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>Sets the thread's status, for example <see cref="PullRequestThreadStatus.Fixed"/> when it has been addressed.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="threadId">The thread's number in the pull request.</param>
    /// <param name="status">The new status; <see cref="PullRequestThreadStatus.Unknown"/> cannot be set.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> SetThreadStatusAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int threadId,
        PullRequestThreadStatus status,
        CancellationToken cancellationToken = default);

    /// <summary>Pull requests in the repo, newest first.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="query">Which pull requests to list.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<IReadOnlyList<PullRequestOverview>>> ListAsync(
        AzureDevOpsRemote remote,
        PullRequestQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>A pull request with status and reviewers, open or not.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PullRequestOverview>> GetOverviewAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        CancellationToken cancellationToken = default);

    /// <summary>Changes status, draft or completion and returns the result.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="update">What to change; anything that is <c>null</c> is left as it is.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PullRequestOverview>> UpdateAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        PullRequestUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>The person the token belongs to.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<AzureDevOpsIdentity>> GetCurrentUserAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default);

    /// <summary>People and groups whose name or email matches the text.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="text">Part of a name or an email address.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<IReadOnlyList<AzureDevOpsIdentity>>> FindIdentitiesAsync(
        AzureDevOpsRemote remote,
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a reviewer, or changes the vote or the requirement for one who is already there.
    /// </summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="reviewerId">The reviewer's identity, from <see cref="AzureDevOpsIdentity.Id"/>.</param>
    /// <param name="vote">The vote to set; <see cref="PullRequestVote.None"/> adds a reviewer without a vote.</param>
    /// <param name="isRequired">Whether the vote is required for the pull request to be completed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PullRequestReviewer>> SetReviewerAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        string reviewerId,
        PullRequestVote vote,
        bool isRequired,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a reviewer.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="reviewerId">The reviewer's identity, from <see cref="AzureDevOpsIdentity.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> RemoveReviewerAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        string reviewerId,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the link between a pull request and a work item.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="pullRequestId">The pull request's number.</param>
    /// <param name="workItemId">The work item's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when there was a link to remove.</returns>
    Task<Result<bool>> UnlinkWorkItemAsync(
        AzureDevOpsRemote remote,
        int pullRequestId,
        int workItemId,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the link between a branch and a work item.</summary>
    /// <param name="remote">The organization, project and repository.</param>
    /// <param name="branch">The branch, with or without <c>refs/heads/</c>.</param>
    /// <param name="workItemId">The work item's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when there was a link to remove.</returns>
    Task<Result<bool>> UnlinkBranchAsync(
        AzureDevOpsRemote remote,
        string branch,
        int workItemId,
        CancellationToken cancellationToken = default);
}

/// <summary>The status of a thread in Azure DevOps.</summary>
/// <remarks>
/// The names are the API's own. The web interface shows <see cref="Fixed"/> as "Resolved",
/// which is worth knowing when comparing.
/// </remarks>
public enum PullRequestThreadStatus
{
    /// <summary>Open and awaiting action.</summary>
    Active = 0,

    /// <summary>Fixed. Shown as "Resolved" in the web interface.</summary>
    Fixed = 1,

    /// <summary>Won't fix: dismissed.</summary>
    WontFix = 2,

    /// <summary>Closed without being either addressed or dismissed.</summary>
    Closed = 3,

    /// <summary>Not a problem: it works as intended.</summary>
    ByDesign = 4,

    /// <summary>Waiting for something before it can be dealt with.</summary>
    Pending = 5,

    /// <summary>
    /// A status the package does not know, or none at all. Only read, never set: the server
    /// refuses it.
    /// </summary>
    Unknown = 6,
}
