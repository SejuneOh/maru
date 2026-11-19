namespace WorkBoard.Api.Infrastructure.Credentials;

public sealed class CredentialOptions
{
    public const string Section = "GitHub:Credentials";

    // Absolute, or relative to the API content root. Mode 600, outside git.
    public string? KeyFile { get; set; }

    // The key file's JSON, for hosts that can only pass secrets as environment variables
    // (GitHub__Credentials__Key). KeyFile wins when both are set.
    public string? Key { get; set; }
}
