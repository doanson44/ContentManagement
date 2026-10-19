using ContentManagement.Server.Storage;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class FilePathNormalizerTests
{
    [Theory]
    [InlineData("products/product-001/image-01.jpg", "products/product-001/image-01.jpg")]
    [InlineData("products\\product-001\\image-01.jpg", "products/product-001/image-01.jpg")]
    [InlineData("  product-001/image.jpg  ", "product-001/image.jpg")]
    public void TryNormalize_accepts_relative_logical_paths(string input, string expected)
    {
        Assert.True(FilePathNormalizer.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("products/../secret.txt")]
    [InlineData("/absolute/path.txt")]
    [InlineData("products//file.txt")]
    [InlineData("products/:stream")]
    [InlineData("products/file?.txt")]
    [InlineData("products/%2e%2e/secret.txt")]
    [InlineData("products/ bad.txt")]
    [InlineData("")]
    public void TryNormalize_rejects_unsafe_or_ambiguous_paths(string input)
    {
        Assert.False(FilePathNormalizer.TryNormalize(input, out _));
    }

    [Fact]
    public void TryNormalize_rejects_paths_longer_than_database_index_limit()
    {
        Assert.False(FilePathNormalizer.TryNormalize(new string('a', 451), out _));
    }
}
