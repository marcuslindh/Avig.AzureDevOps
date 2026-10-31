using System.Net;

namespace Avig.AzureDevOps;

/// <summary>Something went wrong in the communication with Azure DevOps.</summary>
/// <remarks>
/// Internal: it carries a failure from where it happens to <see cref="AzureDevOpsCall"/>,
/// which turns it into a <see cref="Result"/>. A caller of the package never sees it. The
/// message is written to be shown to the user as it is.
/// </remarks>
// Internal on purpose: the public API answers with Result, so no caller can or should catch it.
#pragma warning disable CA1064, S3871
internal sealed class AzureDevOpsException : Exception
#pragma warning restore CA1064, S3871
{
    public AzureDevOpsException(string message)
        : base(message)
    {
    }

    public AzureDevOpsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Whether the requested item does not exist.</summary>
    public bool IsNotFound { get; init; }

    /// <summary>
    /// The server's status code, or <c>null</c> when the error did not come from a response.
    /// </summary>
    public HttpStatusCode? StatusCode { get; init; }
}
