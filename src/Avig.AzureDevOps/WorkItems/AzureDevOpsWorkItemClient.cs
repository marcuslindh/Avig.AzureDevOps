using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avig.AzureDevOps.Identities;
using Avig.AzureDevOps.PullRequests;
using Microsoft.Extensions.Logging;

namespace Avig.AzureDevOps.WorkItems;

/// <summary>
/// Work items in Azure DevOps, over the REST API.
/// </summary>
/// <remarks>
/// Fetching happens in two steps, because the API allows nothing else: a WIQL query returns
/// only numbers, and the fields are then fetched in batches of two hundred. Two calls for a
/// list of three hundred are still faster than three hundred calls.
///
/// The fields are read as <see cref="JsonElement"/> and not into a type per process template.
/// Which fields a work item has depends on the process — Agile, Scrum and Basic spell both
/// types and fields differently, and an organization can add its own. Reading what one
/// recognizes and ignoring the rest is the only thing that works in all three.
/// </remarks>
/// <remarks>A client that sends its requests through <paramref name="httpClient"/>.</remarks>
/// <param name="httpClient">The typed client from the factory, with User-Agent and timeout set.</param>
/// <param name="credential">The sign-in every request is authorized with.</param>
/// <param name="logger">The log.</param>
public sealed class AzureDevOpsWorkItemClient(
    HttpClient httpClient,
    IAzureDevOpsCredential credential,
    ILogger<AzureDevOpsWorkItemClient> logger) : IWorkItemClient
{
    /// <summary>No more numbers than this may be sent to the field fetch in one call.</summary>
    private const int BatchSize = 200;

    /// <summary>No more work items than this are fetched in one list.</summary>
    private const int MaxListed = 2000;

    /// <summary>This many comments are fetched per call. The API's limit is 200.</summary>
    private const int CommentPageSize = 200;

    /// <summary>
    /// The comments exist only as a preview version, also in 7.1. Without the version the
    /// server responds 400.
    /// </summary>
    private const string CommentsApiVersion = "7.1-preview.4";

    /// <summary>
    /// Types that do not belong in a work list.
    /// </summary>
    /// <remarks>
    /// An exclusion and not a list of what to include: an organization can have its own
    /// types, and they should be visible. Test artifacts and the work items Azure DevOps creates
    /// for itself, on the other hand, are never anything anyone wants to browse here.
    /// </remarks>
    private static readonly string[] ExcludedTypes =
    [
        "Test Case",
        "Test Suite",
        "Test Plan",
        "Shared Steps",
        "Shared Parameter",
        "Code Review Request",
        "Code Review Response",
        "Feedback Request",
        "Feedback Response",
    ];

    /// <summary>States that mean the work is finished.</summary>
    private static readonly string[] FinishedStates = ["Closed", "Removed", "Done"];

    /// <summary>
    /// The fields that carry the estimate, in the order they are tried.
    /// </summary>
    /// <remarks>
    /// Agile calls it Story Points, Scrum Effort and CMMI Size. All three exist in every
    /// organization, but only one of them is on a given type — which one is decided by the
    /// type's fields and not by its name, see <see cref="EstimateFieldAsync"/>.
    /// </remarks>
    private static readonly string[] EstimateFields =
    [
        FieldNames.StoryPoints,
        FieldNames.Effort,
        FieldNames.Size,
    ];

    /// <summary>The fields fetched for the list.</summary>
    private static readonly string[] ListFields =
    [
        FieldNames.WorkItemType,
        FieldNames.Title,
        FieldNames.State,
        FieldNames.AssignedTo,
        FieldNames.IterationPath,
        FieldNames.AreaPath,
        FieldNames.Tags,
        FieldNames.Parent,
        FieldNames.ChangedDate,
        FieldNames.BoardColumn,
        .. EstimateFields,
    ];

    /// <summary>
    /// How long after creation a change time may be without counting as a change.
    /// </summary>
    private static readonly TimeSpan EditTolerance = TimeSpan.FromSeconds(1);

    /// <summary>The tag list exists only as a preview version.</summary>
    private const string TagsApiVersion = "7.1-preview.1";

    /// <summary>
    /// The boards, in the order they are offered.
    /// </summary>
    /// <remarks>
    /// The requirement level first. Bugs and user stories are on the same board when the
    /// project manages bugs as requirements, and that is the board one works in daily.
    /// </remarks>
    private static readonly string[] BoardOrder = ["Stories", "Backlog items", "Issues", "Requirements"];

    /// <summary>The text fields, which are only fetched for one work item at a time.</summary>
    private static readonly string[] TextFields =
    [
        FieldNames.Description,
        FieldNames.ReproSteps,
        FieldNames.AcceptanceCriteria,
    ];

    private readonly AzureDevOpsHttp _http = new AzureDevOpsHttp(httpClient, credential, logger);
    private readonly IAzureDevOpsCredential _credential = credential;
    private readonly ILogger<AzureDevOpsWorkItemClient> _logger = logger;

    /// <summary>
    /// The estimate field per type and project, or empty text for a type without one.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _estimateFields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Indicates whether the tool has a sign-in to work with.</summary>
    public bool IsConnected => _credential.IsConfigured;

    /// <summary>The message when no project is given.</summary>
    private const string NoProject =
        "No project was given. Pass the organization and the project, for example from AzureDevOpsRemote.TryParse.";

    /// <summary>The media type the API wants for a change to a work item.</summary>
    private const string JsonPatch = "application/json-patch+json";

    /// <summary>Work items in the project, most recently changed first.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="query">Which work items to fetch.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<IReadOnlyList<WorkItemSummary>>> GetAsync(
        AzureDevOpsRemote remote,
        WorkItemQuery query,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<WorkItemSummary>>.Invalid(NoProject);
        }

        if (query is null)
        {
            return Result<IReadOnlyList<WorkItemSummary>>.Invalid(
                "No query was given. Pass a WorkItemQuery, for example new WorkItemQuery() for the open work items.");
        }

        int limit = Math.Clamp(query.Limit, 1, MaxListed);

        Result<WiqlResultDto> result = await _http.PostAsync<WiqlQueryDto, WiqlResultDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"wit/wiql?$top={limit}"),
            new WiqlQueryDto { Query = BuildWiql(query.IncludeClosed, query.AssignedToMe) },
            "the project's work items",
            cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess(out WiqlResultDto? wiql))
        {
            return result.WithoutValue().AsFailure<IReadOnlyList<WorkItemSummary>>();
        }

        // WIQL only returns numbers. The order is the one the query asked for and must hold all the way,
        // so fetching the fields must not shuffle it.
        int[] ids = [.. wiql.WorkItems.Select(item => item.Id).Take(limit)];

        Result<IReadOnlyList<WorkItemSummary>> items = await FetchAsync(remote, ids, cancellationToken).ConfigureAwait(false);

        if (items.IsSuccess(out IReadOnlyList<WorkItemSummary>? loaded))
        {
            Log.WorkItemsLoaded(_logger, loaded.Count, remote.Project);
        }

        return items;
    }

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
    public async Task<Result<IReadOnlyList<WorkItemSummary>>> GetChildrenAsync(
        AzureDevOpsRemote remote,
        int parentId,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<WorkItemSummary>>.Invalid(NoProject);
        }

        // The number is an integer and is written in as one; no text from outside ends up in the query.
        Result<WiqlResultDto> result = await _http.PostAsync<WiqlQueryDto, WiqlResultDto>(
            AzureDevOpsHttp.ProjectRoute(remote, "wit/wiql"),
            new WiqlQueryDto { Query = BuildChildrenWiql(parentId) },
            $"the work items under {parentId.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess(out WiqlResultDto? wiql))
        {
            return result.WithoutValue().AsFailure<IReadOnlyList<WorkItemSummary>>();
        }

        Result<IReadOnlyList<WorkItemSummary>> children = await FetchAsync(
            remote,
            [.. wiql.WorkItems.Select(item => item.Id)],
            cancellationToken)
            .ConfigureAwait(false);

        if (children.IsSuccess(out IReadOnlyList<WorkItemSummary>? loaded))
        {
            Log.WorkItemChildrenLoaded(_logger, loaded.Count, parentId);
        }

        return children;
    }

    /// <summary>
    /// The fields for a list of numbers, in batches and in the same order as the numbers.
    /// </summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="ids">The work items' numbers.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The work items in the order of <paramref name="ids"/>, or the first batch's failure.</returns>
    private async Task<Result<IReadOnlyList<WorkItemSummary>>> FetchAsync(
        AzureDevOpsRemote remote,
        int[] ids,
        CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            return Result<IReadOnlyList<WorkItemSummary>>.Success([]);
        }

        List<WorkItemSummary> items = new(ids.Length);

        for (int offset = 0; offset < ids.Length; offset += BatchSize)
        {
            int[] page = ids[offset..Math.Min(offset + BatchSize, ids.Length)];

            Result<WorkItemListDto> batch = await _http.PostAsync<WorkItemBatchDto, WorkItemListDto>(
                AzureDevOpsHttp.ProjectRoute(remote, "wit/workitemsbatch"),
                new WorkItemBatchDto { Ids = page, Fields = ListFields },
                "the project's work items",
                cancellationToken)
                .ConfigureAwait(false);

            if (!batch.IsSuccess(out WorkItemListDto? list))
            {
                return batch.WithoutValue().AsFailure<IReadOnlyList<WorkItemSummary>>();
            }

            items.AddRange(list.Value.Select(dto => ToSummary(remote, dto)));
        }

        Dictionary<int, WorkItemSummary> byId = items.ToDictionary(item => item.Id);

        return Result<IReadOnlyList<WorkItemSummary>>.Success([.. ids.Where(byId.ContainsKey).Select(id => byId[id])]);
    }

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
    public async Task<Result<WorkItemIteration>> GetCurrentIterationAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<WorkItemIteration>.Invalid(NoProject);
        }

        // Without a team in the address the API answers for the project's default team, which
        // is the same sprint the web interface shows when nothing else has been chosen.
        Result<ListResponse<TeamIterationDto>> response = await _http.GetAsync<ListResponse<TeamIterationDto>>(
            AzureDevOpsHttp.ProjectRoute(remote, "work/teamsettings/iterations?$timeframe=current"),
            "the project's sprints",
            cancellationToken)
            .ConfigureAwait(false);

        if (response.IsFailure(out string? error, out _, out ListResponse<TeamIterationDto>? iterations))
        {
            // A project without sprints, a team that has no ongoing one, or a
            // token without access to them. None of that is worth bringing down the whole view for
            // — the quick filter is then drawn disabled instead.
            Log.CurrentIterationUnavailable(_logger, remote.Project, error);

            return Result<WorkItemIteration>.Unavailable(error);
        }

        if (iterations.Value.Count == 0)
        {
            return Result<WorkItemIteration>.NotFound($"{remote.Project} has no ongoing sprint.");
        }

        TeamIterationDto dto = iterations.Value[0];

        Log.CurrentIterationLoaded(_logger, dto.Name ?? "?", remote.Project);

        return new WorkItemIteration(
            dto.Name ?? string.Empty,
            dto.Path ?? string.Empty,
            dto.Attributes?.StartDate,
            dto.Attributes?.FinishDate);
    }

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
    public async Task<Result<BoardDefinition>> GetBoardAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<BoardDefinition>.Invalid(NoProject);
        }

        // Without a team in the address the API answers for the project's default team.
        Result<ListResponse<BoardReferenceDto>> boards = await _http.GetAsync<ListResponse<BoardReferenceDto>>(
            AzureDevOpsHttp.ProjectRoute(remote, "work/boards"),
            "the project's boards",
            cancellationToken)
            .ConfigureAwait(false);

        if (boards.IsFailure(out string? error, out _, out ListResponse<BoardReferenceDto>? references))
        {
            Log.BoardUnavailable(_logger, remote.Project, error);

            return Result<BoardDefinition>.Unavailable(error);
        }

        if (!Pick(references.Value).IsSuccess(out BoardReferenceDto? reference))
        {
            return Result<BoardDefinition>.NotFound($"{remote.Project} has no board.");
        }

        Result<BoardDto> fetched = await _http.GetAsync<BoardDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"work/boards/{Uri.EscapeDataString(reference.Id ?? string.Empty)}"),
            "the project's board",
            cancellationToken)
            .ConfigureAwait(false);

        if (fetched.IsFailure(out string? boardError, out _, out BoardDto? board))
        {
            Log.BoardUnavailable(_logger, remote.Project, boardError);

            return Result<BoardDefinition>.Unavailable(boardError);
        }

        BoardDefinition definition = new(
            board.Name ?? reference.Name ?? "Board",
            [
                .. board.Columns.Select(column => new BoardColumn(
                    column.Name ?? string.Empty,
                    column.ItemLimit,
                    string.Equals(column.ColumnType, "incoming", StringComparison.OrdinalIgnoreCase),
                    string.Equals(column.ColumnType, "outgoing", StringComparison.OrdinalIgnoreCase))),
            ]);

        Log.BoardLoaded(_logger, definition.Name, definition.Columns.Count, remote.Project);

        return definition;
    }

    /// <summary>
    /// The board the requirements are on.
    /// </summary>
    /// <param name="boards">The team's boards.</param>
    /// <returns>The board, or <see cref="ResultStatus.NotFound"/> when the team has none.</returns>
    /// <remarks>
    /// The name differs between the process templates — Agile says "Stories", Scrum
    /// "Backlog items", Basic "Issues". If none of them is found, the first board is taken,
    /// which is better than none at all.
    /// </remarks>
    private static Result<BoardReferenceDto> Pick(IReadOnlyList<BoardReferenceDto> boards)
    {
        foreach (string wanted in BoardOrder)
        {
            BoardReferenceDto? match = boards.FirstOrDefault(
                board => string.Equals(board.Name, wanted, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        if (boards.Count > 0)
        {
            return boards[0];
        }

        return Result<BoardReferenceDto>.NotFound();
    }

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
    public async Task<Result<WorkItemSummary>> MoveAsync(
        AzureDevOpsRemote remote,
        WorkItemSummary item,
        string column,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<WorkItemSummary>.Invalid(NoProject);
        }

        if (item is null)
        {
            return Result<WorkItemSummary>.Invalid(
                "No work item was given to move. Pass the work item as the list or the board returned it.");
        }

        if (string.IsNullOrWhiteSpace(column))
        {
            return Result<WorkItemSummary>.Invalid(
                "No column was given. Pass the name of the board column, or the state, to move the work item to.");
        }

        // The board column for the types that have one, the state for those that do not.
        // A task is not on the kanban board and therefore has no column to write.
        string field = FieldNames.BoardColumn;

        if (item.MovesByState)
        {
            field = FieldNames.State;
        }

        PatchOperationDto[] patch = [new PatchOperationDto { Op = "add", Path = "/fields/" + field, Value = column }];

        Result<WorkItemDto> moved = await PatchAsync(remote, item.Id, patch, cancellationToken).ConfigureAwait(false);

        if (!moved.IsSuccess(out WorkItemDto? dto))
        {
            return moved.WithoutValue().AsFailure<WorkItemSummary>();
        }

        Log.WorkItemMoved(_logger, item.Id, column, field);

        return ToSummary(remote, dto);
    }

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
    public async Task<Result<WorkItemDetails>> UpdateAsync(
        AzureDevOpsRemote remote,
        int id,
        WorkItemEdit edit,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<WorkItemDetails>.Invalid(NoProject);
        }

        if (edit is null)
        {
            return Result<WorkItemDetails>.Invalid(
                "No changes were given. Pass a WorkItemEdit with at least the title; fields left null are not changed.");
        }

        if (string.IsNullOrWhiteSpace(edit.Title))
        {
            return Result<WorkItemDetails>.Invalid("A work item must have a title.");
        }

        // Only the fields in the edit. A field that is not mentioned must stay as it
        // was — null means "leave as is", not "clear".
        List<PatchOperationDto> patch = [Add(FieldNames.Title, edit.Title.Trim())];

        AddIfGiven(patch, FieldNames.Description, edit.Description);
        AddIfGiven(patch, FieldNames.ReproSteps, edit.ReproSteps);
        AddIfGiven(patch, FieldNames.AcceptanceCriteria, edit.AcceptanceCriteria);
        AddIfGiven(patch, FieldNames.State, edit.State);
        AddIfGiven(patch, FieldNames.IterationPath, edit.IterationPath);

        // Empty text and an empty list mean "clear" here, unlike the text fields above.
        // Whoever removes the assignment or the tags means exactly that.
        if (edit.AssignedTo is { } assignedTo)
        {
            patch.Add(Add(FieldNames.AssignedTo, assignedTo.Trim()));
        }

        if (edit.Tags is { } tags)
        {
            patch.Add(Add(
                FieldNames.Tags,
                string.Join("; ", tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()))));
        }

        if (edit.StoryPoints is not null || edit.ClearStoryPoints)
        {
            Result<IReadOnlyList<PatchOperationDto>> estimate =
                await EstimatePatchAsync(remote, id, edit, cancellationToken).ConfigureAwait(false);

            if (!estimate.IsSuccess(out IReadOnlyList<PatchOperationDto>? operations))
            {
                return estimate.WithoutValue().AsFailure<WorkItemDetails>();
            }

            patch.AddRange(operations);
        }

        Result<WorkItemDto> updated = await PatchAsync(remote, id, patch, cancellationToken).ConfigureAwait(false);

        if (!updated.IsSuccess(out WorkItemDto? dto))
        {
            return updated.WithoutValue().AsFailure<WorkItemDetails>();
        }

        Log.WorkItemUpdated(_logger, id, patch.Count);

        return new WorkItemDetails(
            ToSummary(remote, dto),
            Text(dto, FieldNames.Description),
            Text(dto, FieldNames.ReproSteps),
            Text(dto, FieldNames.AcceptanceCriteria));
    }

    /// <summary>
    /// The change to the estimate, in the field the type has.
    /// </summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="edit">The change; only its estimate is read.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The operations to add to the patch — none when there is nothing to remove — or why the
    /// estimate cannot be written, for example a type without story points.
    /// </returns>
    /// <remarks>
    /// The type must be read first: which field applies depends on it, and the change only
    /// says what the value should be. Removing a value that does not exist is an error to the
    /// server, so it is only done when there is something to remove.
    /// </remarks>
    private async Task<Result<IReadOnlyList<PatchOperationDto>>> EstimatePatchAsync(
        AzureDevOpsRemote remote,
        int id,
        WorkItemEdit edit,
        CancellationToken cancellationToken)
    {
        string number = id.ToString(CultureInfo.InvariantCulture);
        string fields = string.Join(',', EstimateFields.Prepend(FieldNames.WorkItemType));

        Result<WorkItemDto> fetched = await _http.GetAsync<WorkItemDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{number}?fields={Uri.EscapeDataString(fields)}"),
            $"work item {number}",
            cancellationToken)
            .ConfigureAwait(false);

        if (!fetched.IsSuccess(out WorkItemDto? current))
        {
            return fetched.WithoutValue().AsFailure<IReadOnlyList<PatchOperationDto>>();
        }

        Result<string> estimateField = await EstimateFieldAsync(remote, Text(current, FieldNames.WorkItemType), cancellationToken)
            .ConfigureAwait(false);

        if (!estimateField.IsSuccess(out string? field))
        {
            return estimateField.WithoutValue().AsFailure<IReadOnlyList<PatchOperationDto>>();
        }

        if (edit.StoryPoints is { } points)
        {
            return Result<IReadOnlyList<PatchOperationDto>>.Success(
                [new PatchOperationDto { Op = "add", Path = "/fields/" + field, Value = points }]);
        }

        if (current.Fields.ContainsKey(field))
        {
            return Result<IReadOnlyList<PatchOperationDto>>.Success(
                [new PatchOperationDto { Op = "remove", Path = "/fields/" + field, Value = null }]);
        }

        return Result<IReadOnlyList<PatchOperationDto>>.Success([]);
    }

    /// <summary>
    /// The field that carries the estimate for a type.
    /// </summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="type">The work item type, for example <c>User Story</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The field's reference name, or <see cref="ResultStatus.Invalid"/> when the type has none
    /// of them, and the failure when the type's fields cannot be read.
    /// </returns>
    /// <remarks>
    /// The type's fields are read from the server once and stored. Guessing from the type's
    /// name does not work: a bug has Story Points in Agile, Effort in Scrum and neither in CMMI.
    /// </remarks>
    private async Task<Result<string>> EstimateFieldAsync(AzureDevOpsRemote remote, string type, CancellationToken cancellationToken)
    {
        string key = $"{remote.Organization}/{remote.Project}/{type}";

        if (!_estimateFields.TryGetValue(key, out string? field))
        {
            Result<ListResponse<WorkItemTypeFieldDto>> response = await _http.GetAsync<ListResponse<WorkItemTypeFieldDto>>(
                AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitemtypes/{Uri.EscapeDataString(type)}/fields"),
                $"the fields of {type}",
                cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccess(out ListResponse<WorkItemTypeFieldDto>? typeFields))
            {
                return response.WithoutValue().AsFailure<string>();
            }

            HashSet<string> available = new(
                typeFields.Value.Select(dto => dto.ReferenceName ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            field = EstimateFields.FirstOrDefault(available.Contains) ?? string.Empty;
            _estimateFields[key] = field;
        }

        if (field.Length > 0)
        {
            return field;
        }

        return Result<string>.Invalid($"The type {type} has no story points in the project's process.");
    }

    /// <summary>
    /// The tags that exist in the project, in alphabetical order.
    /// </summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// For suggesting the ones already in use. A tag spelled in a new way is a new tag, and
    /// a filter on the old one does not find it.
    /// </remarks>
    public async Task<Result<IReadOnlyList<string>>> GetTagsAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<string>>.Invalid(NoProject);
        }

        Result<ListResponse<TagDto>> response = await _http.GetAsync<ListResponse<TagDto>>(
            AzureDevOpsHttp.ProjectRoute(remote, "wit/tags", TagsApiVersion),
            "the project's tags",
            cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess(out ListResponse<TagDto>? tags))
        {
            return response.WithoutValue().AsFailure<IReadOnlyList<string>>();
        }

        return Result<IReadOnlyList<string>>.Success(
        [
            .. tags.Value
                .Select(tag => tag.Name ?? string.Empty)
                .Where(name => name.Length > 0)
                .Order(StringComparer.CurrentCultureIgnoreCase),
        ]);
    }

    /// <summary>The description fields of a work item.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<WorkItemDetails>> GetDetailsAsync(
        AzureDevOpsRemote remote,
        int id,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<WorkItemDetails>.Invalid(NoProject);
        }

        string fields = string.Join(',', ListFields.Concat(TextFields));

        Result<WorkItemDto> fetched = await _http.GetAsync<WorkItemDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id}?fields={Uri.EscapeDataString(fields)}"),
            $"work item {id.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken)
            .ConfigureAwait(false);

        if (!fetched.IsSuccess(out WorkItemDto? dto))
        {
            return fetched.WithoutValue().AsFailure<WorkItemDetails>();
        }

        return new WorkItemDetails(
            ToSummary(remote, dto),
            Text(dto, FieldNames.Description),
            Text(dto, FieldNames.ReproSteps),
            Text(dto, FieldNames.AcceptanceCriteria));
    }

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
    public async Task<Result<IReadOnlyList<WorkItemComment>>> GetCommentsAsync(
        AzureDevOpsRemote remote,
        int id,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<WorkItemComment>>.Invalid(NoProject);
        }

        string number = id.ToString(CultureInfo.InvariantCulture);
        List<WorkItemComment> comments = [];
        string? continuation = null;

        // Page by page, since a long discussion does not fit in one response. The cap is 200 per page.
        do
        {
            string relative = $"wit/workItems/{number}/comments?$top={CommentPageSize}&order=asc&$expand=renderedText";

            if (continuation is not null)
            {
                relative += $"&continuationToken={Uri.EscapeDataString(continuation)}";
            }

            Result<CommentListDto> fetched = await _http.GetAsync<CommentListDto>(
                AzureDevOpsHttp.ProjectRoute(remote, relative, CommentsApiVersion),
                $"work item {number}",
                cancellationToken)
                .ConfigureAwait(false);

            if (!fetched.IsSuccess(out CommentListDto? page))
            {
                return fetched.WithoutValue().AsFailure<IReadOnlyList<WorkItemComment>>();
            }

            comments.AddRange(page.Comments.Where(comment => !comment.IsDeleted).Select(ToComment));

            // The server sends an empty token, not none, on the last page.
            continuation = null;

            if (!string.IsNullOrEmpty(page.ContinuationToken))
            {
                continuation = page.ContinuationToken;
            }
        }
        while (continuation is not null);

        return Result<IReadOnlyList<WorkItemComment>>.Success(comments);
    }

    /// <summary>
    /// A comment as it is to be displayed.
    /// </summary>
    /// <param name="dto">The comment from the API.</param>
    /// <returns>The comment, with the HTML to show and when it was last edited.</returns>
    /// <remarks>
    /// A comment can be written in markdown, and then <c>text</c> is markdown and not
    /// HTML. <c>renderedText</c> is always HTML, so it is used when the server has sent it.
    /// </remarks>
    private static WorkItemComment ToComment(CommentDto dto)
    {
        string html = dto.Text ?? string.Empty;

        if (!string.IsNullOrEmpty(dto.RenderedText))
        {
            html = dto.RenderedText;
        }

        // The server sets the modified time when the comment is written, a fraction after
        // creation. It only counts as edited when the difference is larger than that.
        DateTime? modifiedAt = null;

        if (dto.ModifiedDate is { } modified && modified > dto.CreatedDate.Add(EditTolerance))
        {
            modifiedAt = modified;
        }

        return new WorkItemComment(
            dto.Id,
            dto.CreatedBy?.DisplayName ?? dto.CreatedBy?.UniqueName ?? AzureDevOpsIdentity.UnknownName,
            html,
            dto.CreatedDate,
            modifiedAt,
            IdentityImage.From(dto.CreatedBy?.ImageUrl, dto.CreatedBy?.Links).Data);
    }

    /// <summary>Writes a comment in the work item's discussion.</summary>
    /// <param name="remote">The project.</param>
    /// <param name="id">The work item.</param>
    /// <param name="html">The text, as HTML.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The comment as it was saved.</returns>
    public async Task<Result<WorkItemComment>> AddCommentAsync(
        AzureDevOpsRemote remote,
        int id,
        string html,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<WorkItemComment>.Invalid(NoProject);
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            return Result<WorkItemComment>.Invalid("The comment is empty. Pass the comment's text, as HTML.");
        }

        string number = id.ToString(CultureInfo.InvariantCulture);

        Result<CommentDto> created = await _http.PostAsync<NewCommentDto, CommentDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"wit/workItems/{number}/comments", CommentsApiVersion),
            new NewCommentDto { Text = html },
            $"work item {number}",
            cancellationToken)
            .ConfigureAwait(false);

        if (!created.IsSuccess(out CommentDto? comment))
        {
            return created.WithoutValue().AsFailure<WorkItemComment>();
        }

        Log.WorkItemCommented(_logger, id, comment.Id);

        return ToComment(comment);
    }

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
    public async Task<Result<IReadOnlyList<WorkItemRelation>>> GetRelationsAsync(
        AzureDevOpsRemote remote,
        int id,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<IReadOnlyList<WorkItemRelation>>.Invalid(NoProject);
        }

        Result<WorkItemDto> fetched = await GetWithRelationsAsync(remote, id, cancellationToken).ConfigureAwait(false);

        if (!fetched.IsSuccess(out WorkItemDto? dto))
        {
            return fetched.WithoutValue().AsFailure<IReadOnlyList<WorkItemRelation>>();
        }

        List<WorkItemRelation> relations = [];

        foreach (WorkItemRelationDto relation in dto.Relations)
        {
            if (ToRelation(relation).IsSuccess(out WorkItemRelation? found))
            {
                relations.Add(found);
            }
        }

        return Result<IReadOnlyList<WorkItemRelation>>.Success(relations);
    }

    /// <summary>Links a work item to another.</summary>
    /// <param name="remote">The project.</param>
    /// <param name="id">The work item the link starts from.</param>
    /// <param name="relation">What it is to be linked to, and how.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result> LinkAsync(
        AzureDevOpsRemote remote,
        int id,
        WorkItemRelation relation,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result.Invalid(NoProject);
        }

        if (relation is null)
        {
            return Result.Invalid(
                "No link was given. Pass a WorkItemRelation with the kind of link and the other work item's number.");
        }

        PatchOperationDto[] patch =
        [
            new()
            {
                Op = "add",
                Path = "/relations/-",
                Value = new WorkItemRelationDto
                {
                    Rel = RelationName(relation.Kind),
                    Url = WorkItemUrl(remote, relation.TargetId),
                },
            },
        ];

        Result<WorkItemDto> linked = await PatchAsync(remote, id, patch, cancellationToken).ConfigureAwait(false);

        if (linked.IsSuccess(out _))
        {
            Log.WorkItemLinked(_logger, id, relation.TargetId, relation.Kind.ToString());
        }

        return linked.WithoutValue();
    }

    /// <summary>Removes the links from one work item to another.</summary>
    /// <param name="remote">The project.</param>
    /// <param name="id">The work item the link starts from.</param>
    /// <param name="targetId">The other work item.</param>
    /// <param name="kind">Only links of that kind, or <c>null</c> for all.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many links were removed.</returns>
    public async Task<Result<int>> UnlinkAsync(
        AzureDevOpsRemote remote,
        int id,
        int targetId,
        WorkItemLinkKind? kind,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<int>.Invalid(NoProject);
        }

        Result<WorkItemDto> fetched = await GetWithRelationsAsync(remote, id, cancellationToken).ConfigureAwait(false);

        if (!fetched.IsSuccess(out WorkItemDto? dto))
        {
            return fetched.WithoutValue().AsFailure<int>();
        }

        // The links are removed by their index in the list. The indexes are recalculated after each
        // removal, so they are taken from the back — and the server checks the revision, so that
        // no one else's change can move them in between.
        int[] indexes =
        [
            .. dto.Relations
                .Select((relation, index) => (Relation: ToRelation(relation), Index: index))
                .Where(pair => pair.Relation.IsSuccess(out WorkItemRelation? found)
                    && found.TargetId == targetId
                    && (kind is null || found.Kind == kind))
                .Select(pair => pair.Index)
                .OrderDescending(),
        ];

        if (indexes.Length == 0)
        {
            return 0;
        }

        List<PatchOperationDto> patch = [new() { Op = "test", Path = "/rev", Value = dto.Rev }];

        patch.AddRange(indexes.Select(index => new PatchOperationDto
        {
            Op = "remove",
            Path = "/relations/" + index.ToString(CultureInfo.InvariantCulture),
            Value = null,
        }));

        Result<WorkItemDto> unlinked = await PatchAsync(remote, id, patch, cancellationToken).ConfigureAwait(false);

        if (!unlinked.IsSuccess(out _))
        {
            return unlinked.WithoutValue().AsFailure<int>();
        }

        Log.WorkItemUnlinked(_logger, id, targetId, indexes.Length);

        return indexes.Length;
    }

    /// <summary>A work item with its links to other work items, branches and pull requests.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The work item with its relations, or why it could not be read.</returns>
    private Task<Result<WorkItemDto>> GetWithRelationsAsync(AzureDevOpsRemote remote, int id, CancellationToken cancellationToken) =>
        _http.GetAsync<WorkItemDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id.ToString(CultureInfo.InvariantCulture)}?$expand=relations"),
            $"work item {id.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);

    /// <summary>Changes a work item with a JSON Patch, the only form the API accepts for it.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="id">The work item's number.</param>
    /// <param name="patch">The operations to apply.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The work item as it looks afterwards, or why it could not be changed.</returns>
    private Task<Result<WorkItemDto>> PatchAsync(
        AzureDevOpsRemote remote,
        int id,
        IReadOnlyList<PatchOperationDto> patch,
        CancellationToken cancellationToken) =>
        _http.PostAsync<WorkItemDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id.ToString(CultureInfo.InvariantCulture)}"),
            JsonSerializer.Serialize(patch, AzureDevOpsJson.Options),
            JsonPatch,
            $"work item {id.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken,
            HttpMethod.Patch);

    /// <summary>The link type's name in the API.</summary>
    /// <param name="kind">The kind of link.</param>
    /// <returns>The reference name of the link type.</returns>
    internal static string RelationName(WorkItemLinkKind kind) => kind switch
    {
        WorkItemLinkKind.Parent => "System.LinkTypes.Hierarchy-Reverse",
        WorkItemLinkKind.Child => "System.LinkTypes.Hierarchy-Forward",
        WorkItemLinkKind.Duplicate => "System.LinkTypes.Duplicate-Forward",
        WorkItemLinkKind.DuplicateOf => "System.LinkTypes.Duplicate-Reverse",
        WorkItemLinkKind.Predecessor => "System.LinkTypes.Dependency-Reverse",
        WorkItemLinkKind.Successor => "System.LinkTypes.Dependency-Forward",
        _ => "System.LinkTypes.Related",
    };

    /// <summary>
    /// A link to another work item, or <see cref="ResultStatus.NotFound"/> for everything
    /// else — branches, pull requests, attachments and link types the tool does not know.
    /// </summary>
    /// <param name="dto">A relation from the API.</param>
    /// <returns>The link, or <see cref="ResultStatus.NotFound"/> when it does not point at a work item.</returns>
    private static Result<WorkItemRelation> ToRelation(WorkItemRelationDto dto)
    {
        foreach (WorkItemLinkKind kind in Enum.GetValues<WorkItemLinkKind>())
        {
            if (string.Equals(RelationName(kind), dto.Rel, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(dto.Url.TrimEnd('/').Split('/')[^1], CultureInfo.InvariantCulture, out int target))
            {
                return new WorkItemRelation(kind, target);
            }
        }

        return Result<WorkItemRelation>.NotFound($"{dto.Rel} is not a link to a work item.");
    }

    /// <summary>A work item's address in the API, which is what a link to it points at.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="id">The work item's number.</param>
    /// <returns>The absolute address.</returns>
    private static string WorkItemUrl(AzureDevOpsRemote remote, int id) =>
        new Uri(remote.ApiBaseUri, $"_apis/wit/workItems/{id.ToString(CultureInfo.InvariantCulture)}").AbsoluteUri;

    /// <summary>Creates a work item and returns what was created.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="item">The work item to create.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<WorkItemSummary>> CreateAsync(
        AzureDevOpsRemote remote,
        NewWorkItem item,
        CancellationToken cancellationToken = default)
    {
        if (remote is null)
        {
            return Result<WorkItemSummary>.Invalid(NoProject);
        }

        if (item is null)
        {
            return Result<WorkItemSummary>.Invalid("No work item was given. Pass a NewWorkItem with at least a type and a title.");
        }

        // The title is the only thing Azure DevOps requires, and the type is in the address. Without them
        // the response is a 400 that does not say what was missing.
        if (string.IsNullOrWhiteSpace(item.Type) || string.IsNullOrWhiteSpace(item.Title))
        {
            return Result<WorkItemSummary>.Invalid("A work item needs both a type and a title.");
        }

        List<PatchOperationDto> operations = BuildPatch(remote, item);

        if (item.StoryPoints is { } points)
        {
            Result<string> estimateField = await EstimateFieldAsync(remote, item.Type, cancellationToken).ConfigureAwait(false);

            if (!estimateField.IsSuccess(out string? field))
            {
                return estimateField.WithoutValue().AsFailure<WorkItemSummary>();
            }

            operations.Add(new PatchOperationDto { Op = "add", Path = "/fields/" + field, Value = points });
        }

        string patch = JsonSerializer.Serialize(operations, AzureDevOpsJson.Options);

        // The type is in the path with a dollar sign before it. The name can contain
        // spaces ("User Story") and must be encoded, but the dollar sign must stay.
        Result<WorkItemDto> created = await _http.PostAsync<WorkItemDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/${Uri.EscapeDataString(item.Type)}"),
            patch,
            JsonPatch,
            $"work items of the type {item.Type}",
            cancellationToken)
            .ConfigureAwait(false);

        if (!created.IsSuccess(out WorkItemDto? dto))
        {
            return created.WithoutValue().AsFailure<WorkItemSummary>();
        }

        Log.WorkItemCreated(_logger, dto.Id, item.Type, remote.Project);

        return ToSummary(remote, dto);
    }

    // ------------------------------------------------------------------ the query

    /// <summary>
    /// The query that fetches the project's work items.
    /// </summary>
    /// <param name="includeClosed">Whether closed and removed work items are included.</param>
    /// <param name="assignedToMe">Whether only the signed-in user's work items are included.</param>
    /// <returns>The WIQL statement.</returns>
    /// <remarks>
    /// Nothing that comes from outside goes into the text. The types and states are fixed
    /// lists in this file, and the project is given as the macro <c>@project</c> — the server
    /// fills it in from the address. A WIQL statement assembled from the user's text would be
    /// the same kind of hole as an SQL statement that is.
    /// </remarks>
    internal static string BuildWiql(bool includeClosed, bool assignedToMe = false)
    {
        StringBuilder wiql = new();

        wiql.Append("SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project");
        wiql.Append(" AND [System.WorkItemType] NOT IN (").Append(Quote(ExcludedTypes)).Append(')');

        if (!includeClosed)
        {
            wiql.Append(" AND [System.State] NOT IN (").Append(Quote(FinishedStates)).Append(')');
        }

        // @Me is filled in by the server with whoever owns the token. It is the only way to know
        // who "me" is without first asking for the identity.
        if (assignedToMe)
        {
            wiql.Append(" AND [System.AssignedTo] = @Me");
        }

        wiql.Append(" ORDER BY [System.ChangedDate] DESC");

        return wiql.ToString();
    }

    /// <summary>The query for what sits directly under a work item, oldest first.</summary>
    /// <param name="parentId">The number of the work item they sit under.</param>
    /// <returns>The WIQL statement.</returns>
    internal static string BuildChildrenWiql(int parentId) =>
        "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project"
            + $" AND [System.Parent] = {parentId.ToString(CultureInfo.InvariantCulture)}"
            + " ORDER BY [System.Id] ASC";

    /// <summary>Values as a WIQL list: <c>'Closed', 'Removed'</c>.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The values quoted and separated by commas.</returns>
    private static string Quote(IEnumerable<string> values) =>
        string.Join(", ", values.Select(value => $"'{value}'"));

    /// <summary>The fields of a new work item as JSON Patch, with only the fields that are given.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="item">The work item to create.</param>
    /// <returns>The operations, ready to be sent.</returns>
    private static List<PatchOperationDto> BuildPatch(AzureDevOpsRemote remote, NewWorkItem item)
    {
        List<PatchOperationDto> patch = [Add(FieldNames.Title, item.Title.Trim())];

        // An empty field must not be sent at all. Azure DevOps distinguishes between "no value" and
        // "empty text", and the latter makes the field filled in with nothing.
        AddIfGiven(patch, FieldNames.Description, item.Description);
        AddIfGiven(patch, FieldNames.ReproSteps, item.ReproSteps);
        AddIfGiven(patch, FieldNames.AcceptanceCriteria, item.AcceptanceCriteria);
        AddIfGiven(patch, FieldNames.AssignedTo, item.AssignedTo);
        AddIfGiven(patch, FieldNames.AreaPath, item.AreaPath);
        AddIfGiven(patch, FieldNames.IterationPath, item.IterationPath);

        if (item.Tags is { Count: > 0 } tags)
        {
            AddIfGiven(patch, FieldNames.Tags, string.Join("; ", tags.Where(tag => !string.IsNullOrWhiteSpace(tag))));
        }

        // The parent is a link and not a field. System.Parent can be read but not
        // written; what is written is a hierarchy link from the child up to the parent.
        if (item.ParentId is { } parent)
        {
            patch.Add(new PatchOperationDto
            {
                Op = "add",
                Path = "/relations/-",
                Value = new WorkItemRelationDto
                {
                    Rel = RelationName(WorkItemLinkKind.Parent),
                    Url = WorkItemUrl(remote, parent),
                },
            });
        }

        return patch;
    }

    /// <summary>Adds a field to the patch only when it has a value; empty text is not sent.</summary>
    /// <param name="patch">The patch to add to.</param>
    /// <param name="field">The field's reference name, from <see cref="FieldNames"/>.</param>
    /// <param name="value">The value, or <c>null</c> or blank for none.</param>
    private static void AddIfGiven(List<PatchOperationDto> patch, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            patch.Add(Add(field, value));
        }
    }

    /// <summary>An operation that sets a field.</summary>
    /// <param name="field">The field's reference name, from <see cref="FieldNames"/>.</param>
    /// <param name="value">The value to set.</param>
    /// <returns>The operation.</returns>
    private static PatchOperationDto Add(string field, string value) =>
        new() { Op = "add", Path = "/fields/" + field, Value = value };

    // ------------------------------------------------------------------ mapping

    /// <summary>A work item as the list needs it, from the API's fields.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="dto">The item from the API.</param>
    /// <returns>The work item.</returns>
    private static WorkItemSummary ToSummary(AzureDevOpsRemote remote, WorkItemDto dto)
    {
        int? parentId = null;
        double? storyPoints = null;

        if (Number(dto, FieldNames.Parent).IsSuccess(out int parent))
        {
            parentId = parent;
        }

        if (Estimate(dto).IsSuccess(out double points))
        {
            storyPoints = points;
        }

        return new WorkItemSummary(
            dto.Id,
            Text(dto, FieldNames.WorkItemType),
            Text(dto, FieldNames.Title),
            Text(dto, FieldNames.State),
            Identity(dto, FieldNames.AssignedTo),
            Text(dto, FieldNames.IterationPath),
            Text(dto, FieldNames.AreaPath),
            Tags(Text(dto, FieldNames.Tags)),
            parentId,
            Moment(dto, FieldNames.ChangedDate),
            new Uri(
                remote.ApiBaseUri,
                $"{Uri.EscapeDataString(remote.Project)}/_workitems/edit/{dto.Id.ToString(CultureInfo.InvariantCulture)}"),
            Text(dto, FieldNames.BoardColumn),
            storyPoints,
            ImageUrl(dto, FieldNames.AssignedTo).Data);
    }

    /// <summary>The estimate from whichever of the fields has a value.</summary>
    /// <param name="dto">The work item from the API.</param>
    /// <returns>The estimate, or <see cref="ResultStatus.NotFound"/> when none of the fields has one.</returns>
    private static Result<double> Estimate(WorkItemDto dto)
    {
        foreach (string field in EstimateFields)
        {
            if (dto.Fields.TryGetValue(field, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out double points))
            {
                return points;
            }
        }

        return Result<double>.NotFound("No estimate.");
    }

    /// <summary>The tags, as Azure DevOps writes them: separated by semicolons.</summary>
    /// <param name="value">The field's text.</param>
    /// <returns>The tags, without surrounding whitespace.</returns>
    private static IReadOnlyList<string> Tags(string value) =>
    [
        .. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    ];

    /// <summary>A text field, or an empty string when the work item lacks it.</summary>
    /// <param name="dto">The work item from the API.</param>
    /// <param name="field">The field's reference name, from <see cref="FieldNames"/>.</param>
    /// <returns>The field's text.</returns>
    private static string Text(WorkItemDto dto, string field)
    {
        if (dto.Fields.TryGetValue(field, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// The display name from a person field.
    /// </summary>
    /// <param name="dto">The work item from the API.</param>
    /// <param name="field">The field's reference name, from <see cref="FieldNames"/>.</param>
    /// <returns>The display name, or an empty string when no one is assigned.</returns>
    /// <remarks>
    /// The field is an object when someone is assigned and missing entirely otherwise. Older
    /// installations send only a string, and that works just as well.
    /// </remarks>
    private static string Identity(WorkItemDto dto, string field)
    {
        if (!dto.Fields.TryGetValue(field, out JsonElement value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Object when value.TryGetProperty("displayName", out JsonElement name) =>
                name.GetString() ?? string.Empty,
            JsonValueKind.Object when value.TryGetProperty("uniqueName", out JsonElement unique) =>
                unique.GetString() ?? string.Empty,
            _ => string.Empty,
        };
    }

    /// <summary>The profile picture's address from a person field.</summary>
    /// <param name="dto">The work item from the API.</param>
    /// <param name="field">The field's reference name, from <see cref="FieldNames"/>.</param>
    /// <returns>The address, or <see cref="ResultStatus.NotFound"/> when no one is assigned or the server sent only a name.</returns>
    private static Result<Uri> ImageUrl(WorkItemDto dto, string field)
    {
        if (!dto.Fields.TryGetValue(field, out JsonElement value))
        {
            return Result<Uri>.NotFound($"{field} is missing.");
        }

        return IdentityImage.From(value);
    }

    /// <summary>A whole-number field, or <see cref="ResultStatus.NotFound"/> when the work item lacks it.</summary>
    /// <param name="dto">The work item from the API.</param>
    /// <param name="field">The field's reference name, from <see cref="FieldNames"/>.</param>
    /// <returns>The number, or <see cref="ResultStatus.NotFound"/>.</returns>
    private static Result<int> Number(WorkItemDto dto, string field)
    {
        if (dto.Fields.TryGetValue(field, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number))
        {
            return number;
        }

        return Result<int>.NotFound($"{field} is missing.");
    }

    /// <summary>A date field, or <see cref="DateTime"/>'s default when the work item lacks it.</summary>
    /// <param name="dto">The work item from the API.</param>
    /// <param name="field">The field's reference name, from <see cref="FieldNames"/>.</param>
    /// <returns>The date.</returns>
    private static DateTime Moment(WorkItemDto dto, string field)
    {
        if (dto.Fields.TryGetValue(field, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTime(out DateTime moment))
        {
            return moment;
        }

        return default;
    }
}

// ---------------------------------------------------------------------- the shape of the responses

internal sealed record WiqlQueryDto
{
    [JsonPropertyName("query")]
    public string Query { get; init; } = string.Empty;
}

internal sealed record WiqlResultDto
{
    [JsonPropertyName("workItems")]
    public IReadOnlyList<WiqlReferenceDto> WorkItems { get; init; } = [];
}

internal sealed record WiqlReferenceDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
}

internal sealed record WorkItemBatchDto
{
    [JsonPropertyName("ids")]
    public IReadOnlyList<int> Ids { get; init; } = [];

    [JsonPropertyName("fields")]
    public IReadOnlyList<string> Fields { get; init; } = [];
}

internal sealed record WorkItemListDto
{
    [JsonPropertyName("value")]
    public IReadOnlyList<WorkItemDto> Value { get; init; } = [];
}

internal sealed record WorkItemDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("rev")]
    public int Rev { get; init; }

    [JsonPropertyName("fields")]
    public IReadOnlyDictionary<string, JsonElement> Fields { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>The links. Only included when requested with <c>$expand=relations</c>.</summary>
    [JsonPropertyName("relations")]
    public IReadOnlyList<WorkItemRelationDto> Relations { get; init; } = [];
}

internal sealed record WorkItemTypeFieldDto
{
    [JsonPropertyName("referenceName")]
    public string? ReferenceName { get; init; }
}

internal sealed record TagDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed record NewCommentDto
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}

internal sealed record CommentListDto
{
    [JsonPropertyName("comments")]
    public IReadOnlyList<CommentDto> Comments { get; init; } = [];

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; init; }
}

internal sealed record CommentDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("renderedText")]
    public string? RenderedText { get; init; }

    [JsonPropertyName("createdBy")]
    public IdentityRef? CreatedBy { get; init; }

    [JsonPropertyName("createdDate")]
    public DateTime CreatedDate { get; init; }

    [JsonPropertyName("modifiedDate")]
    public DateTime? ModifiedDate { get; init; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; init; }
}

internal sealed record BoardReferenceDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed record BoardDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("columns")]
    public IReadOnlyList<BoardColumnDto> Columns { get; init; } = [];
}

internal sealed record BoardColumnDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("itemLimit")]
    public int ItemLimit { get; init; }

    /// <summary>
    /// The board's entry, middle or exit: <c>incoming</c>, <c>inProgress</c>, <c>outgoing</c>.
    /// </summary>
    [JsonPropertyName("columnType")]
    public string? ColumnType { get; init; }
}

internal sealed record TeamIterationDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("attributes")]
    public TeamIterationAttributesDto? Attributes { get; init; }
}

internal sealed record TeamIterationAttributesDto
{
    [JsonPropertyName("startDate")]
    public DateTime? StartDate { get; init; }

    [JsonPropertyName("finishDate")]
    public DateTime? FinishDate { get; init; }
}

internal sealed record PatchOperationDto
{
    [JsonPropertyName("op")]
    public string Op { get; init; } = "add";

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Text for a field, a <see cref="WorkItemRelationDto"/> for a link, the revision for
    /// <c>test</c>, and nothing at all for <c>remove</c>.
    /// </summary>
    [JsonPropertyName("value")]
    public object? Value { get; init; } = string.Empty;
}

internal sealed record WorkItemRelationDto
{
    [JsonPropertyName("rel")]
    public string Rel { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;
}
