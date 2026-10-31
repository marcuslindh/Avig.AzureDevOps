using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
public sealed class AzureDevOpsWorkItemClient : IWorkItemClient
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

    private readonly AzureDevOpsHttp _http;
    private readonly IAzureDevOpsCredential _credential;
    private readonly ILogger<AzureDevOpsWorkItemClient> _logger;

    /// <summary>
    /// The estimate field per type and project, or empty text for a type without one.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _estimateFields = new(StringComparer.OrdinalIgnoreCase);

    public AzureDevOpsWorkItemClient(
        HttpClient httpClient,
        IAzureDevOpsCredential credential,
        ILogger<AzureDevOpsWorkItemClient> logger)
    {
        _credential = credential;
        _logger = logger;
        _http = new AzureDevOpsHttp(httpClient, credential, logger);
    }

    public bool IsConnected => _credential.IsConfigured;

    public Task<Result<IReadOnlyList<WorkItemSummary>>> GetAsync(
        AzureDevOpsRemote remote,
        WorkItemQuery query,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<WorkItemSummary>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentNullException.ThrowIfNull(query);

            int limit = Math.Clamp(query.Limit, 1, MaxListed);

            WiqlResultDto result = await _http.PostAsync<WiqlQueryDto, WiqlResultDto>(
                    AzureDevOpsHttp.ProjectRoute(remote, $"wit/wiql?$top={limit}"),
                    new WiqlQueryDto { Query = BuildWiql(query.IncludeClosed, query.AssignedToMe) },
                    "the project's work items",
                    cancellationToken)
                .ConfigureAwait(false);

            // WIQL only returns numbers. The order is the one the query asked for and must hold all the way,
            // so fetching the fields must not shuffle it.
            int[] ids = [.. result.WorkItems.Select(item => item.Id).Take(limit)];

            IReadOnlyList<WorkItemSummary> items = await FetchAsync(remote, ids, cancellationToken).ConfigureAwait(false);

            Log.WorkItemsLoaded(_logger, items.Count, remote.Project);

            return items;
        }, cancellationToken);

    public Task<Result<IReadOnlyList<WorkItemSummary>>> GetChildrenAsync(
        AzureDevOpsRemote remote,
        int parentId,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<WorkItemSummary>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            // The number is an integer and is written in as one; no text from outside ends up in the query.
            WiqlResultDto result = await _http.PostAsync<WiqlQueryDto, WiqlResultDto>(
                    AzureDevOpsHttp.ProjectRoute(remote, "wit/wiql"),
                    new WiqlQueryDto { Query = BuildChildrenWiql(parentId) },
                    $"the work items under {parentId.ToString(CultureInfo.InvariantCulture)}",
                    cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<WorkItemSummary> children = await FetchAsync(
                    remote,
                    [.. result.WorkItems.Select(item => item.Id)],
                    cancellationToken)
                .ConfigureAwait(false);

            Log.WorkItemChildrenLoaded(_logger, children.Count, parentId);

            return children;
        }, cancellationToken);

    /// <summary>
    /// The fields for a list of numbers, in batches and in the same order as the numbers.
    /// </summary>
    private async Task<IReadOnlyList<WorkItemSummary>> FetchAsync(
        AzureDevOpsRemote remote,
        int[] ids,
        CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        List<WorkItemSummary> items = new(ids.Length);

        for (int offset = 0; offset < ids.Length; offset += BatchSize)
        {
            int[] page = ids[offset..Math.Min(offset + BatchSize, ids.Length)];

            WorkItemListDto batch = await _http.PostAsync<WorkItemBatchDto, WorkItemListDto>(
                    AzureDevOpsHttp.ProjectRoute(remote, "wit/workitemsbatch"),
                    new WorkItemBatchDto { Ids = page, Fields = ListFields },
                    "the project's work items",
                    cancellationToken)
                .ConfigureAwait(false);

            items.AddRange(batch.Value.Select(dto => ToSummary(remote, dto)));
        }

        Dictionary<int, WorkItemSummary> byId = items.ToDictionary(item => item.Id);

        return [.. ids.Where(byId.ContainsKey).Select(id => byId[id])];
    }

    public Task<Result<WorkItemIteration>> GetCurrentIterationAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunResultAsync<WorkItemIteration>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            try
            {
                // Without a team in the address the API answers for the project's default team, which
                // is the same sprint the web interface shows when nothing else has been chosen.
                ListResponse<TeamIterationDto> response = await _http.GetAsync<ListResponse<TeamIterationDto>>(
                        AzureDevOpsHttp.ProjectRoute(remote, "work/teamsettings/iterations?$timeframe=current"),
                        "the project's sprints",
                        cancellationToken)
                    .ConfigureAwait(false);

                if (response.Value.Count == 0)
                {
                    return Result<WorkItemIteration>.NotFound($"{remote.Project} has no ongoing sprint.");
                }

                TeamIterationDto dto = response.Value[0];

                Log.CurrentIterationLoaded(_logger, dto.Name ?? "?", remote.Project);

                return new WorkItemIteration(
                    dto.Name ?? string.Empty,
                    dto.Path ?? string.Empty,
                    dto.Attributes?.StartDate,
                    dto.Attributes?.FinishDate);
            }
            catch (AzureDevOpsException exception)
            {
                // A project without sprints, a team that has no ongoing one, or a
                // token without access to them. None of that is worth bringing down the whole view for
                // — the quick filter is then drawn disabled instead.
                Log.CurrentIterationUnavailable(_logger, exception, remote.Project);

                return Result<WorkItemIteration>.Unavailable(exception.Message);
            }
        }, cancellationToken);

    public Task<Result<BoardDefinition>> GetBoardAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunResultAsync<BoardDefinition>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            try
            {
                // Without a team in the address the API answers for the project's default team.
                ListResponse<BoardReferenceDto> boards = await _http.GetAsync<ListResponse<BoardReferenceDto>>(
                        AzureDevOpsHttp.ProjectRoute(remote, "work/boards"),
                        "the project's boards",
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!Pick(boards.Value).IsSuccess(out BoardReferenceDto? reference))
                {
                    return Result<BoardDefinition>.NotFound($"{remote.Project} has no board.");
                }

                BoardDto board = await _http.GetAsync<BoardDto>(
                        AzureDevOpsHttp.ProjectRoute(remote, $"work/boards/{Uri.EscapeDataString(reference.Id ?? string.Empty)}"),
                        "the project's board",
                        cancellationToken)
                    .ConfigureAwait(false);

                BoardDefinition definition = new(
                    board.Name ?? reference.Name ?? "Board",
                    [.. board.Columns.Select(column => new BoardColumn(
                        column.Name ?? string.Empty,
                        column.ItemLimit,
                        string.Equals(column.ColumnType, "incoming", StringComparison.OrdinalIgnoreCase),
                        string.Equals(column.ColumnType, "outgoing", StringComparison.OrdinalIgnoreCase)))]);

                Log.BoardLoaded(_logger, definition.Name, definition.Columns.Count, remote.Project);

                return definition;
            }
            catch (AzureDevOpsException exception)
            {
                Log.BoardUnavailable(_logger, exception, remote.Project);

                return Result<BoardDefinition>.Unavailable(exception.Message);
            }
        }, cancellationToken);

    /// <summary>
    /// The board the requirements are on.
    /// </summary>
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

    public Task<Result<WorkItemSummary>> MoveAsync(
        AzureDevOpsRemote remote,
        WorkItemSummary item,
        string column,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<WorkItemSummary>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(column);

            // The board column for the types that have one, the state for those that do not.
            // A task is not on the kanban board and therefore has no column to write.
            string field = FieldNames.BoardColumn;

            if (item.MovesByState)
            {
                field = FieldNames.State;
            }

            string patch = JsonSerializer.Serialize(
                new[] { new PatchOperationDto { Op = "add", Path = "/fields/" + field, Value = column } },
                AzureDevOpsJson.Options);

            WorkItemDto moved = await _http.PostAsync<WorkItemDto>(
                    AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{item.Id.ToString(CultureInfo.InvariantCulture)}"),
                    patch,
                    "application/json-patch+json",
                    $"work item {item.Id.ToString(CultureInfo.InvariantCulture)}",
                    cancellationToken,
                    HttpMethod.Patch)
                .ConfigureAwait(false);

            Log.WorkItemMoved(_logger, item.Id, column, field);

            return ToSummary(remote, moved);
        }, cancellationToken);

    public Task<Result<WorkItemDetails>> UpdateAsync(
        AzureDevOpsRemote remote,
        int id,
        WorkItemEdit edit,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<WorkItemDetails>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentNullException.ThrowIfNull(edit);

            if (string.IsNullOrWhiteSpace(edit.Title))
            {
                throw new AzureDevOpsException("A work item must have a title.") { StatusCode = HttpStatusCode.BadRequest };
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
                patch.AddRange(await EstimatePatchAsync(remote, id, edit, cancellationToken).ConfigureAwait(false));
            }

            WorkItemDto updated = await _http.PostAsync<WorkItemDto>(
                    AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id.ToString(CultureInfo.InvariantCulture)}"),
                    JsonSerializer.Serialize(patch, AzureDevOpsJson.Options),
                    "application/json-patch+json",
                    $"work item {id.ToString(CultureInfo.InvariantCulture)}",
                    cancellationToken,
                    HttpMethod.Patch)
                .ConfigureAwait(false);

            Log.WorkItemUpdated(_logger, id, patch.Count);

            return new WorkItemDetails(
                ToSummary(remote, updated),
                Text(updated, FieldNames.Description),
                Text(updated, FieldNames.ReproSteps),
                Text(updated, FieldNames.AcceptanceCriteria));
        }, cancellationToken);

    /// <summary>
    /// The change to the estimate, in the field the type has.
    /// </summary>
    /// <remarks>
    /// The type must be read first: which field applies depends on it, and the change only
    /// says what the value should be. Removing a value that does not exist is an error to the
    /// server, so it is only done when there is something to remove.
    /// </remarks>
    private async Task<IReadOnlyList<PatchOperationDto>> EstimatePatchAsync(
        AzureDevOpsRemote remote,
        int id,
        WorkItemEdit edit,
        CancellationToken cancellationToken)
    {
        string number = id.ToString(CultureInfo.InvariantCulture);
        string fields = string.Join(',', EstimateFields.Prepend(FieldNames.WorkItemType));

        WorkItemDto current = await _http.GetAsync<WorkItemDto>(
                AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{number}?fields={Uri.EscapeDataString(fields)}"),
                $"work item {number}",
                cancellationToken)
            .ConfigureAwait(false);

        string field = await EstimateFieldAsync(remote, Text(current, FieldNames.WorkItemType), cancellationToken)
            .ConfigureAwait(false);

        if (edit.StoryPoints is { } points)
        {
            return [new PatchOperationDto { Op = "add", Path = "/fields/" + field, Value = points }];
        }

        if (current.Fields.ContainsKey(field))
        {
            return [new PatchOperationDto { Op = "remove", Path = "/fields/" + field, Value = null }];
        }

        return [];
    }

    /// <summary>
    /// The field that carries the estimate for a type.
    /// </summary>
    /// <exception cref="AzureDevOpsException">When the type has none of them.</exception>
    /// <remarks>
    /// The type's fields are read from the server once and stored. Guessing from the type's
    /// name does not work: a bug has Story Points in Agile, Effort in Scrum and neither in CMMI.
    /// </remarks>
    private async Task<string> EstimateFieldAsync(AzureDevOpsRemote remote, string type, CancellationToken cancellationToken)
    {
        string key = $"{remote.Organization}/{remote.Project}/{type}";

        if (!_estimateFields.TryGetValue(key, out string? field))
        {
            ListResponse<WorkItemTypeFieldDto> response = await _http.GetAsync<ListResponse<WorkItemTypeFieldDto>>(
                    AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitemtypes/{Uri.EscapeDataString(type)}/fields"),
                    $"the fields of {type}",
                    cancellationToken)
                .ConfigureAwait(false);

            HashSet<string> available = new(
                response.Value.Select(dto => dto.ReferenceName ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            field = EstimateFields.FirstOrDefault(available.Contains) ?? string.Empty;
            _estimateFields[key] = field;
        }

        if (field.Length > 0)
        {
            return field;
        }

        throw new AzureDevOpsException($"The type {type} has no story points in the project's process.");
    }

    public Task<Result<IReadOnlyList<string>>> GetTagsAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<string>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            ListResponse<TagDto> response = await _http.GetAsync<ListResponse<TagDto>>(
                    AzureDevOpsHttp.ProjectRoute(remote, "wit/tags", TagsApiVersion),
                    "the project's tags",
                    cancellationToken)
                .ConfigureAwait(false);

            return
            [
                .. response.Value
                    .Select(tag => tag.Name ?? string.Empty)
                    .Where(name => name.Length > 0)
                    .Order(StringComparer.CurrentCultureIgnoreCase),
            ];
        }, cancellationToken);

    public Task<Result<WorkItemDetails>> GetDetailsAsync(
        AzureDevOpsRemote remote,
        int id,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<WorkItemDetails>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            string fields = string.Join(',', ListFields.Concat(TextFields));

            WorkItemDto dto = await _http.GetAsync<WorkItemDto>(
                    AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id}?fields={Uri.EscapeDataString(fields)}"),
                    $"work item {id.ToString(CultureInfo.InvariantCulture)}",
                    cancellationToken)
                .ConfigureAwait(false);

            return new WorkItemDetails(
                ToSummary(remote, dto),
                Text(dto, FieldNames.Description),
                Text(dto, FieldNames.ReproSteps),
                Text(dto, FieldNames.AcceptanceCriteria));
        }, cancellationToken);

    public Task<Result<IReadOnlyList<WorkItemComment>>> GetCommentsAsync(
        AzureDevOpsRemote remote,
        int id,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<WorkItemComment>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

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

                CommentListDto page = await _http.GetAsync<CommentListDto>(
                        AzureDevOpsHttp.ProjectRoute(remote, relative, CommentsApiVersion),
                        $"work item {number}",
                        cancellationToken)
                    .ConfigureAwait(false);

                comments.AddRange(page.Comments.Where(comment => !comment.IsDeleted).Select(ToComment));

                // The server sends an empty token, not none, on the last page.
                continuation = null;

                if (!string.IsNullOrEmpty(page.ContinuationToken))
                {
                    continuation = page.ContinuationToken;
                }
            }
            while (continuation is not null);

            return comments;
        }, cancellationToken);

    /// <summary>
    /// A comment as it is to be displayed.
    /// </summary>
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
            modifiedAt);
    }

    public Task<Result<WorkItemComment>> AddCommentAsync(
        AzureDevOpsRemote remote,
        int id,
        string html,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<WorkItemComment>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentException.ThrowIfNullOrWhiteSpace(html);

            string number = id.ToString(CultureInfo.InvariantCulture);

            CommentDto created = await _http.PostAsync<NewCommentDto, CommentDto>(
                    AzureDevOpsHttp.ProjectRoute(remote, $"wit/workItems/{number}/comments", CommentsApiVersion),
                    new NewCommentDto { Text = html },
                    $"work item {number}",
                    cancellationToken)
                .ConfigureAwait(false);

            Log.WorkItemCommented(_logger, id, created.Id);

            return ToComment(created);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<WorkItemRelation>>> GetRelationsAsync(
        AzureDevOpsRemote remote,
        int id,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<IReadOnlyList<WorkItemRelation>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            WorkItemDto dto = await GetWithRelationsAsync(remote, id, cancellationToken).ConfigureAwait(false);

            List<WorkItemRelation> relations = [];

            foreach (WorkItemRelationDto relation in dto.Relations)
            {
                if (ToRelation(relation).IsSuccess(out WorkItemRelation? found))
                {
                    relations.Add(found);
                }
            }

            return relations;
        }, cancellationToken);

    public Task<Result> LinkAsync(
        AzureDevOpsRemote remote,
        int id,
        WorkItemRelation relation,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunVoidAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentNullException.ThrowIfNull(relation);

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

            await PatchAsync(remote, id, patch, cancellationToken).ConfigureAwait(false);

            Log.WorkItemLinked(_logger, id, relation.TargetId, relation.Kind.ToString());
        }, cancellationToken);

    public Task<Result<int>> UnlinkAsync(
        AzureDevOpsRemote remote,
        int id,
        int targetId,
        WorkItemLinkKind? kind,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<int>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);

            WorkItemDto dto = await GetWithRelationsAsync(remote, id, cancellationToken).ConfigureAwait(false);

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

            await PatchAsync(remote, id, patch, cancellationToken).ConfigureAwait(false);

            Log.WorkItemUnlinked(_logger, id, targetId, indexes.Length);

            return indexes.Length;
        }, cancellationToken);

    private Task<WorkItemDto> GetWithRelationsAsync(AzureDevOpsRemote remote, int id, CancellationToken cancellationToken) =>
        _http.GetAsync<WorkItemDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id.ToString(CultureInfo.InvariantCulture)}?$expand=relations"),
            $"work item {id.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);

    private Task<WorkItemDto> PatchAsync(
        AzureDevOpsRemote remote,
        int id,
        IReadOnlyList<PatchOperationDto> patch,
        CancellationToken cancellationToken) =>
        _http.PostAsync<WorkItemDto>(
            AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/{id.ToString(CultureInfo.InvariantCulture)}"),
            JsonSerializer.Serialize(patch, AzureDevOpsJson.Options),
            "application/json-patch+json",
            $"work item {id.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken,
            HttpMethod.Patch);

    /// <summary>The link type's name in the API.</summary>
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

    private static string WorkItemUrl(AzureDevOpsRemote remote, int id) =>
        new Uri(remote.ApiBaseUri, $"_apis/wit/workItems/{id.ToString(CultureInfo.InvariantCulture)}").AbsoluteUri;

    public Task<Result<WorkItemSummary>> CreateAsync(
        AzureDevOpsRemote remote,
        NewWorkItem item,
        CancellationToken cancellationToken = default) =>
        AzureDevOpsCall.RunAsync<WorkItemSummary>(async () =>
        {
            ArgumentNullException.ThrowIfNull(remote);
            ArgumentNullException.ThrowIfNull(item);
            // The title is the only thing Azure DevOps requires, and the type is in the address. Without them
            // the response is a 400 that does not say what was missing.
            if (string.IsNullOrWhiteSpace(item.Type) || string.IsNullOrWhiteSpace(item.Title))
            {
                throw new AzureDevOpsException("A work item needs both a type and a title.") { StatusCode = HttpStatusCode.BadRequest };
            }

            List<PatchOperationDto> operations = BuildPatch(remote, item);

            if (item.StoryPoints is { } points)
            {
                string field = await EstimateFieldAsync(remote, item.Type, cancellationToken).ConfigureAwait(false);

                operations.Add(new PatchOperationDto { Op = "add", Path = "/fields/" + field, Value = points });
            }

            string patch = JsonSerializer.Serialize(operations, AzureDevOpsJson.Options);

            // The type is in the path with a dollar sign before it. The name can contain
            // spaces ("User Story") and must be encoded, but the dollar sign must stay.
            WorkItemDto created = await _http.PostAsync<WorkItemDto>(
                    AzureDevOpsHttp.ProjectRoute(remote, $"wit/workitems/${Uri.EscapeDataString(item.Type)}"),
                    patch,
                    "application/json-patch+json",
                    $"work items of the type {item.Type}",
                    cancellationToken)
                .ConfigureAwait(false);

            Log.WorkItemCreated(_logger, created.Id, item.Type, remote.Project);

            return ToSummary(remote, created);
        }, cancellationToken);

    // ------------------------------------------------------------------ the query

    /// <summary>
    /// The query that fetches the project's work items.
    /// </summary>
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
    internal static string BuildChildrenWiql(int parentId) =>
        "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project"
        + $" AND [System.Parent] = {parentId.ToString(CultureInfo.InvariantCulture)}"
        + " ORDER BY [System.Id] ASC";

    private static string Quote(IEnumerable<string> values) =>
        string.Join(", ", values.Select(value => $"'{value}'"));

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

    private static void AddIfGiven(List<PatchOperationDto> patch, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            patch.Add(Add(field, value));
        }
    }

    private static PatchOperationDto Add(string field, string value) =>
        new() { Op = "add", Path = "/fields/" + field, Value = value };

    // ------------------------------------------------------------------ mapping

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
            storyPoints);
    }

    /// <summary>The estimate from whichever of the fields has a value.</summary>
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
    private static IReadOnlyList<string> Tags(string value) =>
    [
        .. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    ];

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
