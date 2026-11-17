using System.Text.RegularExpressions;

namespace WorkBoard.Api.Infrastructure.Credentials;

// Replaces GitHub token shapes (ghp_, gho_, ghu_, ghs_, ghr_, github_pat_) so text that is logged or stored cannot leak one.
public static partial class TokenMasker
{
    public const string Mask = "[token]";

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,})")]
    private static partial Regex TokenPattern();

    public static string? Apply(string? text) => string.IsNullOrEmpty(text) ? text : TokenPattern().Replace(text, Mask);
}
