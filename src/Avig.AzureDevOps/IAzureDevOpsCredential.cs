namespace Avig.AzureDevOps;

/// <summary>
/// What makes a request to Azure DevOps authorized.
/// </summary>
/// <remarks>
/// A separate surface because the sign-in method is about to change. Microsoft recommends
/// Entra ID via MSAL for new integrations and advises against personal access tokens, but
/// Entra requires an app registration in the directory, which a user is rarely allowed to
/// make on their own. A token works today, without anyone having to grant anything. When Entra
/// is added, it is a second implementation of this, registered before
/// <c>AddAvigAzureDevOps</c> in <see cref="ServiceCollectionExtensions"/>, and nothing else
/// needs to change.
/// </remarks>
public interface IAzureDevOpsCredential
{
    /// <summary>Whether there is a sign-in to use.</summary>
    bool IsConfigured { get; }

    /// <summary>Sets the authorization on a request.</summary>
    /// <returns>
    /// Success, or a failure with <see cref="ResultStatus.Unauthorized"/> and a message for the
    /// user when there is no sign-in to use. The request is then not sent.
    /// </returns>
    Task<Result> AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
}
