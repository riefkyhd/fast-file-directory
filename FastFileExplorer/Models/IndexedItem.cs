namespace FastFileExplorer.Models;

public enum IndexedItemKind
{
    File,
    Folder
}

public enum ItemFilter
{
    All,
    Folder,
    File,
    Document,
    Picture,
    Video
}

public enum DateFilter
{
    All,
    Last1Day,
    Last7Days,
    Last30Days,
    Last365Days
}

public sealed class SearchOptions
{
    public ItemFilter ItemFilter { get; init; } = ItemFilter.All;
    public DateFilter DateFilter { get; init; } = DateFilter.All;
}

public sealed class IndexedItem
{
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required string Directory { get; init; }
    public required string Extension { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }
    public required long SizeBytes { get; init; }
    public required IndexedItemKind Kind { get; init; }
    public required string NormalizedName { get; init; }
}
