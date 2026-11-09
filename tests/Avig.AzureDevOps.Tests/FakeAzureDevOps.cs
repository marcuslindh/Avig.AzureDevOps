using System.Net;
using System.Text;

namespace Avig.AzureDevOps.Tests;

/// <summary>
/// An Azure DevOps server in memory.
/// </summary>
/// <remarks>
/// What can go wrong in the integration is the requests, not business logic: the wrong
/// address, the wrong api-version, a missing <c>changeTrackingId</c> that makes the comment
/// end up on the overview tab instead of on the line. None of that shows up in a test
/// against the real service either — there you only see that something went wrong, much
/// later. This server records exactly what it received, and that is what makes the
/// assertions meaningful.
/// </remarks>
public sealed class FakeAzureDevOps : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, (byte[] Body, string ContentType)> _binaryResponses =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<RecordedRequest> _requests = [];

    /// <summary>The requests the server has received, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests;

    /// <summary>The response to the next request whose address contains <paramref name="pathFragment"/>.</summary>
    /// <param name="pathFragment">Part of the address, for example <c>pullrequests</c>.</param>
    /// <param name="json">The response body.</param>
    /// <param name="status">The response's status code.</param>
    public void Respond(string pathFragment, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        _responses[pathFragment] = (status, json);

    /// <summary>Answers requests whose address contains the fragment with bytes, such as a picture.</summary>
    /// <param name="pathFragment">Part of the address.</param>
    /// <param name="body">The response body.</param>
    /// <param name="contentType">The response's media type.</param>
    public void RespondBytes(string pathFragment, byte[] body, string contentType) =>
        _binaryResponses[pathFragment] = (body, contentType);

    /// <summary>An HttpClient that talks to this server.</summary>
    public HttpClient CreateClient() => new(this, disposeHandler: false);

    /// <summary>The last request whose address contains <paramref name="pathFragment"/>.</summary>
    /// <param name="pathFragment">Part of the address, for example <c>threads/501</c>.</param>
    /// <exception cref="InvalidOperationException">No such request was made; the message lists those that were.</exception>
    public RecordedRequest Last(string pathFragment) =>
        Requests.LastOrDefault(request => request.Uri.Contains(pathFragment, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No request to \"{pathFragment}\". Got: {string.Join(", ", Requests.Select(request => request.Uri))}");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // AbsoluteUri and not ToString: the former is what actually goes on the wire,
        // with the percent-encoding intact.
        string uri = request.RequestUri?.AbsoluteUri ?? string.Empty;

        string? body = null;

        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        _requests.Add(new RecordedRequest(
            request.Method.Method,
            uri,
            body,
            request.Headers.Authorization?.ToString()));

        KeyValuePair<string, (byte[] Body, string ContentType)> binary = _binaryResponses.FirstOrDefault(
            pair => uri.Contains(pair.Key, StringComparison.OrdinalIgnoreCase));

        if (binary.Key is not null)
        {
            ByteArrayContent content = new(binary.Value.Body);

            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(binary.Value.ContentType);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        KeyValuePair<string, (HttpStatusCode Status, string Body)> match = _responses.FirstOrDefault(
            pair => uri.Contains(pair.Key, StringComparison.OrdinalIgnoreCase));

        if (match.Key is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }

        return new HttpResponseMessage(match.Value.Status)
        {
            Content = new StringContent(match.Value.Body, Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>A request that the fake server received.</summary>
/// <param name="Method">The HTTP verb.</param>
/// <param name="Uri">The address, percent-encoded as it went on the wire.</param>
/// <param name="Body">The body, or <c>null</c> when there was none.</param>
/// <param name="Authorization">The Authorization header, or <c>null</c>.</param>
public sealed record RecordedRequest(string Method, string Uri, string? Body, string? Authorization);

/// <summary>A sign-in that always says yes, for tests that are not about it.</summary>
public sealed class FakeCredential : IAzureDevOpsCredential
{
    public bool IsConfigured => true;

    public Task<Result> AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "testtoken");

        return Task.FromResult(Result.Success());
    }
}
