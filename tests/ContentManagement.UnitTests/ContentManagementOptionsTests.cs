using System.ComponentModel.DataAnnotations;
using ContentManagement.Server.Configuration;
namespace ContentManagement.UnitTests;
public sealed class ContentManagementOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var options = new ContentManagementOptions();
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), results, true);
        Assert.True(isValid, string.Join("; ", results.Select(result => result.ErrorMessage)));
        Assert.True(Path.IsPathRooted(options.StorageRoot));
        Assert.True(options.MaxUploadBytes > 0);
    }
    [Fact]
    public void Non_positive_upload_limit_is_invalid()
    {
        var options = new ContentManagementOptions { MaxUploadBytes = 0 };
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), results, true);
        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ContentManagementOptions.MaxUploadBytes)));
    }
}
