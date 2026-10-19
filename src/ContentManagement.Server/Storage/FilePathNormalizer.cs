namespace ContentManagement.Server.Storage;

/// <summary>
/// Validates and canonicalizes public logical file paths. These paths are identifiers, not filesystem paths.
/// </summary>
public static class FilePathNormalizer
{
    private const int MaxLength = 450;
    private const string ForbiddenCharacters = "<>:\"|?*#%";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim().Replace('\\', '/');
        if (candidate.Length is 0 or > MaxLength ||
            candidate.StartsWith('/') ||
            candidate.EndsWith('/') ||
            candidate.Any(char.IsControl))
            return false;

        var segments = candidate.Split('/');
        if (segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                !string.Equals(segment, segment.Trim(), StringComparison.Ordinal) ||
                segment.Any(character => ForbiddenCharacters.Contains(character))))
            return false;

        normalized = string.Join('/', segments);
        return normalized.Length <= MaxLength;
    }
}
