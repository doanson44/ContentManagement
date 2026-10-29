using System.Net;
using System.Net.Http;
using ContentManagement.Server.Services;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class ProxyRetryPolicyTests
{
    [Theory]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("OPTIONS", true)]
    [InlineData("POST", false)]
    [InlineData("PUT", false)]
    [InlineData("DELETE", false)]
    public void IsIdempotent_OnlyAllowsSafeMethods(string method, bool expected) =>
        Assert.Equal(expected, ProxyRetryPolicy.IsIdempotent(method));

    [Fact]
    public void ShouldRetry_RetriesIdempotentCallsAfterATimeout() =>
        Assert.True(ProxyRetryPolicy.ShouldRetry("GET", new TaskCanceledException()));

    [Fact]
    public void ShouldRetry_DoesNotReplayAWriteAfterATimeout() =>
        Assert.False(ProxyRetryPolicy.ShouldRetry("POST", new TaskCanceledException()));

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public void ShouldRetry_RetriesFailuresThatHappenedBeforeDelivery(string method)
    {
        Assert.True(ProxyRetryPolicy.ShouldRetry(method,
            new HttpRequestException(HttpRequestError.ConnectionError, "connect failed")));
        Assert.True(ProxyRetryPolicy.ShouldRetry(method,
            new HttpRequestException(HttpRequestError.ProxyTunnelError, "tunnel rejected")));
    }

    [Fact]
    public void ShouldRetry_DoesNotReplayAWriteWhenTheResponseWasInterrupted() =>
        Assert.False(ProxyRetryPolicy.ShouldRetry("POST",
            new HttpRequestException(HttpRequestError.ResponseEnded, "response truncated")));

    [Fact]
    public void ShouldRetry_TreatsUnknownErrorsAsUnsafeForWrites()
    {
        Assert.False(ProxyRetryPolicy.ShouldRetry("POST", new InvalidOperationException("unexpected")));
        Assert.False(ProxyRetryPolicy.ShouldRetry("POST", new IOException("socket reset")));
        Assert.True(ProxyRetryPolicy.ShouldRetry("GET", new IOException("socket reset")));
    }
}
