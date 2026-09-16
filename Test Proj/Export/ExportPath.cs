using Test_Proj.Options;

namespace Test_Proj.Export;

public static class ExportPath
{
    public static string Resolve(string root, string filename)
    {
        if (!ExportOptionsValidator.IsSafeRoot(root) || string.IsNullOrWhiteSpace(filename) ||
            filename.IndexOfAny(['/', '\\', ':']) >= 0 || filename is "." or ".." ||
            filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ExportException("unsafe_export_path");
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(canonicalRoot, filename));
        if (!path.StartsWith(canonicalRoot + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ExportException("unsafe_export_path");
        var info = new FileInfo(path);
        if (info.LinkTarget != null || Directory.Exists(path) ||
            (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new ExportException("unsafe_export_path");
        return path;
    }
}
