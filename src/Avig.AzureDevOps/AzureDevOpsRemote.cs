using System.Diagnostics.CodeAnalysis;

namespace Avig.AzureDevOps;

/// <summary>
/// Which repository in Azure DevOps a local clone points to.
/// </summary>
/// <param name="Organization">The organization, for example <c>contoso</c>.</param>
/// <param name="Project">The project's name, decoded.</param>
/// <param name="Repository">The repository's name.</param>
public sealed record AzureDevOpsRemote(string Organization, string Project, string Repository)
{
    /// <summary>The host for Azure DevOps Services, and the only one in the documentation.</summary>
    private const string Host = "dev.azure.com";

    /// <summary>The older form, <c>{organization}.visualstudio.com</c>.</summary>
    private const string LegacyHostSuffix = ".visualstudio.com";

    /// <summary>The form the web interface offers for cloning over SSH.</summary>
    private const string SshPrefix = "git@ssh.dev.azure.com:v3/";

    /// <summary>The segment that separates the project from the repo in an HTTPS address.</summary>
    private const string GitSegment = "_git";

    private const string GitSuffix = ".git";

    /// <summary>The base address for API calls.</summary>
    /// <remarks>
    /// Always <c>dev.azure.com</c>, even when the remote is in the older form
    /// <c>{organization}.visualstudio.com</c>. Both host names work, but only one of them is
    /// in the documentation, and that is the one that applies here.
    /// </remarks>
    public Uri ApiBaseUri => new($"https://{Host}/{Uri.EscapeDataString(Organization)}/");

    /// <summary>The address of the repo in the web interface.</summary>
    public Uri WebUri => new(
        $"https://{Host}/{Uri.EscapeDataString(Organization)}"
            + $"/{Uri.EscapeDataString(Project)}/{GitSegment}/{Uri.EscapeDataString(Repository)}");

    /// <summary>
    /// Parses a git remote and says whether it points to Azure DevOps.
    /// </summary>
    /// <remarks>
    /// Four forms occur in practice, and all four are in use at the same time:
    /// <list type="bullet">
    /// <item><c>https://dev.azure.com/{org}/{project}/_git/{repo}</c></item>
    /// <item><c>https://{org}@dev.azure.com/{org}/{project}/_git/{repo}</c> — the one the
    /// web interface offers for cloning</item>
    /// <item><c>git@ssh.dev.azure.com:v3/{org}/{project}/{repo}</c></item>
    /// <item><c>https://{org}.visualstudio.com/[DefaultCollection/]{project}/_git/{repo}</c>
    /// — older accounts, still in use</item>
    /// </list>
    /// Project names with spaces arrive URL-encoded and must be decoded, otherwise they do not
    /// match what the API wants.
    /// </remarks>
    /// <param name="remoteUrl">The git remote address, in any of the four forms.</param>
    /// <param name="remote">The organization and the project.</param>
    /// <returns>Whether the address points at a repository in Azure DevOps.</returns>
    [SuppressMessage(
        "Design",
        "CA1054:URI-like parameters should not be strings",
        Justification =
            "The remote comes as text from git and can be the scp form git@ssh.dev.azure.com:v3/... "
                + "which is not a valid Uri. Requiring a Uri would exclude half of the forms.")]
    public static bool TryParse(string? remoteUrl, [NotNullWhen(true)] out AzureDevOpsRemote? remote)
    {
        remote = null;

        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return false;
        }

        string url = remoteUrl.Trim();

        return TryParseSsh(url, out remote) || TryParseHttps(url, out remote);
    }

    /// <summary>The repository from an SSH address, <c>git@ssh.dev.azure.com:v3/org/project/repo</c>.</summary>
    /// <param name="url">The trimmed address.</param>
    /// <param name="remote">The repository, when the address is one.</param>
    private static bool TryParseSsh(string url, out AzureDevOpsRemote? remote)
    {
        remote = null;

        if (!url.StartsWith(SshPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        List<string> parts = Split(url[SshPrefix.Length..]);

        if (parts.Count != 3)
        {
            return false;
        }

        remote = new AzureDevOpsRemote(parts[0], parts[1], parts[2]);

        return true;
    }

    /// <summary>
    /// The repository from an HTTPS address, on <c>dev.azure.com</c> or the older
    /// <c>visualstudio.com</c>.
    /// </summary>
    /// <param name="url">The trimmed address.</param>
    /// <param name="remote">The repository, when the address is one.</param>
    private static bool TryParseHttps(string url, out AzureDevOpsRemote? remote)
    {
        remote = null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        List<string> segments = Split(uri.AbsolutePath);

        // "_git" separates the project from the repo, and everything before it is the path there.
        int separator = segments.FindIndex(text => text.Equals(GitSegment, StringComparison.OrdinalIgnoreCase));

        if (separator < 1 || separator != segments.Count - 2)
        {
            return false;
        }

        string repository = segments[^1];
        string project = segments[separator - 1];
        string host = uri.Host;

        if (host.EndsWith(LegacyHostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            remote = new AzureDevOpsRemote(host[..^LegacyHostSuffix.Length], project, repository);

            return true;
        }

        if (!host.Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // dev.azure.com/{org}/{project}/_git/{repo}: the organization is the first segment.
        if (separator < 2)
        {
            return false;
        }

        remote = new AzureDevOpsRemote(segments[0], project, repository);

        return true;
    }

    /// <summary>Splits a path and decodes the segments.</summary>
    /// <param name="path">The path, with the segments separated by slashes.</param>
    private static List<string> Split(string path) =>
    [
        .. path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .Select(WithoutGitSuffix),
    ];

    /// <summary>The repo without <c>.git</c>, which a clone address often has and the API does not want.</summary>
    /// <param name="segment">The repository's name from the address.</param>
    private static string WithoutGitSuffix(string segment)
    {
        if (segment.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return segment[..^GitSuffix.Length];
        }

        return segment;
    }
}
