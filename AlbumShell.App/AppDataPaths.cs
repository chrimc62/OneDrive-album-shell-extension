namespace AlbumShell.App;

internal static class AppDataPaths
{
    private static readonly string RootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlbumShellExtension");

    public static string RequestDirectory => Path.Combine(RootDirectory, "Requests");
    public static string LastAlbumName => Path.Combine(RootDirectory, "last-album-name.txt");
    public static string TokenCacheDirectory => Path.Combine(RootDirectory, "TokenCache");
}
