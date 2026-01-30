namespace ContactsApi.WebApi.Models.Responses;

/// <summary>
/// Generic API response wrapper
/// </summary>
/// <typeparam name="T">Type of data being returned</typeparam>
public class ApiResponse<T>
{
    /// <summary>
    /// Indicates if the operation was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Response data (present when Success = true)
    /// </summary>
    public T? Data { get; set; }

    /// <summary>
    /// Error information (present when Success = false)
    /// </summary>
    public ErrorInfo? Error { get; set; }

    /// <summary>
    /// Creates a successful response
    /// </summary>
    public static ApiResponse<T> SuccessResponse(T data)
    {
        return new ApiResponse<T>
        {
            Success = true,
            Data = data
        };
    }

    /// <summary>
    /// Creates an error response
    /// </summary>
    public static ApiResponse<T> ErrorResponse(string code, string message, object? details = null)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Error = new ErrorInfo
            {
                Code = code,
                Message = message,
                Details = details,
                Timestamp = DateTime.UtcNow
            }
        };
    }
}

/// <summary>
/// Error information
/// </summary>
public class ErrorInfo
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public object? Details { get; set; }
    public DateTime Timestamp { get; set; }
}
