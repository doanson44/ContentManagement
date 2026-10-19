using ContentManagement.Server.Controllers;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class PublicHtmlSlugTests
{
    [Theory]
    [InlineData("Demo-Page", "demo-page")]
    [InlineData(" portfolio2026 ", "portfolio2026")]
    [InlineData("API-REFERENCE-v2", "api-reference-v2")]
    public void TryNormalize_normalizes_valid_slugs(string input, string expected)
    {
        Assert.True(PublicHtmlSlug.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-demo")]
    [InlineData("demo-")]
    [InlineData("demo--page")]
    [InlineData("demo page")]
    [InlineData("đường-dẫn")]
    [InlineData("a/b")]
    public void TryNormalize_rejects_invalid_slugs(string input) =>
        Assert.False(PublicHtmlSlug.TryNormalize(input, out _));

    [Fact]
    public void TryNormalize_rejects_slugs_over_80_characters() =>
        Assert.False(PublicHtmlSlug.TryNormalize(new string('a', 81), out _));

    [Theory]
    [InlineData("My File.html", "my-file")]
    [InlineData("api_reference.v2.htm", "api-reference-v2")]
    [InlineData("日本語.html", "")]
    public void FromFileName_creates_ascii_slug(string input, string expected) =>
        Assert.Equal(expected, PublicHtmlSlug.FromFileName(input));
}
