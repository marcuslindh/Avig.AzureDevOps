using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Avig.AzureDevOps.PullRequests;
using Microsoft.Extensions.Logging;

namespace Avig.AzureDevOps;

/// <summary>
/// The exchange with Azure DevOps: addresses, sign-in, throttling and error messages.
/// </summary>
/// <remarks>
/// Four clients talk to the same API — pull requests, work items, builds and the wiki — and
/// what is hard is the same for all of them. Throttling in particular: the server delays
/// before it blocks, and a client that does not respect the delay penalizes the others,
/// since the limit is counted per sign-in. That logic should therefore live in one place,
/// and the errors become <see cref="AzureDevOpsException"/> for all four, which
/// <see cref="AzureDevOpsCall"/> turns into a <see cref="Result"/> at the public boundary.
/// </remarks>
internal sealed class AzureDevOpsHttp(
    HttpClient httpClient,
    IAzureDevOpsCredential credential,
    ILogger logger)
{
    /// <summary>The wait on a 429 when the server has not said how long.</summary>
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);

    /// <summary>How much of an error response is included in the message.</summary>
    private const int MaxErrorLength = 300;

    /// <summary>The address of an API under the project, with api-version appended.</summary>
    /// <param name="remote">The organization and the project.</param>
    /// <param name="relative">
    /// The path after <c>_apis/</c>, for example <c>wit/wiql</c>, with any query string.
    /// </param>
    /// <param name="apiVersion">
    /// A version other than <see cref="AzureDevOpsJson.ApiVersion"/>, for the APIs that only
    /// exist as a preview version — the work items' comments, for example.
    /// </param>
    public static Uri ProjectRoute(AzureDevOpsRemote remote, string relative, string? apiVersion = null)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(relative);

        // A path that already has a query string gets the version as one more parameter.
        char separator = '?';

        if (relative.Contains('?', StringComparison.Ordinal))
        {
            separator = '&';
        }

        return new Uri(
            remote.ApiBaseUri,
            $"{Uri.EscapeDataString(remote.Project)}/_apis/{relative}"
            + $"{separator}api-version={apiVersion ?? AzureDevOpsJson.ApiVersion}");
    }

    /// <summary>Fetches and parses the response.</summary>
    public async Task<T> GetAsync<T>(Uri route, string what, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, route);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<T>(response, what, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends JSON and parses the response.</summary>
    public async Task<TResult> PostAsync<TBody, TResult>(
        Uri route,
        TBody body,
        string what,
        CancellationToken cancellationToken)
    {
        using JsonContent content = JsonContent.Create(body, options: AzureDevOpsJson.Options);
        using HttpRequestMessage request = new(HttpMethod.Post, route) { Content = content };

        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<TResult>(response, what, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends preformatted text with a custom media type and parses the response.
    /// </summary>
    /// <remarks>
    /// Exists to create work items. They are accepted as <c>application/json-patch+json</c>
    /// and not as plain JSON, and the media type must match to the character — otherwise the
    /// server responds 415 without saying what was wrong.
    /// </remarks>
    public async Task<TResult> PostAsync<TResult>(
        Uri route,
        string json,
        string mediaType,
        string what,
        CancellationToken cancellationToken,
        HttpMethod? method = null)
    {
        using StringContent content = new(json, System.Text.Encoding.UTF8, mediaType);

        // PATCH to change a work item, POST to create it. The body and the
        // media type are the same, so only the verb differs.
        using HttpRequestMessage request = new(method ?? HttpMethod.Post, route) { Content = content };

        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<TResult>(response, what, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request and respects the throttling.
    /// </summary>
    /// <remarks>
    /// Azure DevOps delays before it blocks, and a delayed request still succeeds
    /// — the response is 200 with a <c>Retry-After</c>. It must be taken seriously even then,
    /// otherwise one goes from delayed to blocked. Only on a 429 is a retry needed.
    /// </remarks>
    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is not HttpStatusCode.TooManyRequests)
        {
            await HonourDelayAsync(response, cancellationToken).ConfigureAwait(false);

            return response;
        }

        TimeSpan wait = RetryAfter(response).DataOr(DefaultRetryAfter);

        Log.PullRequestThrottled(logger, wait.TotalSeconds);
        response.Dispose();

        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

        using HttpRequestMessage retry = await CloneAsync(request, cancellationToken).ConfigureAwait(false);

        await AuthorizeAsync(retry, cancellationToken).ConfigureAwait(false);

        return await httpClient.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signs the request, or stops the call with the credential's own message.</summary>
    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Result authorized = await credential.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        if (authorized.IsFailure(out string? error, out ResultStatus status))
        {
            HttpStatusCode code = HttpStatusCode.Unauthorized;

            if (status == ResultStatus.Forbidden)
            {
                code = HttpStatusCode.Forbidden;
            }

            throw new AzureDevOpsException(error) { StatusCode = code };
        }
    }

    /// <summary>Parses the response, or stops the call with a message that can be shown.</summary>
    public static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        string what,
        CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, what, cancellationToken).ConfigureAwait(false);

        return await response.Content
                   .ReadFromJsonAsync<T>(AzureDevOpsJson.Options, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new AzureDevOpsException("Azure DevOps sent an empty response.");
    }

    /// <summary>
    /// Stops the call with a message written for whoever sees it.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="what">
    /// What was looked for, with its article and in lower case: <c>the repository or pull request</c>,
    /// <c>the work item</c>.
    /// It appears in the message on 403 and 404, where it is the only thing that tells the
    /// errors apart.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string what,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        throw new AzureDevOpsException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "Azure DevOps rejected the sign-in. Check that the token is valid and has not expired.",
            HttpStatusCode.Forbidden =>
                $"The sign-in does not have access to {what}. Check that the token has "
                + "the required permissions: read, and write to change anything.",
            HttpStatusCode.NotFound =>
                $"Could not find {what} in Azure DevOps. Are the organization, project and repository correct?",
            _ => $"Azure DevOps responded {(int)response.StatusCode}: {Shorten(body)}",
        })
        {
            IsNotFound = response.StatusCode == HttpStatusCode.NotFound,
            StatusCode = response.StatusCode,
        };
    }

    /// <summary>Waits out the delay the server asks for, even on a successful response.</summary>
    private static async Task HonourDelayAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (RetryAfter(response).IsSuccess(out TimeSpan wait) && wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The delay the server asks for, if it asks for one.</summary>
    private static Result<TimeSpan> RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values)
            && double.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out double seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return Result<TimeSpan>.NotFound();
    }

    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpRequestMessage clone = new(request.Method, request.RequestUri);

        if (request.Content is { } content)
        {
            byte[] bytes = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            ByteArrayContent copy = new(bytes);

            foreach (KeyValuePair<string, IEnumerable<string>> header in content.Headers)
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = copy;
        }

        return clone;
    }

    /// <summary>
    /// The error's text: the server's <c>message</c> when the response is a JSON error, otherwise
    /// the beginning of the response. The message is what explains why — a policy that is not
    /// met, a state that does not exist — and the rest of the object is just noise.
    /// </summary>
    private static string Shorten(string body)
    {
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(body);

            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out System.Text.Json.JsonElement message)
                && message.GetString() is { Length: > 0 } text)
            {
                body = text;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON. Then the text has to speak for itself.
        }

        if (body.Length <= MaxErrorLength)
        {
            return body;
        }

        return body[..MaxErrorLength] + "…";
    }
}
