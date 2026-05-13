namespace PowerBase.API.Models;

public class ApiResponse<T>
{
    public T Data { get; init; }
    public ApiMeta Meta { get; init; }

    public ApiResponse(T data)
    {
        Data = data;
        Meta = new ApiMeta();
    }
}

public class ApiListResponse<T>
{
    public IReadOnlyList<T> Data { get; init; }
    public ApiListMeta Meta { get; init; }

    public ApiListResponse(IReadOnlyList<T> data, int total, int page, int pageSize)
    {
        Data = data;
        Meta = new ApiListMeta { Total = total, Page = page, PageSize = pageSize };
    }
}

public class ApiMeta
{
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("o");
}

public class ApiListMeta : ApiMeta
{
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}
