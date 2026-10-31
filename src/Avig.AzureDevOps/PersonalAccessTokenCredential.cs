using System.Net.Http.Headers;
using System.Text;

namespace Avig.AzureDevOps;

/// <summary>
/// Sign-in with a personal access token that the application supplies.
/// </summary>
/// <remarks>
/// <para>
/// The package does not store the token anywhere. The application decides where it lives —
/// an encrypted file, the Windows credential manager, an environment variable, a secret
/// store — and hands over a function that returns it. The function is called on every
/// request and every time <see cref="IsConfigured"/> is read, so a token that is saved or
/// removed takes effect at once. It should therefore be cheap: keep the token in memory and
/// read the file once, not on every call.
/// </para>
/// <para>
/// The function is synchronous because <see cref="IsConfigured"/> is. A token that has to be
/// fetched over the network belongs in an <see cref="IAzureDevOpsCredential"/> of its own.
/// </para>
/// <para>
/// The token is sent as HTTP Basic with an empty user name, which is what Azure DevOps
/// prescribes. It should not be parsed or unpacked: since the summer of 2025 Azure DevOps
/// encrypts its tokens further, and the content is no longer readable.
/// </para>
/// </remarks>
public sealed class PersonalAccessTokenCredential : IAzureDevOpsCredential
{
    private readonly Func<string?> _token;

    /// <summary>A sign-in that asks <paramref name="token"/> for the token.</summary>
    /// <param name="token">
    /// Returns the token, or <see langword="null"/> or an empty string when there is none.
    /// Leading and trailing whitespace is removed.
    /// </param>
    public PersonalAccessTokenCredential(Func<string?> token)
    {
        ArgumentNullException.ThrowIfNull(token);

        _token = token;
    }

    public bool IsConfigured => Current().Length > 0;

    public Task<Result> AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return Task.FromResult(Result.Failure("There is no request to sign.", ResultStatus.Invalid));
        }

        string token = Current();

        if (token.Length == 0)
        {
            return Task.FromResult(Result.Failure(
                "Not signed in to Azure DevOps. Add a personal access token first.",
                ResultStatus.Unauthorized));
        }

        request.Headers.Authorization = BuildHeader(token);

        return Task.FromResult(Result.Success());
    }

    /// <summary>The token without surrounding whitespace, or an empty string when there is none.</summary>
    private string Current() => _token()?.Trim() ?? string.Empty;

    /// <summary>Basic with an empty user name — that is how Azure DevOps wants a token.</summary>
    private static AuthenticationHeaderValue BuildHeader(string token) =>
        new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}")));
}
