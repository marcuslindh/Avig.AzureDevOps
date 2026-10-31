namespace Avig.AzureDevOps.Wiki;

/// <summary>A wiki in the project.</summary>
/// <param name="Id">The wiki's id in Azure DevOps.</param>
/// <param name="Name">The name, for example <c>UmeMap.wiki</c>.</param>
/// <param name="IsProjectWiki">
/// Whether this is the project's own wiki. The other kind is a folder in a repo that has
/// been published as a wiki.
/// </param>
public sealed record WikiSummary(string Id, string Name, bool IsProjectWiki);

/// <summary>A page in the wiki's tree, with its subpages.</summary>
/// <param name="Path">The path, for example <c>/Utveckling/Kom igång</c>.</param>
/// <param name="Id">The page's number, or <c>null</c> when the server did not send one.</param>
/// <param name="Children">The subpages, in the wiki's order.</param>
public sealed record WikiPageNode(string Path, int? Id, IReadOnlyList<WikiPageNode> Children)
{
    /// <summary>The page's name, the last segment of the path.</summary>
    public string Name
    {
        get
        {
            if (Path.TrimEnd('/') is { Length: > 0 } trimmed)
            {
                return trimmed[(trimmed.LastIndexOf('/') + 1)..];
            }

            return "/";
        }
    }
}

/// <summary>A page with its text.</summary>
/// <param name="WikiName">The wiki it is in.</param>
/// <param name="Path">The path.</param>
/// <param name="Id">The page's number, or <c>null</c>.</param>
/// <param name="Markdown">The text, as markdown — just as it was written.</param>
/// <param name="WebUri">The address of the page in the web interface, or <c>null</c>.</param>
/// <param name="SubPages">The paths of the subpages, the nearest level.</param>
public sealed record WikiPage(
    string WikiName,
    string Path,
    int? Id,
    string Markdown,
    Uri? WebUri,
    IReadOnlyList<string> SubPages);

/// <summary>A hit in a search.</summary>
/// <param name="WikiName">The wiki the page is in.</param>
/// <param name="Path">The page's path.</param>
/// <param name="Highlights">The excerpts where the text was found, with the hit inside «».</param>
public sealed record WikiSearchHit(string WikiName, string Path, IReadOnlyList<string> Highlights);

/// <summary>
/// Reads the wiki in Azure DevOps.
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
/// Read-only. A wiki page is documentation that several people stand behind, and writing to
/// it is a different decision from reading it — that will have to wait until someone asks
/// for it.
/// </remarks>
public interface IWikiClient
{
    /// <summary>Whether the tool has a sign-in to work with.</summary>
    bool IsConnected { get; }

    /// <summary>Wikis in the project.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<IReadOnlyList<WikiSummary>>> GetWikisAsync(
        AzureDevOpsRemote remote,
        CancellationToken cancellationToken = default);

    /// <summary>The pages under a path, with all levels below it.</summary>
    /// <param name="remote">The project.</param>
    /// <param name="wikiId">The wiki's id or name, from <see cref="WikiSummary.Id"/>.</param>
    /// <param name="path">The path the tree starts at; <c>/</c> for the whole wiki.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page at the path with everything under it, or <see cref="ResultStatus.NotFound"/>.</returns>
    Task<Result<WikiPageNode>> GetPageTreeAsync(
        AzureDevOpsRemote remote,
        string wikiId,
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>A page with text, by path.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="wikiId">The wiki's id or name, from <see cref="WikiSummary.Id"/>.</param>
    /// <param name="path">The page's path, for example <c>/Development/Getting started</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page, or <see cref="ResultStatus.NotFound"/> when it does not exist.</returns>
    Task<Result<WikiPage>> GetPageAsync(
        AzureDevOpsRemote remote,
        string wikiId,
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>A page with text, by number.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="wikiId">The wiki's id or name, from <see cref="WikiSummary.Id"/>.</param>
    /// <param name="id">The page's number, from its address or <see cref="WikiPage.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page, or <see cref="ResultStatus.NotFound"/> when it does not exist.</returns>
    Task<Result<WikiPage>> GetPageByIdAsync(
        AzureDevOpsRemote remote,
        string wikiId,
        int id,
        CancellationToken cancellationToken = default);

    /// <summary>Searches the project's wikis.</summary>
    /// <param name="remote">The project.</param>
    /// <param name="text">What is searched for.</param>
    /// <param name="limit">At most this many hits.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<Result<IReadOnlyList<WikiSearchHit>>> SearchAsync(
        AzureDevOpsRemote remote,
        string text,
        int limit = 25,
        CancellationToken cancellationToken = default);
}
