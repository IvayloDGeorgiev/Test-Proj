using Microsoft.Extensions.Options;

namespace Test_Proj.Options;

public sealed class ExportOptionsValidator : IValidateOptions<ExportOptions>
{
    public ValidateOptionsResult Validate(string? name, ExportOptions options)
    {
        if (!IsSafeRoot(options.OutputRoot))
            return ValidateOptionsResult.Fail("Export.OutputRoot must be an absolute local directory without links or traversal.");
        if (options.MaximumRequestBodyBytes is < 1 or > 4096 ||
            options.MaximumConcurrentOperations != 1 || options.MaximumQueuedOperations != 0)
            return ValidateOptionsResult.Fail("Export limits must allow one active operation, no queue and at most 4096 request bytes.");
        return ValidateOptionsResult.Success;
    }

    public static bool IsSafeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Any(char.IsControl)) return false;
        try
        {
            var segments = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            if (segments.Any(p => p is "." or "..")) return false;
            var fullPath = Path.GetFullPath(path);
            if (fullPath == Path.GetPathRoot(fullPath)) return false;
            foreach (var segment in segments.Skip(1))
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    segment.EndsWith(' ') || segment.EndsWith('.') || IsWindowsDevice(segment)) return false;

            if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(fullPath)!).DriveType != DriveType.Fixed)
                return false;

            for (var current = new DirectoryInfo(fullPath); current != null; current = current.Parent)
            {
                if (File.Exists(current.FullName)) return false;
                if (current.LinkTarget != null ||
                    (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsWindowsDevice(string segment)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                "123456789¹²³".Contains(stem[3]));
    }
}
