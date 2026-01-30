using System.Net;
using System.Text.Json;
using ContactsApi.Core.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ContactsApi.WebApi.Middleware;

/// <summary>
/// Global exception handling middleware
/// Catches all unhandled exceptions and returns standardized error responses
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var response = context.Response;
        response.ContentType = "application/json";

        var errorResponse = new ErrorResponse
        {
            Success = false,
            Error = new ErrorDetails
            {
                Timestamp = DateTime.UtcNow
            }
        };

        // Handle specific exception types
        switch (exception)
        {
            case GoogleApiException googleApiEx:
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                errorResponse.Error.Code = googleApiEx.Code;
                errorResponse.Error.Message = googleApiEx.Message;
                errorResponse.Error.Details = googleApiEx.Context;

                _logger.LogError(
                    googleApiEx,
                    "Google API error occurred: {Code} - {Message}",
                    googleApiEx.Code,
                    googleApiEx.Message);
                break;

            case ArgumentNullException argNullEx:
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                errorResponse.Error.Code = "INVALID_ARGUMENT";
                errorResponse.Error.Message = "A required parameter was null or empty";
                errorResponse.Error.Details = new Dictionary<string, object>
                {
                    { "parameterName", argNullEx.ParamName ?? "unknown" }
                };

                _logger.LogWarning(
                    argNullEx,
                    "Argument null exception: {ParamName}",
                    argNullEx.ParamName);
                break;

            case ArgumentException argEx:
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                errorResponse.Error.Code = "INVALID_ARGUMENT";
                errorResponse.Error.Message = argEx.Message;

                _logger.LogWarning(
                    argEx,
                    "Argument exception: {Message}",
                    argEx.Message);
                break;

            case UnauthorizedAccessException:
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                errorResponse.Error.Code = "UNAUTHORIZED";
                errorResponse.Error.Message = "Authentication required";

                _logger.LogWarning(
                    exception,
                    "Unauthorized access attempt");
                break;

            case KeyNotFoundException:
                response.StatusCode = (int)HttpStatusCode.NotFound;
                errorResponse.Error.Code = "NOT_FOUND";
                errorResponse.Error.Message = "The requested resource was not found";

                _logger.LogWarning(
                    exception,
                    "Resource not found");
                break;

            default:
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                errorResponse.Error.Code = GoogleApiErrorCodes.UnknownError;
                errorResponse.Error.Message = "An unexpected error occurred";

                // Log full details but don't expose to client in production
                _logger.LogError(
                    exception,
                    "Unhandled exception occurred: {Message}",
                    exception.Message);

                // Only include stack trace in development
                if (_environment.IsDevelopment())
                {
                    errorResponse.Error.Details = new Dictionary<string, object>
                    {
                        { "exceptionType", exception.GetType().Name },
                        { "stackTrace", exception.StackTrace ?? "No stack trace available" }
                    };
                }
                break;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = _environment.IsDevelopment()
        };

        var result = JsonSerializer.Serialize(errorResponse, jsonOptions);
        await response.WriteAsync(result);
    }
}

/// <summary>
/// Standardized error response format
/// </summary>
public class ErrorResponse
{
    public bool Success { get; set; }
    public ErrorDetails Error { get; set; } = new();
}

/// <summary>
/// Error details
/// </summary>
public class ErrorDetails
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object>? Details { get; set; }
    public DateTime Timestamp { get; set; }
}
