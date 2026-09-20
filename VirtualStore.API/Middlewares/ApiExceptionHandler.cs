using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using VirtualStore.Application.Common;

namespace VirtualStore.API.Middlewares;

public sealed class ApiExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ApiExceptionHandler> _logger;
    private readonly IHostEnvironment _env;

    public ApiExceptionHandler(ILogger<ApiExceptionHandler> logger, IHostEnvironment env)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _env = env ?? throw new ArgumentNullException(nameof(env));
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted)
            return false;

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        int statusCode;
        string title;
        string detail;
        IDictionary<string, string[]>? errors = null;

        switch (exception)
        {
            case FluentValidation.ValidationException validationException:
                statusCode = StatusCodes.Status400BadRequest;
                title = "Validation Failed";
                detail = "One or more validation errors occurred.";
                errors = validationException.Errors
                    .GroupBy(f => string.IsNullOrEmpty(f.PropertyName) ? "general" : f.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray());
                _logger.LogWarning(exception, "Validation failed. TraceId: {TraceId}", traceId);
                break;
            case AccountLockedException:
                statusCode = StatusCodes.Status423Locked;
                title = "Locked";
                detail = "Account is temporarily locked due to too many failed login attempts.";
                _logger.LogWarning(exception, "Account locked. TraceId: {TraceId}", traceId);
                break;
            case EmailNotConfirmedException:
                statusCode = StatusCodes.Status403Forbidden;
                title = "Email Not Confirmed";
                detail = "Email address is not confirmed.";
                _logger.LogWarning(exception, "Email not confirmed. TraceId: {TraceId}", traceId);
                break;
            case UnauthorizedAccessException:
                statusCode = StatusCodes.Status401Unauthorized;
                title = "Unauthorized";
                detail = "Unauthorized.";
                _logger.LogWarning(exception, "Unauthorized. TraceId: {TraceId}", traceId);
                break;
            case KeyNotFoundException:
                statusCode = StatusCodes.Status404NotFound;
                title = "Not Found";
                detail = "The requested resource was not found.";
                _logger.LogWarning(exception, "Resource not found. TraceId: {TraceId}", traceId);
                break;
            case InvalidOperationException:
                statusCode = StatusCodes.Status409Conflict;
                title = "Conflict";
                detail = "The request conflicts with the current state.";
                _logger.LogWarning(exception, "Conflict. TraceId: {TraceId}", traceId);
                break;
            default:
                statusCode = StatusCodes.Status500InternalServerError;
                title = "Internal Server Error";
                detail = _env.IsDevelopment()
                    ? exception.Message
                    : "An unexpected error occurred.";
                _logger.LogError(exception, "Unhandled exception. TraceId: {TraceId}", traceId);
                break;
        }

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = traceId;
        if (errors != null)
            problem.Extensions["errors"] = errors;

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);

        return true;
    }
}
