namespace Avig.AzureDevOps.WorkItems;

/// <summary>An image that a comment points to.</summary>
/// <param name="Alt">The description between the square brackets, often the file name.</param>
/// <param name="Reference">The address or path, as it appears in the text.</param>
public sealed record MarkdownImage(string Alt, string Reference);
