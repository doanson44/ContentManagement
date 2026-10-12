using System.ComponentModel.DataAnnotations;
using ContentManagement.Server.Domain;

namespace ContentManagement.UnitTests;

public sealed class QuickNoteTests
{
    [Fact]
    public void Validate_RejectsMissingTitle()
    {
        var note = new QuickNote { Title = string.Empty, Content = string.Empty };

        var errors = Validate(note);

        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(QuickNote.Title)));
    }

    [Fact]
    public void Validate_RejectsContentLongerThanTwentyThousandCharacters()
    {
        var note = new QuickNote
        {
            Title = "Reminder",
            Content = new string('x', 20_001)
        };

        var errors = Validate(note);

        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(QuickNote.Content)));
    }

    [Fact]
    public void Validate_AllowsAnEmptyDraft()
    {
        var note = new QuickNote { Title = "Untitled note", Content = string.Empty };

        var errors = Validate(note);

        Assert.Empty(errors);
    }

    private static List<ValidationResult> Validate(QuickNote note)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(note, new ValidationContext(note), results, validateAllProperties: true);
        return results;
    }
}
