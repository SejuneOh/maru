using System.Text.RegularExpressions;

namespace WorkBoard.Api.Features.Issues;

// Recognises the footer (and marker) GitHubWriter appends to bodies written through the App. Only trust the named
// login when the GitHub author is a bot: anyone can type the same text into their own comment.
public static partial class BoardFooter
{
    [GeneratedRegex(@"(?:\A|\r?\n\r?\n)---\r?\n_작성: `@(?<login>[A-Za-z0-9][A-Za-z0-9-]*)` \(work-board\)_(?:\r?\n<!-- work-board:(?:create|comment):[0-9a-f]+ -->)?\s*\z")]
    private static partial Regex Pattern();

    public static bool IsBot(string? login) => login?.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) == true;

    // The body without the footer, and the login it names; null when the body does not end with one.
    public static (string Body, string Login)? Strip(string body)
    {
        var m = Pattern().Match(body);
        return m.Success ? (body[..m.Index], m.Groups["login"].Value) : null;
    }
}
