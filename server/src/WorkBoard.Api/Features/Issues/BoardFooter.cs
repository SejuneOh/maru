using System.Text.RegularExpressions;

namespace WorkBoard.Api.Features.Issues;

// Recognises the footer (and marker) GitHubWriter appends to bodies written through the App. Only trust the named
// login when the GitHub author is this App's bot (GitHubAppOptions.IsBot): anyone, another bot included, can type it.
public static partial class BoardFooter
{
    [GeneratedRegex(@"(?:\A|\r?\n\r?\n)---\r?\n_작성: `@(?<login>[A-Za-z0-9][A-Za-z0-9-]*)` \(work-board\)_(?<marker>\r?\n<!-- work-board:(?:create|comment):[0-9a-f]+ -->)?\s*\z")]
    private static partial Regex Pattern();

    // The body without the footer, the login it names, and whether the board's hidden marker follows it (only items
    // created through the board carry one); null when the body does not end with a footer.
    public static (string Body, string Login, bool HasMarker)? Strip(string body)
    {
        var m = Pattern().Match(body);
        return m.Success ? (body[..m.Index], m.Groups["login"].Value, m.Groups["marker"].Success) : null;
    }
}
