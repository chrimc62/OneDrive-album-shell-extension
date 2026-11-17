using Microsoft.Win32;

namespace AlbumShell.App;

internal static class OneDriveSyncRoot
{
    public static IReadOnlyList<string> Discover()
    {
        var roots = new List<string>();
        foreach (var variableName in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            AddRoot(roots, Environment.GetEnvironmentVariable(variableName));

        using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        if (accounts is not null)
        {
            foreach (var accountName in accounts.GetSubKeyNames())
            {
                using var account = accounts.OpenSubKey(accountName);
                AddRoot(roots, account?.GetValue("UserFolder") as string);
            }
        }

        return roots;
    }

    public static string? FindContainingRoot(string filePath, IReadOnlyList<string> roots)
    {
        var fullFilePath = Path.GetFullPath(filePath);
        foreach (var root in roots)
        {
            var relativePath = Path.GetRelativePath(root, fullFilePath);
            if (!Path.IsPathRooted(relativePath) &&
                !relativePath.Equals("..", StringComparison.Ordinal) &&
                !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
                return root;
        }

        return null;
    }

    private static void AddRoot(List<string> roots, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        var fullPath = Path.GetFullPath(candidate);
        if (Directory.Exists(fullPath) && !roots.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            roots.Add(fullPath);
    }
}