using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Avig.AzureDevOps.PullRequests;
using Microsoft.Extensions.Logging;

namespace Avig.AzureDevOps;

/// <summary>
/// The exchange with Azure DevOps: addresses, sign-in, throttling and error messages.
/// </summary>
/// <param name="httpClient">The client the requests are sent with.</param>
/// <param name="credential">The sign-in every request is authorized with.</param>
/// <param name="logger">The log, for throttling.</param>
/// <remarks>
/// <para>
/// Four clients talk to the same API — pull requests, work items, builds and the wiki — and
/// what is hard is the same for all of them. Throttling in particular: the server delays
/// before it blocks, and a client that does not respect the delay penalizes the others,
/// since the limit is counted per sign-in. That logic therefore lives in one place.
/// </para>
/// <para>
/// Every method answers with a <see cref="Result"/>. A failure from the server gets a message
/// written for the user and a status from the HTTP status code; a network error or a timeout
/// from <see cref="HttpClient"/> is caught here and becomes <see cref="ResultStatus.Unavailable"/>.
/// Only the caller's own cancellation passes through as <see cref="OperationCanceledException"/>.
/// </para>
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
    /// <returns>The absolute address.</returns>
    public static Uri ProjectRoute(AzureDevOpsRemote remote, string relative, string? apiVersion = null)
    {
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
    /// <typeparam name="T">The type the response is read as.</typeparam>
    /// <param name="route">The full address of the API.</param>
    /// <param name="what">What is asked for, with its article and in lower case, for the messages: <c>the work item</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The parsed response, or why there is none.</returns>
    public async Task<Result<T>> GetAsync<T>(Uri route, string what, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, route);

        return await SendAndReadAsync<T>(request, what, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends JSON and parses the response.</summary>
    /// <typeparam name="TBody">The type of the object sent.</typeparam>
    /// <typeparam name="TResult">The type the response is read as.</typeparam>
    /// <param name="route">The full address of the API.</param>
    /// <param name="body">The object sent as JSON.</param>
    /// <param name="what">What is asked for, with its article and in lower case, for the messages: <c>the work item</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The parsed response, or why there is none.</returns>
    public async Task<Result<TResult>> PostAsync<TBody, TResult>(
        Uri route,
        TBody body,
        string what,
        CancellationToken cancellationToken)
    {
        using JsonContent content = JsonContent.Create(body, options: AzureDevOpsJson.Options);
        using HttpRequestMessage request = new(HttpMethod.Post, route) { Content = content };

        return await SendAndReadAsync<TResult>(request, what, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends preformatted text with a custom media type and parses the response.
    /// </summary>
    /// <typeparam name="TResult">The type the response is read as.</typeparam>
    /// <param name="route">The full address of the API.</param>
    /// <param name="json">The body, already serialized.</param>
    /// <param name="mediaType">The body's media type, for example <c>application/json-patch+json</c>.</param>
    /// <param name="what">What is asked for, with its article and in lower case, for the messages: <c>the work item</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="method">The verb, or <c>null</c> for POST.</param>
    /// <returns>The parsed response, or why there is none.</returns>
    /// <remarks>
    /// Exists to create work items. They are accepted as <c>application/json-patch+json</c>
    /// and not as plain JSON, and the media type must match to the character — otherwise the
    /// server responds 415 without saying what was wrong.
    /// </remarks>
    public async Task<Result<TResult>> PostAsync<TResult>(
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

        return await SendAndReadAsync<TResult>(request, what, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request and respects the throttling.
    /// </summary>
    /// <param name="request">The request. It is authorized here, so it must not be already.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The response, whatever its status code — the caller owns it and disposes it — or a
    /// failure when there is none: no sign-in, no network, or no answer in time.
    /// </returns>
    /// <remarks>
    /// Azure DevOps delays before it blocks, and a delayed request still succeeds
    /// — the response is 200 with a <c>Retry-After</c>. It must be taken seriously even then,
    /// otherwise one goes from delayed to blocked. Only on a 429 is a retry needed.
    /// </remarks>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The response belongs to the caller, who disposes it. It is disposed here only when a 429 "
            + "makes it be replaced by the retry's response.")]
    public async Task<Result<HttpResponseMessage>> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Result<HttpResponseMessage> first = await AuthorizeAndSendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!first.IsSuccess(out HttpResponseMessage? response))
        {
            return first;
        }

        if (response.StatusCode is not HttpStatusCode.TooManyRequests)
        {
            await HonourDelayAsync(response, cancellationToken).ConfigureAwait(false);

            return first;
        }

        TimeSpan wait = RetryAfter(response).DataOr(DefaultRetryAfter);

        Log.PullRequestThrottled(logger, wait.TotalSeconds);
        response.Dispose();

        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

        using HttpRequestMessage retry = await CloneAsync(request, cancellationToken).ConfigureAwait(false);

        return await AuthorizeAndSendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses a response, after checking that it is not an error.</summary>
    /// <typeparam name="T">The type the response is read as.</typeparam>
    /// <param name="response">The server's response.</param>
    /// <param name="what">What is asked for, with its article and in lower case, for the messages: <c>the work item</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The parsed response, or why there is none: an error status, an empty body or one that is not JSON.</returns>
    public static async Task<Result<T>> ReadAsync<T>(
        HttpResponseMessage response,
        string what,
        CancellationToken cancellationToken)
    {
        Result success = await EnsureSuccessAsync(response, what, cancellationToken).ConfigureAwait(false);

        if (success.IsFailure(out string? error, out ResultStatus status))
        {
            return Result<T>.Failure(error, status);
        }

        try
        {
            T? value = await response.Content
                .ReadFromJsonAsync<T>(AzureDevOpsJson.Options, cancellationToken)
                .ConfigureAwait(false);

            if (value is null)
            {
                return Result<T>.Failure("Azure DevOps sent an empty response.", ResultStatus.Error);
            }

            return value;
        }
        catch (JsonException)
        {
            return Result<T>.Failure("Azure DevOps sent a response that could not be read.", ResultStatus.Error);
        }
    }

    /// <summary>
    /// Whether the server answered with success, and if not, a message written for whoever sees it.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="what">
    /// What was looked for, with its article and in lower case: <c>the repository or pull request</c>,
    /// <c>the work item</c>.
    /// It appears in the message on 403 and 404, where it is the only thing that tells the
    /// errors apart.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// Success, or a failure with the message and a status from the HTTP status code:
    /// <see cref="ResultStatus.Unauthorized"/> for 401, <see cref="ResultStatus.Forbidden"/> for 403,
    /// <see cref="ResultStatus.NotFound"/> for 404, <see cref="ResultStatus.Conflict"/> for 409,
    /// <see cref="ResultStatus.Invalid"/> for 400, <see cref="ResultStatus.Unavailable"/> for 429
    /// and 5xx, and <see cref="ResultStatus.Error"/> for the rest.
    /// </returns>
    public static async Task<Result> EnsureSuccessAsync(
        HttpResponseMessage response,
        string what,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return Result.Success();
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        string message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "Azure DevOps rejected the sign-in. Check that the token is valid and has not expired.",
            HttpStatusCode.Forbidden =>
                $"The sign-in does not have access to {what}. Check that the token has "
                    + "the required permissions: read, and write to change anything.",
            HttpStatusCode.NotFound =>
                $"Could not find {what} in Azure DevOps. Are the organization, project and repository correct?",
            _ => $"Azure DevOps responded {(int)response.StatusCode}: {Shorten(body)}",
        };

        return Result.Failure(message, StatusFor(response.StatusCode));
    }

    /// <summary>The status for an HTTP status code that is not success.</summary>
    /// <param name="code">The server's status code.</param>
    /// <returns>What kind of failure the code means.</returns>
    private static ResultStatus StatusFor(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized => ResultStatus.Unauthorized,
        HttpStatusCode.Forbidden => ResultStatus.Forbidden,
        HttpStatusCode.NotFound => ResultStatus.NotFound,
        HttpStatusCode.Conflict => ResultStatus.Conflict,
        HttpStatusCode.BadRequest => ResultStatus.Invalid,
        HttpStatusCode.TooManyRequests => ResultStatus.Unavailable,
        >= HttpStatusCode.InternalServerError => ResultStatus.Unavailable,
        _ => ResultStatus.Error,
    };

    /// <summary>Sends a request and parses the response, disposing it afterwards.</summary>
    /// <typeparam name="T">The type the response is read as.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="what">What is asked for, for the messages.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The parsed response, or why there is none.</returns>
    private async Task<Result<T>> SendAndReadAsync<T>(
        HttpRequestMessage request,
        string what,
        CancellationToken cancellationToken)
    {
        Result<HttpResponseMessage> sent = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!sent.IsSuccess(out HttpResponseMessage? response))
        {
            return sent.WithoutValue().AsFailure<T>();
        }

        using (response)
        {
            return await ReadAsync<T>(response, what, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Signs the request and sends it.</summary>
    /// <param name="request">The request to authorize and send.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The response, or the credential's own failure when there is no sign-in to use, and
    /// <see cref="ResultStatus.Unavailable"/> when the network fails or the server does not
    /// answer in time.
    /// </returns>
    private async Task<Result<HttpResponseMessage>> AuthorizeAndSendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Result authorized = await credential.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        if (authorized.IsFailure(out string? error, out ResultStatus status))
        {
            return Result<HttpResponseMessage>.Failure(error, status);
        }

        try
        {
            return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return Result<HttpResponseMessage>.Unavailable($"Could not reach Azure DevOps: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's own timeout, not the caller cancelling.
            return Result<HttpResponseMessage>.Unavailable("Azure DevOps did not respond in time.");
        }
    }

    /// <summary>Waits out the delay the server asks for, even on a successful response.</summary>
    /// <param name="response">The server's response.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the delay is over.</returns>
    private static async Task HonourDelayAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (RetryAfter(response).IsSuccess(out TimeSpan wait) && wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The delay the server asks for, if it asks for one.</summary>
    /// <param name="response">The server's response.</param>
    /// <returns>The delay, or <see cref="ResultStatus.NotFound"/> when the server asks for none.</returns>
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

    /// <summary>A copy of a request with its body and content headers, to send again after a 429.</summary>
    /// <param name="request">The request to copy.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The copy.</returns>
    /// <remarks>
    /// A request can be sent only once, so the retry needs a request of its own. The sign-in is
    /// not copied; it is set again on the copy before it is sent.
    /// </remarks>
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
    /// <param name="body">The body of the server's error response.</param>
    /// <returns>The text for the message, at most <see cref="MaxErrorLength"/> characters.</returns>
    private static string Shorten(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out JsonElement message)
                && message.GetString() is { Length: > 0 } text)
            {
                body = text;
            }
        }
        catch (JsonException)
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
