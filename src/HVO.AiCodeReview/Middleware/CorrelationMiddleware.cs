using HVO.Enterprise.Telemetry.Correlation;

namespace AiCodeReview.Middleware;

/// <summary>
/// Middleware that reads or generates an X-Correlation-ID header and
/// sets it on <see cref="CorrelationContext"/> for the duration of the request.
/// The correlation ID is also returned in the response header so callers
/// can trace their request end-to-end.
/// </summary>
public sealed class CorrelationMiddleware
{
    private const string CorrelationHeader = "X-Correlation-ID";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationMiddleware> _logger;

    public CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Read or generate a correlation ID
        var correlationId = context.Request.Headers[CorrelationHeader].FirstOrDefault()
            ?? Guid.NewGuid().ToString("D");

        // Begin a correlation scope for downstream telemetry operations
        using var scope = CorrelationContext.BeginScope(correlationId);

        // Echo the correlation ID back in the response
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationHeader] = correlationId;
            return Task.CompletedTask;
        });

        _logger.LogDebug("Correlation ID: {CorrelationId}", correlationId);

        await _next(context);
    }
}

/// <summary>
/// Extension methods for <see cref="CorrelationMiddleware"/>.
/// </summary>
public static class CorrelationMiddlewareExtensions
{
    /// <summary>
    /// Adds X-Correlation-ID propagation middleware to the pipeline.
    /// </summary>
    public static IApplicationBuilder UseCorrelation(this IApplicationBuilder app)
        => app.UseMiddleware<CorrelationMiddleware>();
}
