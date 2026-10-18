namespace ContactsApi.Core.Exceptions;

/// <summary>
/// Custom exception for Google API errors with context
/// </summary>
public class GoogleApiException : Exception
{
    /// <summary>
    /// Error code for categorization
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Original underlying exception
    /// </summary>
    public Exception? OriginalError { get; }

    /// <summary>
    /// Additional contextual metadata
    /// </summary>
    public Dictionary<string, object>? Context { get; }

    /// <summary>
    /// Timestamp when error occurred
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// HTTP status the API should answer with (400 unless the cause is more specific,
    /// e.g. 412 for an etag conflict or 403 for a missing OAuth scope)
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// Initializes a new instance of GoogleApiException
    /// </summary>
    /// <param name="code">Error code</param>
    /// <param name="message">Error message</param>
    /// <param name="originalError">Original exception</param>
    /// <param name="context">Additional context</param>
    /// <param name="statusCode">HTTP status to return to the client</param>
    public GoogleApiException(
        string code,
        string message,
        Exception? originalError = null,
        Dictionary<string, object>? context = null,
        int statusCode = 400)
        : base(message, originalError)
    {
        Code = code;
        OriginalError = originalError;
        Context = context;
        Timestamp = DateTime.UtcNow;
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets formatted error message with full context
    /// </summary>
    public string GetDetailedMessage()
    {
        var details = $"[{Code}] {Message}";

        if (Context != null && Context.Count > 0)
        {
            details += $"\nContext: {string.Join(", ", Context.Select(kv => $"{kv.Key}={kv.Value}"))}";
        }

        if (OriginalError != null)
        {
            details += $"\nCaused by: {OriginalError.Message}";
        }

        return details;
    }
}

/// <summary>
/// Common error codes for Google API operations
/// </summary>
public static class GoogleApiErrorCodes
{
    public const string InvalidAccessToken = "INVALID_ACCESS_TOKEN";
    public const string FetchContactsFailed = "FETCH_CONTACTS_FAILED";
    public const string GetContactFailed = "GET_CONTACT_FAILED";
    public const string UpdateContactFailed = "UPDATE_CONTACT_FAILED";
    public const string CreateContactFailed = "CREATE_CONTACT_FAILED";
    public const string ContactConflict = "CONTACT_CONFLICT";
    public const string ContactNotFound = "CONTACT_NOT_FOUND";
    public const string InsufficientScope = "INSUFFICIENT_SCOPE";
    public const string RateLimited = "RATE_LIMITED";
    public const string FetchGroupsFailed = "FETCH_GROUPS_FAILED";
    public const string DeleteContactFailed = "DELETE_CONTACT_FAILED";
    public const string ModifyGroupFailed = "MODIFY_GROUP_FAILED";
    public const string MergeWouldLoseData = "MERGE_WOULD_LOSE_DATA";
    public const string TokenRefreshFailed = "TOKEN_REFRESH_FAILED";
    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";
    public const string UnknownError = "UNKNOWN_ERROR";
}
