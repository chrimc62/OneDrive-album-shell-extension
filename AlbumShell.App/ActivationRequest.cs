namespace AlbumShell.App;

internal static class ActivationRequest
{
    private static readonly string RequestDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlbumShellExtension", "Requests");

    public static IReadOnlyList<string> ReadFiles(IEnumerable<string> arguments)
    {
        var files = new List<string>();
        foreach (var argument in arguments)
        {
            if (Uri.TryCreate(argument, UriKind.Absolute, out var uri) && uri.Scheme.Equals("albumshell", StringComparison.OrdinalIgnoreCase))
            {
                if (Guid.TryParse(uri.AbsolutePath.Trim('/'), out var id))
                {
                    var requestPath = Path.Combine(RequestDirectory, $"{id:D}.txt");
                    if (File.Exists(requestPath))
                    {
                        files.AddRange(File.ReadAllLines(requestPath).Where(File.Exists));
                        File.Delete(requestPath);
                    }
                }
            }
            else if (File.Exists(argument))
            {
                files.Add(argument);
            }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
