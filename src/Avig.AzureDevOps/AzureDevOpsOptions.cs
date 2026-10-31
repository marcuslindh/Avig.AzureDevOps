namespace Avig.AzureDevOps;

/// <summary>
/// The settings for the clients that <see cref="ServiceCollectionExtensions"/> registers.
/// </summary>
/// <remarks>
/// Where the token comes from is not a setting: the application passes a function that
/// returns it, or registers its own <see cref="IAzureDevOpsCredential"/>.
/// </remarks>
public sealed class AzureDevOpsOptions
{
    /// <summary>
    /// The value in <c>User-Agent</c>. A descriptive name makes the traffic recognizable
    /// on the server side.
    /// </summary>
    /// <remarks>The default is the package's name and version, for example <c>Avig.AzureDevOps/1.0.3</c>.</remarks>
    public string UserAgent { get; set; } = DefaultUserAgent();

    /// <summary>How long a call may take before <see cref="HttpClient"/> gives up.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The package's name and version, so that the default never falls behind a release.</summary>
    private static string DefaultUserAgent()
    {
        string version = "0.0.0";

        if (typeof(AzureDevOpsOptions).Assembly.GetName().Version is { } assembly)
        {
            version = assembly.ToString(3);
        }

        return $"Avig.AzureDevOps/{version}";
    }
}
