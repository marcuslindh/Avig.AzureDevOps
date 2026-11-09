namespace Avig.AzureDevOps.Identities;

/// <summary>A picture fetched from Azure DevOps.</summary>
/// <param name="Content">The picture's bytes. <c>Content.ToArray()</c> gives an array.</param>
/// <param name="ContentType">The media type, for example <c>image/png</c>.</param>
public sealed record AzureDevOpsImage(ReadOnlyMemory<byte> Content, string ContentType);

/// <summary>
/// People's profile pictures. The addresses come with the people: <see cref="AzureDevOpsIdentity.ImageUrl"/>,
/// <see cref="WorkItems.WorkItemSummary.AssignedToImageUrl"/>, <see cref="WorkItems.WorkItemComment.AuthorImageUrl"/>,
/// <see cref="PullRequests.PullRequestSummary.AuthorImageUrl"/> and <see cref="PullRequests.PullRequestComment.AuthorImageUrl"/>.
/// </summary>
/// <remarks>
/// The pictures sit behind the same sign-in as everything else, so an application cannot put
/// the address in an <c>img</c> tag; it fetches the picture here and shows the bytes.
/// </remarks>
public interface IAvatarClient
{
    /// <summary>Fetches a profile picture with the sign-in.</summary>
    /// <param name="imageUrl">
    /// The picture's address, as the package gave it. Only https addresses on <c>dev.azure.com</c>,
    /// its subdomains and <c>*.visualstudio.com</c> are fetched; the sign-in never goes anywhere else.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The picture, or why there is none: <see cref="ResultStatus.Invalid"/> for an address that is
    /// missing, not https or on another host (nothing is sent then), or for a picture larger than 1 MiB;
    /// the HTTP status's failure when the server refuses; <see cref="ResultStatus.Error"/> when the
    /// answer is not a picture.
    /// </returns>
    Task<Result<AzureDevOpsImage>> DownloadAsync(Uri imageUrl, CancellationToken cancellationToken = default);
}
