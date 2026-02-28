using System.ClientModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AiCodeReview.Services;

/// <summary>
/// Utility methods for parsing rate-limit response headers and computing
/// appropriate retry delay values from Azure OpenAI 429 responses.
/// </summary>
public static class RateLimitHelper
{
    /// <summary>
    /// Default minimum delay for 429 rate-limit retries when no Retry-After
    /// header is available and no value can be parsed from the error message.
    /// Azure OpenAI S0 tier typically requires 60 seconds.
    /// </summary>
    public const int DefaultRetryAfterSeconds = 30;

    /// <summary>
    /// Maximum per-retry delay cap to prevent indefinitely long waits.
    /// </summary>
    public const int MaxRetryAfterSeconds = 120;

    /// <summary>
    /// The number of additional seconds to add above the Retry-After value
    /// as a buffer to reduce the chance of a second immediate 429.
    /// </summary>
    public const int RetryAfterBufferSeconds = 5;

    /// <summary>
    /// Maximum number of retries for 429 rate-limit errors.
    /// Set higher than the old value of 3 because we now honour proper back-off durations.
    /// With 5 retries at ~60s each, total per-call retry budget is ~5 minutes.
    /// </summary>
    public const int MaxRateLimitRetries = 5;

    /// <summary>
    /// Maximum total retry duration per-call (5 minutes).
    /// If cumulative delay exceeds this, we stop retrying even if max retries not reached.
    /// </summary>
    public static readonly TimeSpan MaxTotalRetryDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Compute the delay to wait before retrying after a 429 rate-limit response.
    /// <para>
    /// Resolution order:
    /// 1. Retry-After HTTP header from the raw response (if <see cref="ClientResultException"/>
    ///    provides one via <c>GetRawResponse()</c>)
    /// 2. "retry after N seconds" pattern parsed from the exception message
    /// 3. <see cref="DefaultRetryAfterSeconds"/> (30 s)
    /// </para>
    /// The result is clamped to [1, <see cref="MaxRetryAfterSeconds"/>] and a small
    /// <see cref="RetryAfterBufferSeconds"/> buffer is added.
    /// </summary>
    /// <param name="exception">
    /// The exception caught on a 429 response. May be <see cref="ClientResultException"/>
    /// (Azure.AI.OpenAI SDK) or any other exception type.
    /// </param>
    /// <returns>A <see cref="TimeSpan"/> representing how long to wait before retrying.</returns>
    public static TimeSpan ComputeRetryDelay(Exception exception)
    {
        int retryAfterSeconds = DefaultRetryAfterSeconds;

        // ─── 1. Try Retry-After header from the raw HTTP response ───────────
        if (exception is ClientResultException cex)
        {
            retryAfterSeconds = TryParseRetryAfterFromResponse(cex)
                             ?? TryParseRetryAfterFromMessage(cex.Message)
                             ?? DefaultRetryAfterSeconds;
        }
        else
        {
            retryAfterSeconds = TryParseRetryAfterFromMessage(exception.Message)
                             ?? DefaultRetryAfterSeconds;
        }

        // Clamp and add buffer
        retryAfterSeconds = Math.Clamp(retryAfterSeconds, 1, MaxRetryAfterSeconds)
                          + RetryAfterBufferSeconds;

        return TimeSpan.FromSeconds(retryAfterSeconds);
    }

    /// <summary>
    /// Attempts to parse the <c>Retry-After</c> header from a <see cref="ClientResultException"/>.
    /// The header may contain either an integer (seconds) or an HTTP-date per RFC 7231 §7.1.3.
    /// </summary>
    internal static int? TryParseRetryAfterFromResponse(ClientResultException exception)
    {
        try
        {
            var response = exception.GetRawResponse();
            if (response == null)
                return null;

            // PipelineResponseHeaders.TryGetValue returns the first header value
            if (!response.Headers.TryGetValue("Retry-After", out var headerValue)
                || string.IsNullOrWhiteSpace(headerValue))
                return null;

            // Case 1: Integer seconds (most common for Azure OpenAI)
            if (int.TryParse(headerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                return seconds;

            // Case 2: HTTP-date (e.g., "Thu, 01 Dec 2025 16:00:00 GMT")
            if (DateTimeOffset.TryParseExact(
                    headerValue,
                    "r", // RFC 1123 format
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var retryDate))
            {
                var delta = retryDate - DateTimeOffset.UtcNow;
                return Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));
            }

            return null;
        }
        catch
        {
            // GetRawResponse() might be null or not available
            return null;
        }
    }

    /// <summary>
    /// Attempts to parse "retry after N seconds" from an exception message.
    /// Azure OpenAI error messages sometimes embed this information.
    /// </summary>
    internal static int? TryParseRetryAfterFromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var match = Regex.Match(
            message,
            @"retry\s+after\s+(\d+)\s+second",
            RegexOptions.IgnoreCase);

        if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed) && parsed > 0)
            return parsed;

        return null;
    }
}
