namespace D2D.Core;

/// <summary>Expected failure that maps directly to an HTTP problem response.</summary>
public class ApiException(int statusCode, string title, string? detail = null) : Exception(detail ?? title)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;

    public static ApiException BadRequest(string title, string? detail = null) => new(400, title, detail);
    public static ApiException Unauthorized(string title = "Unauthorized", string? detail = null) => new(401, title, detail);
    public static ApiException Forbidden(string title, string? detail = null) => new(403, title, detail);
    public static ApiException NotFound(string title, string? detail = null) => new(404, title, detail);
    public static ApiException Conflict(string title, string? detail = null) => new(409, title, detail);
}
