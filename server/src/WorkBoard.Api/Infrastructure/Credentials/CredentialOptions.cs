namespace WorkBoard.Api.Infrastructure.Credentials;

public sealed class CredentialOptions
{
    public const string Section = "GitHub:Credentials";

    // Absolute, or relative to the API content root. Mode 600, outside git.
    public string? KeyFile { get; set; }
}
