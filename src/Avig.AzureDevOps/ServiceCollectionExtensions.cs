using Avig.AzureDevOps.Builds;
using Avig.AzureDevOps.PullRequests;
using Avig.AzureDevOps.Wiki;
using Avig.AzureDevOps.WorkItems;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Avig.AzureDevOps;

/// <summary>Registers the clients for Azure DevOps.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the clients and signs in with the personal access token that
    /// <paramref name="token"/> returns.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="token">
    /// Returns the token, or <see langword="null"/> when there is none. Called on every
    /// request; see <see cref="PersonalAccessTokenCredential"/>.
    /// </param>
    /// <param name="configure">The settings, or <see langword="null"/> for the defaults.</param>
    /// <returns>The same services, for chaining.</returns>
    public static IServiceCollection AddAvigAzureDevOps(
        this IServiceCollection services,
        Func<string?> token,
        Action<AzureDevOpsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(token);

        return services.AddAvigAzureDevOps(_ => token(), configure);
    }

    /// <summary>
    /// Registers the clients and signs in with the personal access token that
    /// <paramref name="token"/> returns, with access to the application's services.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="token">
    /// Returns the token, or <see langword="null"/> when there is none — typically by asking
    /// the application's own token store. Called on every request with the root provider,
    /// so only singletons should be resolved from it.
    /// </param>
    /// <param name="configure">The settings, or <see langword="null"/> for the defaults.</param>
    /// <returns>The same services, for chaining.</returns>
    public static IServiceCollection AddAvigAzureDevOps(
        this IServiceCollection services,
        Func<IServiceProvider, string?> token,
        Action<AzureDevOpsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(token);

        services.TryAddSingleton<IAzureDevOpsCredential>(
            provider => new PersonalAccessTokenCredential(() => token(provider)));

        return services.AddAvigAzureDevOps(configure);
    }

    /// <summary>
    /// Registers the clients for pull requests, work items, builds and the wiki, without a
    /// sign-in.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configure">The settings, or <see langword="null"/> for the defaults.</param>
    /// <returns>The same services, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The clients need an <see cref="IAzureDevOpsCredential"/>, which the application then
    /// registers itself — the seam for a sign-in with Entra ID, or for a token that has to be
    /// fetched asynchronously. The overloads that take a token register a
    /// <see cref="PersonalAccessTokenCredential"/>, but never over one that is already
    /// registered.
    /// </para>
    /// <para>
    /// The clients each have their own <see cref="HttpClient"/> registration rather than a
    /// shared one: a slow fetch of builds should not stand in the way of publishing comments.
    /// They are transient, like all typed clients from the factory.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAvigAzureDevOps(
        this IServiceCollection services,
        Action<AzureDevOpsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        OptionsBuilder<AzureDevOpsOptions> options = services.AddOptions<AzureDevOpsOptions>();

        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddHttpClient<IPullRequestClient, AzureDevOpsPullRequestClient>(Configure);
        services.AddHttpClient<IWorkItemClient, AzureDevOpsWorkItemClient>(Configure);
        services.AddHttpClient<IBuildClient, AzureDevOpsBuildClient>(Configure);
        services.AddHttpClient<IWikiClient, AzureDevOpsWikiClient>(Configure);

        return services;
    }

    private static void Configure(IServiceProvider provider, HttpClient client)
    {
        AzureDevOpsOptions options = provider.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value;

        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        client.Timeout = options.Timeout;
    }
}
