namespace Edgewise.Contracts.Common;

/// <summary>Standard paging envelope for list endpoints.</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount)
{
    public bool HasMore => (long)Page * PageSize < TotalCount;
}
