using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Avig.AzureDevOps.WorkItems;

/// <summary>
/// Turns HTML from a work item into markdown that can be edited.
/// </summary>
/// <remarks>
/// The opposite of a markdown-to-HTML conversion, and deliberately limited to exactly what
/// it can write back: headings, lists, quotes, code, links and emphasis. What goes in should
/// be able to come out again without losing its form.
///
/// Images and tables are out of scope. They cannot be expressed in the markdown the tool
/// writes, and a text being edited would silently lose them. That is why
/// <see cref="HasUnsupported"/> exists: whoever changes a field with an image in it should
/// be told before anything is overwritten, not discover it afterwards.
///
/// This is not an HTML parser. It recognizes the tags Azure DevOps actually writes and
/// lets the rest fall away as text.
/// </remarks>
public static partial class WorkItemMarkdown
{
    /// <summary>Elements whose content must never be included.</summary>
    [GeneratedRegex(
        @"<(?<name>script|style|iframe|object|embed)\b[^>]*>.*?</\k<name>\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Forbidden();

    /// <summary>A code block: <c>pre</c> with or without <c>code</c> inside.</summary>
    [GeneratedRegex(
        @"<pre\b[^>]*>\s*(?:<code\b[^>]*>)?(?<code>.*?)(?:</code>)?\s*</pre>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CodeBlock();

    /// <summary>A numbered list, which needs to be counted.</summary>
    [GeneratedRegex(
        @"<ol\b[^>]*>(?<items>.*?)</ol>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex OrderedList();

    /// <summary>An item in a list.</summary>
    [GeneratedRegex(
        @"<li\b[^>]*>(?<text>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ListItem();

    /// <summary>A link with an address.</summary>
    [GeneratedRegex(
        @"<a\b[^>]*?href\s*=\s*[""'](?<url>[^""']*)[""'][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Link();

    /// <summary>A heading.</summary>
    [GeneratedRegex(
        @"<h(?<level>[1-6])\b[^>]*>(?<text>.*?)</h\k<level>>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Heading();

    /// <summary>A quote.</summary>
    [GeneratedRegex(
        @"<blockquote\b[^>]*>(?<text>.*?)</blockquote>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Quote();

    /// <summary>An image, with the address and the description among the attributes.</summary>
    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Image();

    /// <summary>An attribute in a tag: name in <c>name</c>, value in <c>value</c>.</summary>
    [GeneratedRegex(
        @"\b(?<name>src|alt)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ImageAttribute();

    /// <summary>A table.</summary>
    [GeneratedRegex(@"<table\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Table();

    /// <summary>
    /// The character that holds the place of an image while the rest is rewritten.
    /// </summary>
    private const char ImagePlaceholder = '\u0001';

    /// <summary>The description for an image that lacks one.</summary>
    private const string DefaultImageAlt = "image";

    /// <summary>What cannot be expressed in markdown: images and tables.</summary>
    [GeneratedRegex(@"<(?<tag>img|table)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Unsupported();

    /// <summary>Any tag at all.</summary>
    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AnyTag();

    /// <summary>
    /// The character that holds the place of a code block while the rest is rewritten.
    /// </summary>
    private const char Placeholder = '\u0000';

    /// <summary>
    /// Indicates whether the text contains something that an edit would lose.
    /// </summary>
    /// <param name="html">The HTML from a work item field or comment, or <c>null</c>.</param>
    /// <remarks>
    /// Images and tables. Whoever changes such a field gets a warning and can choose
    /// to open the work item in Azure DevOps instead.
    /// </remarks>
    public static bool HasUnsupported(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        try
        {
            return Unsupported().IsMatch(html);
        }
        catch (RegexMatchTimeoutException)
        {
            // If the text cannot be scanned, the safest thing is to assume the worst.
            return true;
        }
    }

    /// <summary>
    /// Indicates whether the text has a table, which cannot be shown as markdown.
    /// </summary>
    /// <param name="html">The HTML from a work item field or comment, or <c>null</c>.</param>
    public static bool HasTables(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        try
        {
            return Table().IsMatch(html);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>
    /// The images in the text, in the order they appear: the address as it appears in
    /// <c>src</c> and the description in <c>alt</c>.
    /// </summary>
    /// <param name="html">The HTML from a work item field or comment, or <c>null</c>.</param>
    /// <remarks>Returns an empty list when the text cannot be gone through in time.</remarks>
    public static IReadOnlyList<MarkdownImage> Images(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        try
        {
            return
            [
                .. Image().Matches(html)
                    .Select(match => Attributes(match.Value))
                    .Where(image => image.Reference.Length > 0),
            ];
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    /// <summary>The text as markdown, ready to be edited.</summary>
    /// <param name="html">The HTML from a work item field or comment, or <c>null</c>.</param>
    public static string FromHtml(string? html) => FromHtml(html, images: null);

    /// <summary>
    /// The text as markdown for reading, with the images left where they were.
    /// </summary>
    /// <param name="html">The field as Azure DevOps stores it.</param>
    /// <param name="images">
    /// The address in <c>src</c> mapped to the path the image was downloaded to. An image missing
    /// here keeps its address. With <c>null</c> the images are dropped, as when the text is to be
    /// edited.
    /// </param>
    /// <remarks>
    /// Only for reading. Markdown written back to the work item does not include images,
    /// and a path on this machine does not belong in Azure DevOps.
    /// </remarks>
    public static string FromHtml(string? html, IReadOnlyDictionary<string, string>? images)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        try
        {
            return Convert(html, images);
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }

    /// <summary>The conversion itself, for HTML that is not empty.</summary>
    /// <param name="html">The HTML, which is not empty.</param>
    /// <param name="images">The downloaded images by address, or <c>null</c> to leave the images out.</param>
    /// <remarks>
    /// Code blocks and images are set aside first and put back last, so that nothing in between
    /// reads their content as markdown.
    /// </remarks>
    private static string Convert(string html, IReadOnlyDictionary<string, string>? images)
    {
        string text = Forbidden().Replace(html, string.Empty);

        // The images are set aside like the code blocks, so that the path is not read as text —
        // an underscore in a file name is not italics.
        List<string> pictures = [];

        if (images is not null)
        {
            text = Image().Replace(
                text,
                match =>
                {
                    MarkdownImage image = Attributes(match.Value);

                    if (image.Reference.Length == 0)
                    {
                        return string.Empty;
                    }

                    // An image that has been downloaded points at the file, otherwise the address stays.
                    string target = image.Reference;

                    if (images.TryGetValue(image.Reference, out string? local))
                    {
                        target = local;
                    }

                    string alt = image.Alt.Replace('[', '(').Replace(']', ')');

                    if (alt.Length == 0)
                    {
                        alt = DefaultImageAlt;
                    }

                    pictures.Add($"![{alt}]({target})");

                    return $"{ImagePlaceholder}{pictures.Count - 1}{ImagePlaceholder}";
                });
        }

        // The code blocks are set aside first. Their content must not be touched by any of
        // what comes after — an asterisk in code is an asterisk, not italics.
        List<string> blocks = [];

        text = CodeBlock().Replace(
            text,
            match =>
            {
                blocks.Add(Decode(AnyTag().Replace(match.Groups["code"].Value, string.Empty)).Trim('\n'));

                return $"\n{Placeholder}{blocks.Count - 1}{Placeholder}\n";
            });

        // List items are separated by a line break and not by a blank line: a blank line between
        // the items turns the markdown into loose paragraphs instead of a list.
        text = OrderedList().Replace(text, match => Numbered(match.Groups["items"].Value));
        text = ListItem().Replace(text, match => "\n- " + Inline(match.Groups["text"].Value));

        text = Heading().Replace(
            text,
            match => "\n\n"
                + new string('#', int.Parse(match.Groups["level"].Value, CultureInfo.InvariantCulture))
                + " "
                + Inline(match.Groups["text"].Value)
                + "\n\n");

        text = Quote().Replace(text, match => "\n\n" + Prefix(Inline(match.Groups["text"].Value), "> ") + "\n\n");
        text = Link().Replace(text, match => $"[{Inline(match.Groups["text"].Value)}]({match.Groups["url"].Value})");

        text = Inline(text);
        text = Collapse(text);

        // The code blocks back, as fences.
        for (int index = 0; index < blocks.Count; index++)
        {
            text = text.Replace(
                $"{Placeholder}{index.ToString(CultureInfo.InvariantCulture)}{Placeholder}",
                "```\n" + blocks[index] + "\n```",
                StringComparison.Ordinal);
        }

        for (int index = 0; index < pictures.Count; index++)
        {
            text = text.Replace(
                $"{ImagePlaceholder}{index.ToString(CultureInfo.InvariantCulture)}{ImagePlaceholder}",
                pictures[index],
                StringComparison.Ordinal);
        }

        return text.Trim();
    }

    /// <summary>The address and the description in an <c>img</c> tag, decoded.</summary>
    /// <param name="tag">The <c>img</c> tag.</param>
    private static MarkdownImage Attributes(string tag)
    {
        string source = string.Empty;
        string alt = string.Empty;

        foreach (Match attribute in ImageAttribute().Matches(tag))
        {
            string value = WebUtility.HtmlDecode(attribute.Groups["value"].Value).Trim();

            if (string.Equals(attribute.Groups["name"].Value, "src", StringComparison.OrdinalIgnoreCase))
            {
                source = value;
            }
            else
            {
                alt = value;
            }
        }

        return new MarkdownImage(alt, source);
    }

    /// <summary>The items in a numbered list, with their numbers.</summary>
    /// <param name="items">The HTML inside the <c>ol</c> tag.</param>
    private static string Numbered(string items)
    {
        StringBuilder result = new();
        int number = 1;

        foreach (Match item in ListItem().Matches(items))
        {
            result.Append('\n')
                .Append(number.ToString(CultureInfo.InvariantCulture))
                .Append(". ")
                .Append(Inline(item.Groups["text"].Value));

            number++;
        }

        return result.Append('\n').ToString();
    }

    /// <summary>Emphasis, code and line breaks inside a paragraph.</summary>
    /// <param name="html">The HTML of a paragraph or a list item.</param>
    private static string Inline(string html)
    {
        string text = html;

        text = Replace(text, "strong", "**");
        text = Replace(text, "b", "**");
        text = Replace(text, "em", "*");
        text = Replace(text, "i", "*");
        text = Replace(text, "s", "~~");
        text = Replace(text, "del", "~~");
        text = Replace(text, "code", "`");

        // A paragraph that ends is a blank line in markdown. Without it two paragraphs are merged
        // into one when the text is written back.
        text = Regex.Replace(
            text,
            @"<\s*/\s*(?<tag>p|div)\s*>",
            "\n\n",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        // A line break is just a line break.
        text = Regex.Replace(
            text,
            @"<\s*(?<tag>br|/tr)\s*/?>",
            "\n",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        return Decode(AnyTag().Replace(text, string.Empty));
    }

    /// <summary>Replaces a pair of tags with a markdown character.</summary>
    /// <param name="html">The HTML from a work item field or comment, or <c>null</c>.</param>
    /// <param name="tag">The tag's name, for example <c>strong</c>.</param>
    /// <param name="marker">The markdown that replaces the tags, for example <c>**</c>.</param>
    private static string Replace(string html, string tag, string marker) => Regex.Replace(
        html,
        $@"<{tag}\b[^>]*>(?<text>.*?)</{tag}\s*>",
        match => marker + match.Groups["text"].Value.Trim() + marker,
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        TimeSpan.FromSeconds(1));

    /// <summary>Puts <paramref name="marker"/> before every line that has text, as a quote needs.</summary>
    /// <param name="text">The text.</param>
    /// <param name="marker">What each line starts with, for example <c>&gt; </c>.</param>
    private static string Prefix(string text, string marker) => string.Join(
        '\n',
        text.Split('\n').Select(line =>
        {
            if (line.Trim().Length == 0)
            {
                return line;
            }

            return marker + line.Trim();
        }));

    /// <summary>The text with HTML entities decoded and non-breaking spaces made ordinary.</summary>
    /// <param name="text">The text.</param>
    private static string Decode(string text) => WebUtility.HtmlDecode(text).Replace(' ', ' ');

    /// <summary>
    /// Collapses the whitespace the tags have left behind.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <remarks>
    /// At most one blank line in a row. Azure DevOps likes to write
    /// <c>&lt;p&gt;&lt;br&gt;&lt;/p&gt;</c> between paragraphs, and without this the text becomes
    /// full of holes.
    /// </remarks>
    private static string Collapse(string text)
    {
        List<string> lines = [];
        bool previousBlank = false;

        foreach (string raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            // TrimEnd removes all whitespace, including the non-breaking space, so a line that only
            // had whitespace is empty afterwards.
            string line = raw.TrimEnd();
            bool blank = line.Length == 0;

            if (blank && previousBlank)
            {
                continue;
            }

            lines.Add(line);
            previousBlank = blank;
        }

        return string.Join('\n', lines);
    }
}
