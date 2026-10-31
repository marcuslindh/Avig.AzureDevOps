using Avig.AzureDevOps.Builds;
using Avig.AzureDevOps.PullRequests;
using Avig.AzureDevOps.Wiki;
using Avig.AzureDevOps.WorkItems;
using Microsoft.Extensions.DependencyInjection;

namespace Avig.AzureDevOps.Tests;

/// <summary>
/// The registration and the sign-in: the application owns the token, the package only asks
/// for it.
/// </summary>
public sealed class RegistrationTests
{
    [Fact]
    public void AllFourClientsResolve()
    {
        using ServiceProvider provider = Build(services => services.AddAvigAzureDevOps(() => "secret"));

        Assert.IsType<AzureDevOpsPullRequestClient>(provider.GetRequiredService<IPullRequestClient>());
        Assert.IsType<AzureDevOpsWorkItemClient>(provider.GetRequiredService<IWorkItemClient>());
        Assert.IsType<AzureDevOpsBuildClient>(provider.GetRequiredService<IBuildClient>());
        Assert.IsType<AzureDevOpsWikiClient>(provider.GetRequiredService<IWikiClient>());
    }

    [Fact]
    public void TheTokenFunctionBecomesTheCredential()
    {
        using ServiceProvider provider = Build(services => services.AddAvigAzureDevOps(() => "secret"));

        Assert.IsType<PersonalAccessTokenCredential>(provider.GetRequiredService<IAzureDevOpsCredential>());
    }

    [Fact]
    public void TheTokenCanComeFromTheApplicationsServices()
    {
        TokenHolder holder = new() { Token = "secret" };

        using ServiceProvider provider = Build(services =>
        {
            services.AddSingleton(holder);
            services.AddAvigAzureDevOps(serviceProvider => serviceProvider.GetRequiredService<TokenHolder>().Token);
        });

        Assert.True(provider.GetRequiredService<IWorkItemClient>().IsConnected);
    }

    [Fact]
    public void ACredentialRegisteredFirstIsKept()
    {
        using ServiceProvider provider = Build(services =>
        {
            services.AddSingleton<IAzureDevOpsCredential, FakeCredential>();
            services.AddAvigAzureDevOps(() => "secret");
        });

        Assert.IsType<FakeCredential>(provider.GetRequiredService<IAzureDevOpsCredential>());
    }

    [Fact]
    public void WithoutATokenOverloadTheApplicationsOwnCredentialIsUsed()
    {
        using ServiceProvider provider = Build(services =>
        {
            services.AddAvigAzureDevOps(options => options.UserAgent = "Test/1.0");
            services.AddSingleton<IAzureDevOpsCredential, FakeCredential>();
        });

        Assert.IsType<FakeCredential>(provider.GetRequiredService<IAzureDevOpsCredential>());
        Assert.True(provider.GetRequiredService<IBuildClient>().IsConnected);
    }

    [Fact]
    public async Task TheTokenIsSentAsBasicWithAnEmptyUserName()
    {
        PersonalAccessTokenCredential credential = new(() => "  secret  ");

        using HttpRequestMessage request = new(HttpMethod.Get, "https://dev.azure.com/contoso/");
        await credential.AuthorizeAsync(request);

        Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(":secret"u8.ToArray()), request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void TheFunctionIsAskedEveryTime()
    {
        // A token that is saved or removed takes effect without registering anything again.
        TokenHolder holder = new();
        PersonalAccessTokenCredential credential = new(() => holder.Token);

        Assert.False(credential.IsConfigured);

        holder.Token = "secret";
        Assert.True(credential.IsConfigured);

        holder.Token = null;
        Assert.False(credential.IsConfigured);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyTokenIsNoSignIn(string? token)
    {
        Assert.False(new PersonalAccessTokenCredential(() => token).IsConfigured);
    }

    [Fact]
    public async Task WithoutATokenTheRequestIsRefusedWithAMessage()
    {
        PersonalAccessTokenCredential credential = new(() => null);

        using HttpRequestMessage request = new(HttpMethod.Get, "https://dev.azure.com/contoso/");
        Result result = await credential.AuthorizeAsync(request);

        Assert.True(result.IsFailure(out string? error, out ResultStatus status));
        Assert.Equal(ResultStatus.Unauthorized, status);
        Assert.Contains("access token", error, StringComparison.Ordinal);
    }

    private static ServiceProvider Build(Action<ServiceCollection> register)
    {
        ServiceCollection services = new();
        services.AddLogging();
        register(services);

        return services.BuildServiceProvider();
    }

    private sealed class TokenHolder
    {
        public string? Token { get; set; }
    }
}
