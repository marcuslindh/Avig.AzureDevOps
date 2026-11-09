using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Avig.AzureDevOps.Identities;

/// <summary>
/// People's profile pictures from Azure DevOps, fetched with the sign-in.
/// </summary>
/// <remarks>A client that sends its requests through <paramref name="httpClient"/>.</remarks>
/// <param name="httpClient">The typed client from the factory, with User-Agent and timeout set.</param>
/// <param name="credential">The sign-in every request is authorized with.</param>
/// <param name="logger">The log.</param>
public sealed class AzureDevOpsAvatarClient(
    HttpClient httpClient,
    IAzureDevOpsCredential credential,
    ILogger<AzureDevOpsAvatarClient> logger) : IAvatarClient
{
    /// <summary>The largest picture that is accepted, in bytes: 1 MiB.</summary>
    internal const int MaxImageBytes = 1024 * 1024;

    /// <summary>The media type when the server does not say.</summary>
    private const string DefaultContentType = "image/png";

    /// <summary>The media types asked for.</summary>
    private const string AcceptedMediaTypes = "image/*";

    /// <summary>The prefix every picture's media type has.</summary>
    private const string ImageMediaTypePrefix = "image/";

    /// <summary>The host of Azure DevOps Services.</summary>
    private const string AzureDevOpsHost = "dev.azure.com";

    /// <summary>The suffix of Azure DevOps' own subdomains, for example <c>vssps.dev.azure.com</c>.</summary>
    private const string AzureDevOpsSubdomainSuffix = ".dev.azure.com";

    /// <summary>The suffix of the older organization hosts, <c>{organization}.visualstudio.com</c>.</summary>
    private const string VisualStudioHostSuffix = ".visualstudio.com";

    /// <summary>What is fetched, for the messages.</summary>
    private const string What = "the profile picture";

    private readonly AzureDevOpsHttp _http = new(httpClient, credential, logger);
    private readonly ILogger<AzureDevOpsAvatarClient> _logger = logger;

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
    public async Task<Result<AzureDevOpsImage>> DownloadAsync(Uri imageUrl, CancellationToken cancellationToken = default)
    {
        if (imageUrl is null)
        {
            return Result<AzureDevOpsImage>.Invalid("No picture address was given. Pass the image address the package returned.");
        }

        // The token goes along with the request, so the address decides who gets it. Only
        // Azure DevOps itself: an address from an answer could in principle point anywhere.
        if (!IsAzureDevOps(imageUrl))
        {
            return Result<AzureDevOpsImage>.Invalid($"{imageUrl} is not an https address on Azure DevOps and is not fetched.");
        }

        using HttpRequestMessage request = new(HttpMethod.Get, imageUrl);

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AcceptedMediaTypes));

        Result<HttpResponseMessage> sent = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!sent.IsSuccess(out HttpResponseMessage? received))
        {
            return sent.WithoutValue().AsFailure<AzureDevOpsImage>();
        }

        using HttpResponseMessage response = received;

        Result success = await AzureDevOpsHttp.EnsureSuccessAsync(response, What, cancellationToken).ConfigureAwait(false);

        if (success.IsFailure(out _, out _))
        {
            Log.AvatarUnavailable(_logger, imageUrl.AbsoluteUri, (int)response.StatusCode);

            return success.AsFailure<AzureDevOpsImage>();
        }

        return await ReadImageAsync(response, imageUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Indicates whether an address is https on Azure DevOps, so that the sign-in may go along.</summary>
    /// <param name="url">The address.</param>
    /// <returns><c>true</c> for an absolute https address on <c>dev.azure.com</c>, its subdomains or <c>*.visualstudio.com</c>.</returns>
    internal static bool IsAzureDevOps(Uri url)
    {
        if (!url.IsAbsoluteUri || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = url.Host;

        return string.Equals(host, AzureDevOpsHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(AzureDevOpsSubdomainSuffix, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(VisualStudioHostSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the picture from a successful answer, at most <see cref="MaxImageBytes"/> bytes.</summary>
    /// <param name="response">The server's answer.</param>
    /// <param name="url">The address, for the messages.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The picture, or why the answer is not one that is accepted.</returns>
    private static async Task<Result<AzureDevOpsImage>> ReadImageAsync(
        HttpResponseMessage response,
        Uri url,
        CancellationToken cancellationToken)
    {
        string contentType = response.Content.Headers.ContentType?.MediaType ?? DefaultContentType;

        // A token that is not accepted can give 203 with the sign-in page as HTML: a success
        // status, but not a picture.
        if (!contentType.StartsWith(ImageMediaTypePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Result<AzureDevOpsImage>.Failure($"Azure DevOps answered {url} with {contentType}, not a picture.", ResultStatus.Error);
        }

        long? length = response.Content.Headers.ContentLength;

        if (length > MaxImageBytes)
        {
            return TooLarge(url);
        }

        // One byte more than allowed, so that a picture over the limit shows itself while it is
        // read: the length header is not always there.
        byte[] buffer = new byte[(int)(length ?? MaxImageBytes) + 1];
        int total = 0;
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);

            while (read > 0 && total + read < buffer.Length)
            {
                total += read;
                read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            }

            total += read;
        }

        if (total > MaxImageBytes || total == buffer.Length)
        {
            return TooLarge(url);
        }

        return new AzureDevOpsImage(buffer.AsMemory(0, total), contentType);
    }

    /// <summary>The failure for a picture larger than <see cref="MaxImageBytes"/>.</summary>
    /// <param name="url">The address, for the message.</param>
    /// <returns>The failure.</returns>
    private static Result<AzureDevOpsImage> TooLarge(Uri url) =>
        Result<AzureDevOpsImage>.Invalid($"The picture at {url} is larger than 1 MiB and is not fetched.");
}
