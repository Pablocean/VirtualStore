namespace VirtualStore.Application.Common;

public class PagedResult<T>
{
    private List<T> _items = new();
    private int _pageNumber = 1;
    private int _pageSize = 20;
    private int _totalCount;

    public List<T> Items
    {
        get => _items;
        set => _items = value ?? new();
    }

    public int TotalCount
    {
        get => _totalCount;
        set => _totalCount = value < 0 ? 0 : value;
    }

    public int PageNumber
    {
        get => _pageNumber;
        set => _pageNumber = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value <= 0 ? 20 : Math.Min(value, 100);
    }

    public int TotalPages
    {
        get
        {
            if (TotalCount <= 0 || PageSize <= 0) return 0;
            return (int)Math.Ceiling(TotalCount / (double)PageSize);
        }
    }

    public bool HasPrevious => PageNumber > 1;

    public bool HasNext => TotalPages > 0 && PageNumber < TotalPages;
}
