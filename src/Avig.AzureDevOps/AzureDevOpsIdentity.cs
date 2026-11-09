namespace Avig.AzureDevOps;

/// <summary>A person or group in Azure DevOps.</summary>
/// <param name="Id">The identity's id, a GUID as text.</param>
/// <param name="Name">The display name.</param>
/// <param name="UniqueName">The sign-in name, usually the email address.</param>
/// <param name="ImageUrl">The address of their profile picture, or <c>null</c>. Fetch it with <see cref="Identities.IAvatarClient"/>.</param>
public sealed record AzureDevOpsIdentity(string Id, string Name, string UniqueName, Uri? ImageUrl = null)
{
    /// <summary>
    /// The name used when the server has sent nothing about the person who wrote or started it.
    /// </summary>
    internal const string UnknownName = "unknown";
}
