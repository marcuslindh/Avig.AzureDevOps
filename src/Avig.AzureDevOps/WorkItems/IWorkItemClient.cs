namespace Avig.AzureDevOps.WorkItems;

/// <summary>A work item, as much of it as the list needs.</summary>
/// <param name="Id">The number in Azure DevOps.</param>
/// <param name="Type">The type's name, as the process spells it: <c>Bug</c>, <c>User Story</c>, <c>Task</c>…</param>
/// <param name="Title">The title.</param>
/// <param name="State">The state: <c>New</c>, <c>Active</c>, <c>Closed</c>…</param>
/// <param name="AssignedTo">The person who owns it, or empty text when no one does.</param>
/// <param name="IterationPath">The sprint, with the project name first.</param>
/// <param name="AreaPath">The area, with the project name first.</param>
/// <param name="Tags">The tags, one per entry.</param>
/// <param name="ParentId">The number of the work item it sits under, or <c>null</c>.</param>
/// <param name="ChangedAt">When it was last changed.</param>
/// <param name="WebUri">Its address in the web interface.</param>
/// <param name="BoardColumn">
/// The column on the kanban board, or empty text for a type that is not on any board.
/// The column is not the same thing as the state: a board can have <c>Blocked</c> and
/// <c>Testing</c> without the process having them.
/// </param>
/// <param name="StoryPoints">
/// The estimate, or <c>null</c> when none is set. The processes call it different things —
/// Story Points in Agile, Effort in Scrum, Size in CMMI — but it is the same thing, and it
/// is shown under one name.
/// </param>
public sealed record WorkItemSummary(
    int Id,
    string Type,
    string Title,
    string State,
    string AssignedTo,
    string IterationPath,
    string AreaPath,
    IReadOnlyList<string> Tags,
    int? ParentId,
    DateTime ChangedAt,
    Uri WebUri,
    string BoardColumn = "",
    double? StoryPoints = null)
{
    /// <summary>
    /// The column the card belongs in.
    /// </summary>
    /// <remarks>
    /// The board column when the type has one, otherwise the state. Tasks are not on any
    /// kanban board in Azure DevOps and therefore have no column — but their states are
    /// named <c>New</c>, <c>Active</c> and <c>Closed</c>, which are names the Stories board
    /// also has. So the card ends up in the right place anyway.
    /// </remarks>
    public string Lane
    {
        get
        {
            if (BoardColumn.Length > 0)
            {
                return BoardColumn;
            }

            return State;
        }
    }

    /// <summary>Indicates whether the card is moved by writing the state instead of the column.</summary>
    public bool MovesByState => BoardColumn.Length == 0;

    /// <summary>
    /// The character that separates the steps in an area path or iteration path.
    /// </summary>
    /// <remarks>
    /// Always a backslash, regardless of which operating system the tool runs on.
    /// The path is Azure DevOps' own and has nothing to do with the file system.
    /// </remarks>
    private const char PathSeparator = '\\';

    /// <summary>The sprint's name without the path leading to it, as shown in the web interface.</summary>
    public string Iteration => LastSegment(IterationPath);

    /// <summary>The area's name without the path leading to it.</summary>
    public string Area => LastSegment(AreaPath);

    /// <summary>The last step of an area or iteration path: <c>Sprint 6</c> from <c>Webshop\Sprint 6</c>.</summary>
    /// <param name="path">The path, with backslashes between the steps.</param>
    private static string LastSegment(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        int separator = path.LastIndexOf(PathSeparator);

        if (separator < 0)
        {
            return path;
        }

        return path[(separator + 1)..];
    }
}

/// <summary>A work item with the text fields that describe it.</summary>
/// <param name="Summary">The basic details.</param>
/// <param name="Description">The description, as HTML.</param>
/// <param name="ReproSteps">The steps that reproduce the bug, as HTML. Only bugs have it.</param>
/// <param name="AcceptanceCriteria">The acceptance criteria, as HTML. Only user stories have it.</param>
public sealed record WorkItemDetails(
    WorkItemSummary Summary,
    string Description,
    string ReproSteps,
    string AcceptanceCriteria);

/// <summary>A comment in a work item's discussion.</summary>
/// <param name="Id">The comment's number within the work item.</param>
/// <param name="Author">The display name of the person who wrote it.</param>
/// <param name="Html">The text, as HTML.</param>
/// <param name="CreatedAt">When it was written.</param>
/// <param name="ModifiedAt">When it was last changed, or <c>null</c> if it has never been changed.</param>
public sealed record WorkItemComment(
    int Id,
    string Author,
    string Html,
    DateTime CreatedAt,
    DateTime? ModifiedAt);

/// <summary>A column on a kanban board.</summary>
/// <param name="Name">The column's name, as shown on the board.</param>
/// <param name="ItemLimit">The WIP limit, or <c>0</c> when the column has none.</param>
/// <param name="IsIncoming">Indicates whether the column is the board's entry column.</param>
/// <param name="IsOutgoing">Indicates whether the column is the board's exit column.</param>
public sealed record BoardColumn(string Name, int ItemLimit, bool IsIncoming, bool IsOutgoing);

/// <summary>A kanban board: its columns, in order.</summary>
/// <param name="Name">The board's name, for example <c>Stories</c>.</param>
/// <param name="Columns">The columns from left to right.</param>
public sealed record BoardDefinition(string Name, IReadOnlyList<BoardColumn> Columns);

/// <summary>A sprint in the project.</summary>
/// <param name="Name">The sprint's name, as shown in the interface.</param>
/// <param name="Path">The full path, with the project first.</param>
/// <param name="StartsOn">The first day, or <c>null</c> when the sprint has no dates.</param>
/// <param name="EndsOn">The last day, or <c>null</c>.</param>
public sealed record WorkItemIteration(string Name, string Path, DateTime? StartsOn, DateTime? EndsOn);

/// <summary>What to fetch.</summary>
/// <param name="IncludeClosed">Indicates whether closed and removed work items are included.</param>
/// <param name="Limit">The maximum number to fetch.</param>
/// <param name="AssignedToMe">
/// Only those assigned to the signed-in user. Decided by the server, since only the server
/// knows whose token it is.
/// </param>
/// <remarks>
/// The selection is deliberately coarse. Filtering by type, person and sprint happens in the
/// interface, on what has already been fetched — going to the server for every keystroke
/// would make a fast list sluggish, and the list is small enough to fit in memory anyway.
/// </remarks>
public sealed record WorkItemQuery(bool IncludeClosed = false, int Limit = 500, bool AssignedToMe = false);

/// <summary>A new work item, as it is to be created.</summary>
/// <param name="Type">The type's name, for example <c>Bug</c> or <c>User Story</c>.</param>
/// <param name="Title">The title. The only field Azure DevOps requires.</param>
/// <param name="Description">The description, as HTML or plain text.</param>
/// <param name="ReproSteps">The steps that reproduce the bug. Sent only for bugs.</param>
/// <param name="AcceptanceCriteria">The acceptance criteria. Sent only for user stories.</param>
/// <param name="AssignedTo">The person who is to own it, or <c>null</c> for no one.</param>
/// <param name="AreaPath">The area, or <c>null</c> for the project's default.</param>
/// <param name="IterationPath">The sprint, or <c>null</c> for the project's default.</param>
/// <param name="Tags">The tags.</param>
/// <param name="StoryPoints">The estimate, or <c>null</c> for none.</param>
/// <param name="ParentId">
/// The number of the work item it is to sit under, or <c>null</c>. A task under a user
/// story gets its story here.
/// </param>
public sealed record NewWorkItem(
    string Type,
    string Title,
    string? Description = null,
    string? ReproSteps = null,
    string? AcceptanceCriteria = null,
    string? AssignedTo = null,
    string? AreaPath = null,
    string? IterationPath = null,
    IReadOnlyList<string>? Tags = null,
    int? ParentId = null,
    double? StoryPoints = null);

/// <summary>Changes to an existing work item.</summary>
/// <param name="Title">The title.</param>
/// <param name="Description">The description, as HTML.</param>
/// <param name="AcceptanceCriteria">The criteria, as HTML, or <c>null</c>.</param>
/// <param name="State">New state, or <c>null</c>.</param>
/// <param name="IterationPath">The sprint's path, or <c>null</c>.</param>
/// <param name="StoryPoints">New estimate, or <c>null</c>.</param>
/// <param name="ReproSteps">
/// The steps, as HTML, or <c>null</c> for a field that is not to be touched.
/// </param>
/// <remarks>
/// <c>null</c> means "leave as is" and not "clear". A field that the type does not have, or
/// that could not be edited, must not be overwritten with nothing.
/// </remarks>
/// <param name="AssignedTo">
/// The person who is to have it, by name or email; empty text removes the assignment;
/// <c>null</c> leaves it as is.
/// </param>
/// <param name="Tags">
/// The tags that are to apply, replacing the old ones, or <c>null</c>. An empty list removes
/// all of them.
/// </param>
/// <param name="ClearStoryPoints">
/// Removes the estimate. A separate field, since <c>null</c> in
/// <paramref name="StoryPoints"/> already means "leave as is".
/// </param>
public sealed record WorkItemEdit(
    string Title,
    string? Description = null,
    string? ReproSteps = null,
    string? AcceptanceCriteria = null,
    string? State = null,
    string? AssignedTo = null,
    IReadOnlyList<string>? Tags = null,
    string? IterationPath = null,
    double? StoryPoints = null,
    bool ClearStoryPoints = false);

/// <summary>How two work items are connected, seen from the first.</summary>
public enum WorkItemLinkKind
{
    /// <summary>Related, without direction.</summary>
    Related = 0,

    /// <summary>The second is the parent of the first.</summary>
    Parent = 1,

    /// <summary>The second sits under the first.</summary>
    Child = 2,

    /// <summary>The second is a duplicate of the first.</summary>
    Duplicate = 3,

    /// <summary>The first is a duplicate of the second.</summary>
    DuplicateOf = 4,

    /// <summary>The second must be done before the first.</summary>
    Predecessor = 5,

    /// <summary>The second can start only when the first is done.</summary>
    Successor = 6,
}

/// <summary>A link from one work item to another.</summary>
/// <param name="Kind">How they are connected.</param>
/// <param name="TargetId">The number of the other one.</param>
public sealed record WorkItemRelation(WorkItemLinkKind Kind, int TargetId);

/// <summary>
/// Reads and creates work items in Azure DevOps.
/// </summary>
/// <remarks>
/// Every method answers with a <see cref="Result"/> or <see cref="Result{T}"/> and never
/// throws: a failure carries a message that can be shown to the user as it is, and a
/// <see cref="ResultStatus"/> that says what kind it was — <see cref="ResultStatus.Unauthorized"/>,
/// <see cref="ResultStatus.Forbidden"/>, <see cref="ResultStatus.NotFound"/>,
/// <see cref="ResultStatus.Conflict"/>, <see cref="ResultStatus.Invalid"/>,
/// <see cref="ResultStatus.Unavailable"/> or <see cref="ResultStatus.Error"/>. Only the
/// caller's own cancellation comes as <see cref="OperationCanceledException"/>.
/// The sprint and the board answer <see cref="ResultStatus.NotFound"/> or
/// <see cref="ResultStatus.Unavailable"/> for a project that lacks them.
/// </remarks>
public interface IWorkItemClient
{
    /// <summary>Indicates whether the tool has a sign-in to work with.</summary>
    bool IsConnected { get; }

    /// <summary>Work items in the project, most recently changed first.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="query">Which work items to fetch.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<IReadOnlyList<WorkItemSummary>>> GetAsync(
        AzureDevOpsRemote remote,
        WorkItemQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The sprint in progress, or <c>null</c> when there is none.
    /// </summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// The question has to be asked of the server. Which sprint is the current one is
    /// decided by its dates and by which team owns it, and neither can be worked out from
    /// the list of work items.
    ///
    /// Returns a failed result instead of throwing when the sprint cannot be read — a
    /// project does not need to have sprints at all, and a list without the quick filter is
    /// far better than no list.
    /// </remarks>
    Task<Result<WorkItemIteration>> GetCurrentIterationAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The board for the project's requirements — the one bugs and user stories are on.
    /// </summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// The columns come from the board and not from the process's states. The two are not
    /// the same thing: a board can have <c>Blocked</c> and <c>Testing</c> as columns without
    /// any work item ever having them as its state.
    ///
    /// Returns a failed result when the board cannot be read, for the same reason as
    /// <see cref="GetCurrentIterationAsync"/>: a list without a board is better than nothing
    /// at all.
    /// </remarks>
    Task<Result<BoardDefinition>> GetBoardAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a work item to another column on the board.
    /// </summary>
    /// <param name="remote">The project.</param>
    /// <param name="item">The card being moved.</param>
    /// <param name="column">The column it is to go to.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The work item as it looks afterwards.</returns>
    /// <remarks>
    /// Writes the board column for the types that have one, and the state for those that do
    /// not — see <see cref="WorkItemSummary.MovesByState"/>. This is a change others see, and
    /// it should therefore come from an action someone took deliberately.
    /// </remarks>
    Task<Result<WorkItemSummary>> MoveAsync(
        AzureDevOpsRemote remote,
        WorkItemSummary item,
        string column,
        CancellationToken cancellationToken = default);

    /// <summary>The description fields of a work item.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<WorkItemDetails>> GetDetailsAsync(
        AzureDevOpsRemote remote,
        int id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the text of an existing work item.
    /// </summary>
    /// <param name="remote">The project.</param>
    /// <param name="id">The number of the one to change.</param>
    /// <param name="edit">The fields to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The work item as it looks afterwards.</returns>
    /// <remarks>
    /// This overwrites text someone else may have written. Only the fields actually present
    /// in <paramref name="edit"/> are sent — see <see cref="WorkItemEdit"/>.
    /// </remarks>
    Task<Result<WorkItemDetails>> UpdateAsync(
        AzureDevOpsRemote remote,
        int id,
        WorkItemEdit edit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The work items directly under another one — a user story's tasks.
    /// </summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="parentId">The number of the work item they sit under.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// Closed ones are included. A story whose tasks are done should show them as done, not
    /// as if they had never existed.
    /// </remarks>
    Task<Result<IReadOnlyList<WorkItemSummary>>> GetChildrenAsync(
        AzureDevOpsRemote remote,
        int parentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The comments in the work item's discussion, oldest first.
    /// </summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// Deleted comments are not included. The discussion is often where it says what was
    /// actually decided, when the description was written before anyone knew.
    /// </remarks>
    Task<Result<IReadOnlyList<WorkItemComment>>> GetCommentsAsync(
        AzureDevOpsRemote remote,
        int id,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a comment in the work item's discussion.</summary>
    /// <param name="remote">The project.</param>
    /// <param name="id">The work item.</param>
    /// <param name="html">The text, as HTML.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The comment as it was saved.</returns>
    Task<Result<WorkItemComment>> AddCommentAsync(
        AzureDevOpsRemote remote,
        int id,
        string html,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The links from a work item to other work items.
    /// </summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// Only links between work items. Links to branches, pull requests and commits are
    /// something else and are handled by <see cref="PullRequests.IPullRequestClient"/>.
    /// </remarks>
    Task<Result<IReadOnlyList<WorkItemRelation>>> GetRelationsAsync(
        AzureDevOpsRemote remote,
        int id,
        CancellationToken cancellationToken = default);

    /// <summary>Links a work item to another.</summary>
    /// <param name="remote">The project.</param>
    /// <param name="id">The work item the link starts from.</param>
    /// <param name="relation">What it is to be linked to, and how.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<Result> LinkAsync(
        AzureDevOpsRemote remote,
        int id,
        WorkItemRelation relation,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the links from one work item to another.</summary>
    /// <param name="remote">The project.</param>
    /// <param name="id">The work item the link starts from.</param>
    /// <param name="targetId">The other work item.</param>
    /// <param name="kind">Only links of that kind, or <c>null</c> for all.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many links were removed.</returns>
    Task<Result<int>> UnlinkAsync(
        AzureDevOpsRemote remote,
        int id,
        int targetId,
        WorkItemLinkKind? kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The tags that exist in the project, in alphabetical order.
    /// </summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// For suggesting the ones already in use. A tag spelled in a new way is a new tag, and
    /// a filter on the old one does not find it.
    /// </remarks>
    Task<Result<IReadOnlyList<string>>> GetTagsAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a work item and returns what was created.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="item">The work item to create.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<WorkItemSummary>> CreateAsync(
        AzureDevOpsRemote remote,
        NewWorkItem item,
        CancellationToken cancellationToken = default);
}
