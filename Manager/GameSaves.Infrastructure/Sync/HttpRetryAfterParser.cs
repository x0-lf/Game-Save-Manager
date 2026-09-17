using GameSaves.Core.Sync;
using System.Globalization;
using System.Net.Http.Headers;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// Parses standard HTTP Retry-After headers (RFC 7231 / RFC 9110 Section 10.2.3)
    /// into bounded TimeSpan durations.
    ///
    /// The Retry-After header may specify either:
    /// 1. A non-negative integer number of seconds (delay-seconds, e.g. "120").
    /// 2. An HTTP date timestamp (e.g. "Fri, 31 Dec 2026 23:59:59 GMT").
    /// </summary>
    internal static class HttpRetryAfterParser
    {
        private static readonly string[] HttpDateFormats =
        [
            // RFC 1123 / RFC 822
            "r",
            "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
            "ddd, dd MMM yyyy HH:mm:ss zzz",
            // RFC 850
            "dddd, dd-MMM-yy HH:mm:ss 'GMT'",
            // ANSI C asctime()
            "ddd MMM d HH:mm:ss yyyy",
            "ddd MMM  d HH:mm:ss yyyy"
        ];

        public static bool TryParse(
            string? headerValue,
            IUtcClock clock,
            out TimeSpan delay)
        {
            ArgumentNullException.ThrowIfNull(clock);

            delay = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(headerValue))
                return false;

            string trimmed = headerValue.Trim();

            // 1. Integer seconds format (non-negative digits)
            if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
            {
                if (seconds < 0)
                    return false;

                delay = TimeSpan.FromSeconds(seconds);
                return true;
            }

            // Also check long in case a large seconds value is supplied
            if (long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out long longSeconds))
            {
                if (longSeconds < 0)
                    return false;

                delay = longSeconds > int.MaxValue
                    ? TimeSpan.MaxValue
                    : TimeSpan.FromSeconds(longSeconds);
                return true;
            }

            // 2. HTTP date format
            if (DateTimeOffset.TryParseExact(
                    trimmed,
                    HttpDateFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset targetDate))
            {
                DateTimeOffset now = clock.UtcNow;
                TimeSpan delta = targetDate - now;

                // If the instructed date is in the past, return TimeSpan.Zero
                delay = delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
                return true;
            }

            // 3. Fallback to standard RetryConditionHeaderValue parser
            if (RetryConditionHeaderValue.TryParse(trimmed, out RetryConditionHeaderValue? condition))
            {
                if (condition.Delta.HasValue)
                {
                    TimeSpan delta = condition.Delta.Value;
                    if (delta < TimeSpan.Zero)
                        return false;

                    delay = delta;
                    return true;
                }

                if (condition.Date.HasValue)
                {
                    DateTimeOffset now = clock.UtcNow;
                    TimeSpan delta = condition.Date.Value - now;
                    delay = delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
                    return true;
                }
            }

            return false;
        }

        public static TimeSpan? ParseOrNull(
            string? headerValue,
            IUtcClock clock) =>
            TryParse(headerValue, clock, out TimeSpan delay) ? delay : null;

        public static TimeSpan? ParseOrNull(
            string? headerValue,
            DateTimeOffset now) =>
            TryParse(headerValue, new FixedUtcClock(now), out TimeSpan delay) ? delay : null;

        public static TimeSpan? ParseOrNull(
            HttpResponseHeaders? headers,
            IUtcClock clock)
        {
            if (headers is null)
                return null;

            if (headers.RetryAfter is { } retryAfter)
            {
                if (retryAfter.Delta.HasValue)
                {
                    TimeSpan delta = retryAfter.Delta.Value;
                    return delta >= TimeSpan.Zero ? delta : TimeSpan.Zero;
                }

                if (retryAfter.Date.HasValue)
                {
                    DateTimeOffset now = clock.UtcNow;
                    TimeSpan delta = retryAfter.Date.Value - now;
                    return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
                }
            }

            if (headers.TryGetValues("Retry-After", out IEnumerable<string>? values))
            {
                string? first = values?.FirstOrDefault();
                return ParseOrNull(first, clock);
            }

            return null;
        }

        public static TimeSpan? ParseOrNull(
            HttpResponseMessage? response,
            IUtcClock clock) =>
            response is null ? null : ParseOrNull(response.Headers, clock);

        private sealed class FixedUtcClock : IUtcClock
        {
            public FixedUtcClock(DateTimeOffset utcNow) => UtcNow = utcNow;
            public DateTimeOffset UtcNow { get; }
        }
    }
}
