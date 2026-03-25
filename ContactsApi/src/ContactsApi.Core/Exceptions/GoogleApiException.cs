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
    /// Initializes a new instance of GoogleApiException
    /// </summary>
    /// <param name="code">Error code</param>
    /// <param name="message">Error message</param>
    /// <param name="originalError">Original exception</param>
    /// <param name="context">Additional context</param>
    public GoogleApiException(
        string code,
        string message,
        Exception? originalError = null,
        Dictionary<string, object>? context = null)
        : base(message, originalError)
    {
        Code = code;
        OriginalError = originalError;
        Context = context;
        Timestamp = DateTime.UtcNow;
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
    public const string TokenRefreshFailed = "TOKEN_REFRESH_FAILED";
    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";
    public const string UnknownError = "UNKNOWN_ERROR";
}
