using System.Text.Json;
using System.Text.Json.Serialization;

namespace Avig.AzureDevOps.PullRequests;

/// <summary>
/// The shape of what Azure DevOps sends and receives.
/// </summary>
/// <remarks>
/// Hand-written types instead of the client libraries. Microsoft.TeamFoundationServer.Client
/// exists and is maintained, but ships only for netstandard2.0, pulls in some forty packages
/// including an SQL driver, and its interactive sign-in — the whole point for a
/// desktop tool — does not work on modern .NET. Five endpoints are cheaper to
/// describe by hand.
/// </remarks>
internal static class AzureDevOpsJson
{
    /// <summary>The version that is generally available for Azure DevOps Services.</summary>
    public const string ApiVersion = "7.1";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record ListResponse<T>
{
    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("value")]
    public IReadOnlyList<T> Value { get; init; } = [];
}

internal sealed record IdentityRef
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("uniqueName")]
    public string? UniqueName { get; init; }
}

internal sealed record ReviewerDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("uniqueName")]
    public string? UniqueName { get; init; }

    [JsonPropertyName("vote")]
    public int Vote { get; init; }

    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; init; }

    [JsonPropertyName("isContainer")]
    public bool IsContainer { get; init; }
}

internal sealed record SetReviewerDto
{
    [JsonPropertyName("vote")]
    public int Vote { get; init; }

    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; init; }
}

internal sealed record CommitRefDto
{
    [JsonPropertyName("commitId")]
    public string? CommitId { get; init; }
}

internal sealed record CompletionOptionsDto
{
    [JsonPropertyName("mergeStrategy")]
    public string? MergeStrategy { get; init; }

    [JsonPropertyName("deleteSourceBranch")]
    public bool DeleteSourceBranch { get; init; }

    [JsonPropertyName("mergeCommitMessage")]
    public string? MergeCommitMessage { get; init; }

    [JsonPropertyName("transitionWorkItems")]
    public bool TransitionWorkItems { get; init; }
}

/// <summary>
/// A change to a pull request. Anything that is <c>null</c> is not sent and stays as it is.
/// </summary>
internal sealed record PullRequestStateDto
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("isDraft")]
    public bool? IsDraft { get; init; }

    [JsonPropertyName("lastMergeSourceCommit")]
    public CommitRefDto? LastMergeSourceCommit { get; init; }

    [JsonPropertyName("completionOptions")]
    public CompletionOptionsDto? CompletionOptions { get; init; }

    [JsonPropertyName("autoCompleteSetBy")]
    public IdentityRef? AutoCompleteSetBy { get; init; }
}

internal sealed record ConnectionDataDto
{
    [JsonPropertyName("authenticatedUser")]
    public ConnectionUserDto? AuthenticatedUser { get; init; }
}

internal sealed record ConnectionUserDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("providerDisplayName")]
    public string? ProviderDisplayName { get; init; }

    [JsonPropertyName("properties")]
    public JsonElement Properties { get; init; }
}

internal sealed record IdentityDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("providerDisplayName")]
    public string? ProviderDisplayName { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; } = true;

    [JsonPropertyName("isContainer")]
    public bool IsContainer { get; init; }

    [JsonPropertyName("properties")]
    public JsonElement Properties { get; init; }
}

internal sealed record WorkItemWithRelationsDto
{
    [JsonPropertyName("rev")]
    public int Rev { get; init; }

    [JsonPropertyName("relations")]
    public IReadOnlyList<ArtifactLinkDto> Relations { get; init; } = [];
}

internal sealed record PullRequestDto
{
    [JsonPropertyName("pullRequestId")]
    public int PullRequestId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("createdBy")]
    public IdentityRef? CreatedBy { get; init; }

    [JsonPropertyName("sourceRefName")]
    public string? SourceRefName { get; init; }

    [JsonPropertyName("targetRefName")]
    public string? TargetRefName { get; init; }

    [JsonPropertyName("creationDate")]
    public DateTime CreationDate { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// <c>vstfs:///Git/PullRequestId/…</c>, the address a work item links the pull request with.
    /// </summary>
    [JsonPropertyName("artifactId")]
    public string? ArtifactId { get; init; }

    [JsonPropertyName("isDraft")]
    public bool IsDraft { get; init; }

    [JsonPropertyName("mergeStatus")]
    public string? MergeStatus { get; init; }

    [JsonPropertyName("closedDate")]
    public DateTime? ClosedDate { get; init; }

    [JsonPropertyName("lastMergeSourceCommit")]
    public CommitRefDto? LastMergeSourceCommit { get; init; }

    [JsonPropertyName("autoCompleteSetBy")]
    public IdentityRef? AutoCompleteSetBy { get; init; }

    [JsonPropertyName("reviewers")]
    public IReadOnlyList<ReviewerDto> Reviewers { get; init; } = [];
}

internal sealed record CreatePullRequestDto
{
    [JsonPropertyName("sourceRefName")]
    public string SourceRefName { get; init; } = string.Empty;

    [JsonPropertyName("targetRefName")]
    public string TargetRefName { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("isDraft")]
    public bool IsDraft { get; init; }

    [JsonPropertyName("workItemRefs")]
    public IReadOnlyList<ResourceRefDto>? WorkItemRefs { get; init; }
}

/// <summary>What is rewritten when a pull request is changed.</summary>
internal sealed record UpdatePullRequestDto
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
}

/// <summary>A reference to a work item. The number is text in the API.</summary>
internal sealed record ResourceRefDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
}

/// <summary>An operation that adds a link to a work item.</summary>
internal sealed record LinkOperationDto
{
    [JsonPropertyName("op")]
    public string Op { get; init; } = "add";

    [JsonPropertyName("path")]
    public string Path { get; init; } = "/relations/-";

    [JsonPropertyName("value")]
    public ArtifactLinkDto Value { get; init; } = new();
}

internal sealed record ArtifactLinkDto
{
    [JsonPropertyName("rel")]
    public string Rel { get; init; } = "ArtifactLink";

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("attributes")]
    public ArtifactLinkAttributesDto Attributes { get; init; } = new();
}

/// <summary>
/// The name is what says what the link points to: <c>Pull Request</c>, <c>Branch</c>
/// or <c>Fixed in Commit</c>.
/// </summary>
internal sealed record ArtifactLinkAttributesDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "Pull Request";
}

/// <summary>A repo, for the ids a branch's artifact id is built from.</summary>
internal sealed record RepositoryDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("project")]
    public RepositoryProjectDto? Project { get; init; }
}

internal sealed record RepositoryProjectDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

/// <summary>A reference in the repo, for example <c>refs/heads/main</c>.</summary>
internal sealed record RefDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>The query for the work items that link to an artifact.</summary>
internal sealed record ArtifactUriQueryDto
{
    [JsonPropertyName("artifactUris")]
    public IReadOnlyList<string> ArtifactUris { get; init; } = [];
}

internal sealed record ArtifactUriQueryResponseDto
{
    [JsonPropertyName("artifactUrisQueryResult")]
    public IReadOnlyDictionary<string, IReadOnlyList<WorkItemReferenceDto>> Result { get; init; } =
        new Dictionary<string, IReadOnlyList<WorkItemReferenceDto>>(StringComparer.Ordinal);
}

/// <summary>A reference to a work item, with the number as an integer.</summary>
internal sealed record WorkItemReferenceDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
}

internal sealed record IdDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
}

internal sealed record IterationDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
}

internal sealed record ChangeItemDto
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

internal sealed record ChangeEntryDto
{
    [JsonPropertyName("changeTrackingId")]
    public int ChangeTrackingId { get; init; }

    [JsonPropertyName("changeType")]
    public string? ChangeType { get; init; }

    [JsonPropertyName("item")]
    public ChangeItemDto? Item { get; init; }
}

internal sealed record IterationChangesDto
{
    [JsonPropertyName("changeEntries")]
    public IReadOnlyList<ChangeEntryDto> ChangeEntries { get; init; } = [];

    [JsonPropertyName("nextTop")]
    public int? NextTop { get; init; }

    [JsonPropertyName("nextSkip")]
    public int? NextSkip { get; init; }
}

internal sealed record CommentPositionDto
{
    [JsonPropertyName("line")]
    public int Line { get; init; }

    /// <summary>
    /// The column, counted from one.
    /// </summary>
    /// <remarks>
    /// The documentation for 7.1 says "Starts at 0", but both the examples on the same page and
    /// the 7.2 page say one. One is what works.
    /// </remarks>
    [JsonPropertyName("offset")]
    public int Offset { get; init; }
}

internal sealed record ThreadContextDto
{
    [JsonPropertyName("filePath")]
    public string? FilePath { get; init; }

    [JsonPropertyName("rightFileStart")]
    public CommentPositionDto? RightFileStart { get; init; }

    [JsonPropertyName("rightFileEnd")]
    public CommentPositionDto? RightFileEnd { get; init; }

    [JsonPropertyName("leftFileStart")]
    public CommentPositionDto? LeftFileStart { get; init; }

    [JsonPropertyName("leftFileEnd")]
    public CommentPositionDto? LeftFileEnd { get; init; }
}

internal sealed record IterationContextDto
{
    [JsonPropertyName("firstComparingIteration")]
    public int FirstComparingIteration { get; init; }

    [JsonPropertyName("secondComparingIteration")]
    public int SecondComparingIteration { get; init; }
}

internal sealed record PullRequestThreadContextDto
{
    [JsonPropertyName("changeTrackingId")]
    public int ChangeTrackingId { get; init; }

    [JsonPropertyName("iterationContext")]
    public IterationContextDto? IterationContext { get; init; }
}

internal sealed record CommentDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("parentCommentId")]
    public int ParentCommentId { get; init; }

    [JsonPropertyName("author")]
    public IdentityRef? Author { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("publishedDate")]
    public DateTime PublishedDate { get; init; }

    [JsonPropertyName("commentType")]
    public string? CommentType { get; init; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; init; }
}

internal sealed record ThreadDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; init; }

    [JsonPropertyName("threadContext")]
    public ThreadContextDto? ThreadContext { get; init; }

    [JsonPropertyName("comments")]
    public IReadOnlyList<CommentDto> Comments { get; init; } = [];
}

internal sealed record CreateThreadDto
{
    [JsonPropertyName("comments")]
    public IReadOnlyList<CommentDto> Comments { get; init; } = [];

    [JsonPropertyName("status")]
    public string Status { get; init; } = "active";

    [JsonPropertyName("threadContext")]
    public ThreadContextDto? ThreadContext { get; init; }

    [JsonPropertyName("pullRequestThreadContext")]
    public PullRequestThreadContextDto? PullRequestThreadContext { get; init; }
}

internal sealed record UpdateThreadDto
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "active";
}
