namespace VeilDesk.Core.Settings;

/// <summary>
/// Resolves the user supplied export folder. The value comes from a text box
/// and therefore may contain anything a person or a stray paste can produce:
/// a URL, a relative path, or a path with characters Windows rejects. Every
/// rejection is explicit so the UI can explain the problem instead of the
/// process dying inside <see cref="Directory.CreateDirectory(string)"/>.
/// </summary>
public static class ExportDirectory
{
    public const int MaxLength = 240;

    /// <summary>
    /// Validates a raw export folder string. Returns <c>false</c> and a
    /// human readable reason when the value cannot be used.
    /// </summary>
    public static bool TryResolve(string? raw, out string resolved, out string? error)
    {
        resolved = string.Empty;
        error = null;

        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "Enter a folder path first.";
            return false;
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            error = "A web address is not a folder. Use a local path such as C:\\Exports.";
            return false;
        }

        if (text.Length > MaxLength)
        {
            error = "That path is too long.";
            return false;
        }

        string expanded;
        try
        {
            expanded = Environment.ExpandEnvironmentVariables(text);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (!Path.IsPathFullyQualified(expanded))
        {
            error = "Use an absolute path, for example C:\\Exports.";
            return false;
        }

        try
        {
            var full = Path.GetFullPath(expanded);
            if (full.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                error = "That path contains characters Windows does not allow.";
                return false;
            }

            resolved = full;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Resolves a raw export folder, falling back to the given default when
    /// the raw value is empty. Invalid values never fall back silently:
    /// the reason is reported through <paramref name="error"/>.
    /// </summary>
    public static bool TryResolveOrDefault(string? raw, string fallback, out string resolved, out string? error)
    {
        if ((raw ?? string.Empty).Trim().Length == 0)
        {
            resolved = fallback;
            error = null;
            return true;
        }

        return TryResolve(raw, out resolved, out error);
    }
}
