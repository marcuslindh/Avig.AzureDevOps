using System.Text.Json;
using Avig.AzureDevOps.PullRequests;

namespace Avig.AzureDevOps.Identities;

/// <summary>
/// Where a person's profile picture is, read from what the API says about the person.
/// </summary>
/// <remarks>
/// An identity in an API answer carries the picture twice: <c>_links.avatar.href</c>, the
/// address the current API documents, and <c>imageUrl</c>, the older one. The link is used
/// when it is there and <c>imageUrl</c> otherwise. Both point at Azure DevOps and need the
/// sign-in, which is why <see cref="IAvatarClient"/> exists.
/// </remarks>
internal static class IdentityImage
{
    /// <summary>The property that holds an identity's links.</summary>
    private const string LinksProperty = "_links";

    /// <summary>The link to the profile picture.</summary>
    private const string AvatarProperty = "avatar";

    /// <summary>The address in a link.</summary>
    private const string HrefProperty = "href";

    /// <summary>The older property with the picture's address.</summary>
    private const string ImageUrlProperty = "imageUrl";

    /// <summary>The address of the picture for an identity id, when the answer only has the id.</summary>
    private const string IdentityImageRoute = "_api/_common/identityImage?id=";

    /// <summary>The picture's address from a typed identity.</summary>
    /// <param name="imageUrl">The identity's <c>imageUrl</c>, or <c>null</c>.</param>
    /// <param name="links">The identity's <c>_links</c>, or <c>null</c>.</param>
    /// <returns>
    /// The avatar link when there is one, otherwise <paramref name="imageUrl"/>, otherwise
    /// <see cref="ResultStatus.NotFound"/>.
    /// </returns>
    public static Result<Uri> From(string? imageUrl, IdentityLinksDto? links)
    {
        if (ToUri(links?.Avatar?.Href).IsSuccess(out Uri? avatar))
        {
            return avatar;
        }

        return ToUri(imageUrl);
    }

    /// <summary>The picture's address from an identity that is still JSON, as in a work item's person fields.</summary>
    /// <param name="identity">The identity object; anything else gives <c>null</c>.</param>
    /// <returns>The avatar link when there is one, otherwise <c>imageUrl</c>, otherwise <see cref="ResultStatus.NotFound"/>.</returns>
    public static Result<Uri> From(JsonElement identity)
    {
        if (identity.ValueKind != JsonValueKind.Object)
        {
            return Result<Uri>.NotFound("The person field has no identity object.");
        }

        if (AvatarLink(identity).IsSuccess(out string? avatar) && ToUri(avatar).IsSuccess(out Uri? link))
        {
            return link;
        }

        if (identity.TryGetProperty(ImageUrlProperty, out JsonElement image) && image.ValueKind == JsonValueKind.String)
        {
            return ToUri(image.GetString());
        }

        return Result<Uri>.NotFound("The identity has no picture address.");
    }

    /// <summary>The picture's address for an identity the answer gives only the id of.</summary>
    /// <param name="remote">The organization the identity belongs to.</param>
    /// <param name="id">The identity's id, a GUID as text.</param>
    /// <returns>The address, or <see cref="ResultStatus.NotFound"/> when there is no id.</returns>
    public static Result<Uri> ForId(AzureDevOpsRemote remote, string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return Result<Uri>.NotFound("The identity has no id to find a picture by.");
        }

        return new Uri(remote.ApiBaseUri, IdentityImageRoute + Uri.EscapeDataString(id));
    }

    /// <summary>An absolute address from the text the API sent.</summary>
    /// <param name="text">The text, or <c>null</c>.</param>
    /// <returns>The address, or <see cref="ResultStatus.NotFound"/> when the text is empty or not an absolute address.</returns>
    private static Result<Uri> ToUri(string? text)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? url))
        {
            return url;
        }

        return Result<Uri>.NotFound("The identity has no picture address.");
    }

    /// <summary>The address in <c>_links.avatar.href</c>.</summary>
    /// <param name="identity">The identity object.</param>
    /// <returns>The address, or <see cref="ResultStatus.NotFound"/> when the identity has no such link.</returns>
    private static Result<string> AvatarLink(JsonElement identity)
    {
        if (identity.TryGetProperty(LinksProperty, out JsonElement links)
            && links.ValueKind == JsonValueKind.Object
            && links.TryGetProperty(AvatarProperty, out JsonElement avatar)
            && avatar.ValueKind == JsonValueKind.Object
            && avatar.TryGetProperty(HrefProperty, out JsonElement href)
            && href.ValueKind == JsonValueKind.String
            && href.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        return Result<string>.NotFound("The identity has no avatar link.");
    }
}
