namespace WorkBoard.Api.Features.Repositories;

public sealed class RepositoriesOptions
{
    public const string Section = "Repositories";

    // Folder levels shown in the tree (the root's entries are level 1).
    public int TreeDepth { get; set; } = 3;

    // Nodes returned in the tree; shallower levels are kept first.
    public int MaxTreeNodes { get; set; } = 2000;

    // Folder reads allowed when GitHub cuts the recursive listing short.
    public int MaxTreeRequests { get; set; } = 40;

    public int MaxDependencyFiles { get; set; } = 5;
    public int MaxDependencyFileBytes { get; set; } = 1024 * 1024;
    public int MaxDependencyItems { get; set; } = 300;

    public int ReadmeMaxBytes { get; set; } = 256 * 1024;

    // How long a result stays in memory. A new default-branch commit gets a new cache key regardless.
    public int CacheTtlSeconds { get; set; } = 600;
}
