using System.Net;
using System.Net.Http.Headers;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

public sealed class HttpRetryAfterParserTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
    private readonly FixedUtcClock _clock = new(FixedNow);

    [Theory]
    [InlineData("120", 120)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("3600", 3600)]
    [InlineData("  42  ", 42)]
    [InlineData("\t15\r\n", 15)]
    public void TryParse_ValidIntegerSeconds_ReturnsExpectedDelay(string input, int expectedSeconds)
    {
        bool success = HttpRetryAfterParser.TryParse(input, _clock, out TimeSpan delay);

        Assert.True(success);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-120")]
    [InlineData("not-a-number")]
    [InlineData("120s")]
    [InlineData("12.5")]
    public void TryParse_InvalidOrNegativeInteger_ReturnsFalse(string input)
    {
        bool success = HttpRetryAfterParser.TryParse(input, _clock, out TimeSpan delay);

        Assert.False(success);
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void TryParse_NullOrWhitespace_ReturnsFalse(string? input)
    {
        bool success = HttpRetryAfterParser.TryParse(input, _clock, out TimeSpan delay);

        Assert.False(success);
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void TryParse_Rfc1123FutureDate_ReturnsDeltaFromClock()
    {
        // 30 seconds into the future from FixedNow (2026-09-17 10:00:00 GMT)
        string futureDate = "Thu, 17 Sep 2026 10:00:30 GMT";

        bool success = HttpRetryAfterParser.TryParse(futureDate, _clock, out TimeSpan delay);

        Assert.True(success);
        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void TryParse_Rfc850FutureDate_ReturnsDeltaFromClock()
    {
        string futureDate = "Thursday, 17-Sep-26 10:01:00 GMT";

        bool success = HttpRetryAfterParser.TryParse(futureDate, _clock, out TimeSpan delay);

        Assert.True(success);
        Assert.Equal(TimeSpan.FromMinutes(1), delay);
    }

    [Fact]
    public void TryParse_AsctimeFutureDate_ReturnsDeltaFromClock()
    {
        string futureDate = "Thu Sep 17 10:02:00 2026";

        bool success = HttpRetryAfterParser.TryParse(futureDate, _clock, out TimeSpan delay);

        Assert.True(success);
        Assert.Equal(TimeSpan.FromMinutes(2), delay);
    }

    [Fact]
    public void TryParse_PastDate_ReturnsZeroDelay()
    {
        // 1 hour in the past
        string pastDate = "Thu, 17 Sep 2026 09:00:00 GMT";

        bool success = HttpRetryAfterParser.TryParse(pastDate, _clock, out TimeSpan delay);

        Assert.True(success);
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void ParseOrNull_ValidSeconds_ReturnsTimeSpan()
    {
        TimeSpan? delay = HttpRetryAfterParser.ParseOrNull("60", _clock);

        Assert.NotNull(delay);
        Assert.Equal(TimeSpan.FromSeconds(60), delay.Value);
    }

    [Fact]
    public void ParseOrNull_InvalidString_ReturnsNull()
    {
        TimeSpan? delay = HttpRetryAfterParser.ParseOrNull("garbage", _clock);

        Assert.Null(delay);
    }

    [Fact]
    public void ParseOrNull_WithDateTimeOffset_ParsesRelativeCorrectly()
    {
        DateTimeOffset now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        string futureDate = "Thu, 17 Sep 2026 12:00:45 GMT";

        TimeSpan? delay = HttpRetryAfterParser.ParseOrNull(futureDate, now);

        Assert.NotNull(delay);
        Assert.Equal(TimeSpan.FromSeconds(45), delay.Value);
    }

    [Fact]
    public void ParseOrNull_HttpResponseMessage_WithRetryAfterDelta_ReturnsDelta()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(15));

        TimeSpan? delay = HttpRetryAfterParser.ParseOrNull(response, _clock);

        Assert.NotNull(delay);
        Assert.Equal(TimeSpan.FromSeconds(15), delay.Value);
    }

    [Fact]
    public void ParseOrNull_HttpResponseMessage_WithRetryAfterDate_ReturnsDelta()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(FixedNow.AddSeconds(20));

        TimeSpan? delay = HttpRetryAfterParser.ParseOrNull(response, _clock);

        Assert.NotNull(delay);
        Assert.Equal(TimeSpan.FromSeconds(20), delay.Value);
    }

    [Fact]
    public void ParseOrNull_HttpResponseMessage_WithoutRetryAfter_ReturnsNull()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        TimeSpan? delay = HttpRetryAfterParser.ParseOrNull(response, _clock);

        Assert.Null(delay);
    }

    [Fact]
    public void ParseOrNull_NullHttpResponseMessage_ReturnsNull()
    {
        TimeSpan? delay = HttpRetryAfterParser.ParseOrNull((HttpResponseMessage?)null, _clock);

        Assert.Null(delay);
    }

    [Fact]
    public void ParseOrNull_NullHeaders_ReturnsNull()
    {
        TimeSpan? delay = HttpRetryAfterParser.ParseOrNull((HttpResponseHeaders?)null, _clock);

        Assert.Null(delay);
    }
}
