namespace Avig.AzureDevOps.PullRequests;

/// <summary>Where a pull request is in its life.</summary>
public enum PullRequestStatus
{
    /// <summary>Open: it can be reviewed, voted on and completed.</summary>
    Active = 0,

    /// <summary>Completed: the changes are in the target branch.</summary>
    Completed = 1,

    /// <summary>Abandoned without being completed. It can be made active again.</summary>
    Abandoned = 2,
}

/// <summary>Which pull requests <see cref="IPullRequestClient.ListAsync"/> lists.</summary>
public enum PullRequestStatusFilter
{
    /// <summary>The open ones.</summary>
    Active = 0,

    /// <summary>The completed ones.</summary>
    Completed = 1,

    /// <summary>The abandoned ones.</summary>
    Abandoned = 2,

    /// <summary>All of them, whatever their status.</summary>
    All = 3,
}

/// <summary>How the server's attempt to merge a pull request went.</summary>
/// <remarks>
/// The server tries the merge on its own every time the source or target branch moves, so
/// this says whether the pull request can be completed as it stands.
/// </remarks>
public enum PullRequestMergeStatus
{
    /// <summary>No merge has been attempted yet.</summary>
    NotSet = 0,

    /// <summary>The merge is waiting to be attempted.</summary>
    Queued = 1,

    /// <summary>The branches conflict and must be resolved first.</summary>
    Conflicts = 2,

    /// <summary>The merge went through.</summary>
    Succeeded = 3,

    /// <summary>A policy on the target branch stopped the merge.</summary>
    RejectedByPolicy = 4,

    /// <summary>The merge failed for another reason.</summary>
    Failure = 5,
}

/// <summary>What happened to a file in a pull request.</summary>
/// <remarks>
/// A flags value, because the server reports several things at once: a file that is both
/// moved and changed is <c>Edit | Rename</c>.
/// </remarks>
[Flags]
public enum PullRequestChangeType
{
    /// <summary>Nothing the package recognizes.</summary>
    None = 0,

    /// <summary>The file is new.</summary>
    Add = 1,

    /// <summary>The content has changed.</summary>
    Edit = 2,

    /// <summary>The encoding has changed.</summary>
    Encoding = 4,

    /// <summary>The file has been moved or renamed.</summary>
    Rename = 8,

    /// <summary>The file has been deleted.</summary>
    Delete = 16,

    /// <summary>A deleted file has been brought back.</summary>
    Undelete = 32,

    /// <summary>The file came with a branch.</summary>
    Branch = 64,

    /// <summary>The file came with a merge.</summary>
    Merge = 128,

    /// <summary>The file is locked.</summary>
    Lock = 256,

    /// <summary>A change has been rolled back.</summary>
    Rollback = 512,

    /// <summary>The file is the source of a rename.</summary>
    SourceRename = 1024,

    /// <summary>The file is the target of a rename.</summary>
    TargetRename = 2048,

    /// <summary>A property of the file has changed.</summary>
    Property = 4096,
}
