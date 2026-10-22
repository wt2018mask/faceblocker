using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeilDesk.Core.Packs;

public sealed class PackAsset
{
    public string Path { get; set; } = "";
    public string Type { get; set; } = "image";
    public string? Name { get; set; }
}

public sealed class PackTextPreset
{
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#FFFFFF";
    public string FontFamily { get; set; } = "Segoe UI";
}

public sealed class PackTheme
{
    public string Background { get; set; } = "#0B0D12";
    public string Accent { get; set; } = "#7C5CFF";
    public string Surface { get; set; } = "#151923";
}

public sealed class PackManifest
{
    public string Format { get; set; } = PackFormat.Current;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string License { get; set; } = "";
    public List<PackAsset> Assets { get; set; } = new();
    public List<PackTextPreset> TextPresets { get; set; } = new();
    public PackTheme? Theme { get; set; }
    public Dictionary<string, JsonElement>? SettingsPreset { get; set; }
}

public static class PackFormat
{
    public const string Current = "veilpack/1";

    public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif",
        ".mp4", ".webm", ".json", ".txt",
        ".ttf", ".otf", ".woff", ".woff2"
    };

    public const long MaxUncompressedBytes = 200L * 1024 * 1024;
    public const int MaxEntries = 512;
}

public sealed record PackValidationResult(bool Success, string? Error, PackManifest? Manifest, string? ExtractedDirectory)
{
    public static PackValidationResult Fail(string error) => new(false, error, null, null);
}

/// <summary>
/// Imports user-authored packs as inert data. No entry is executed, scripts are
/// rejected, absolute/.. paths are rejected, expansion is bounded, and only an
/// extension allowlist is extracted.
/// </summary>
public static class PackValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static PackValidationResult ValidateAndExtract(string zipPath, string destinationDirectory)
    {
        if (!File.Exists(zipPath)) return PackValidationResult.Fail("Pack file does not exist.");
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > PackFormat.MaxEntries) return PackValidationResult.Fail("Pack has too many entries.");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                total += entry.Length;
                if (total > PackFormat.MaxUncompressedBytes) return PackValidationResult.Fail("Pack expands beyond the 200 MB limit.");
                if (!IsSafeRelativePath(entry.FullName)) return PackValidationResult.Fail($"Unsafe entry path: {entry.FullName}");
                if (!string.IsNullOrEmpty(entry.Name) &&
                    !PackFormat.AllowedExtensions.Contains(System.IO.Path.GetExtension(entry.Name)))
                    return PackValidationResult.Fail($"File type is not allowed in packs: {entry.Name}");
            }

            var manifestEntry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase));
            if (manifestEntry is null) return PackValidationResult.Fail("manifest.json is missing.");

            PackManifest? manifest;
            using (var stream = manifestEntry.Open())
                manifest = JsonSerializer.Deserialize<PackManifest>(stream, JsonOptions);

            if (manifest is null) return PackValidationResult.Fail("manifest.json is invalid.");
            if (manifest.Format != PackFormat.Current) return PackValidationResult.Fail($"Unsupported pack format '{manifest.Format}'. Expected {PackFormat.Current}.");
            if (!IsSafeId(manifest.Id)) return PackValidationResult.Fail("Pack id must be 1-64 characters of letters, digits, '-', '_' or '.'.");
            if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 80) return PackValidationResult.Fail("Pack name must be 1-80 characters.");
            if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 32) return PackValidationResult.Fail("Pack version must be 1-32 characters.");
            if (manifest.TextPresets.Count > 64) return PackValidationResult.Fail("Too many text presets.");
            foreach (var preset in manifest.TextPresets)
            {
                if (preset.Text.Length > 128) return PackValidationResult.Fail("Text preset is too long.");
                if (!Settings.SettingsValidator.IsHexColor(preset.Color)) return PackValidationResult.Fail("Text preset colour is invalid.");
            }
            if (manifest.Theme is not null &&
                (!Settings.SettingsValidator.IsHexColor(manifest.Theme.Background) ||
                 !Settings.SettingsValidator.IsHexColor(manifest.Theme.Accent) ||
                 !Settings.SettingsValidator.IsHexColor(manifest.Theme.Surface)))
                return PackValidationResult.Fail("Theme colours are invalid.");

            Directory.CreateDirectory(destinationDirectory);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(destinationDirectory, entry.FullName));
                var root = System.IO.Path.GetFullPath(destinationDirectory) + System.IO.Path.DirectorySeparatorChar;
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return PackValidationResult.Fail($"Entry escapes the pack directory: {entry.FullName}");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            return new PackValidationResult(true, null, manifest, destinationDirectory);
        }
        catch (InvalidDataException)
        {
            return PackValidationResult.Fail("The file is not a valid ZIP archive.");
        }
        catch (Exception ex)
        {
            return PackValidationResult.Fail($"Pack import failed: {ex.Message}");
        }
    }

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains(':', StringComparison.Ordinal)) return false;
        if (path.StartsWith('/') || path.StartsWith('\\')) return false;
        var parts = path.Replace('\\', '/').Split('/');
        return parts.All(p => p != ".." && p != "." && p.Length > 0);
    }

    public static bool IsSafeId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id.Length <= 64 &&
           id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
