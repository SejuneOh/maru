namespace WorkBoard.Api.Infrastructure.Identity;

public interface ICurrentUser
{
    DevIdentity Identity { get; }

    // The AI tool (MCP client) token acting on the owner's behalf, set only on a /mcp write; null for ordinary requests.
    Guid? McpClientId => null;
}

public sealed class CurrentUser : ICurrentUser
{
    private DevIdentity? _identity;

    public DevIdentity Identity =>
        _identity ?? throw new InvalidOperationException("No authenticated user on this request.");

    public Guid? McpClientId { get; private set; }

    public void Set(DevIdentity identity, Guid? mcpClientId = null)
    {
        _identity = identity;
        McpClientId = mcpClientId;
    }
}
