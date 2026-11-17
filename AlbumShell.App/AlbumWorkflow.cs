using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace AlbumShell.App;

internal static class AlbumWorkflow
{
    private const string ClientId = "97175158-c358-4d1a-a03d-9214f3a19a16";
    private static readonly string[] Scopes = ["Files.ReadWrite"];
    private static readonly HttpClient Http = new();

    public static bool IsValidAlbumName(string name, out string message)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 120 || name.Any(invalid.Contains))
        {
            message = "Enter a valid album name (up to 120 characters, without Windows-invalid filename characters).";
            return false;
        }
        message = string.Empty;
        return true;
    }

    public static async Task<AlbumWorkflowResult> AddFilesAsync(IReadOnlyList<string> paths, string albumName, IWin32Window owner, Action<string> status)
    {
        var token = await GetAccessTokenAsync(ClientId, owner);
        var api = new GraphApi(token);
        await api.EnsurePersonalDriveAsync();
        var syncRoots = OneDriveSyncRoot.Discover();
        if (syncRoots.Count == 0)
            throw new InvalidOperationException("No local OneDrive sync folder was found. Sign in to the OneDrive desktop app and make sure its folder is available.");
        var primaryRoot = syncRoots[0];

        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedItems = new List<JsonElement>();
        var uploadedFiles = new List<string>();
        var existingFiles = new List<string>();

        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            var name = Path.GetFileName(path);
            status($"Resolving file {index + 1} of {paths.Count} in OneDrive: {name}...");
            if (!File.Exists(path))
                throw new FileNotFoundException("A selected file no longer exists.", path);

            var syncRoot = OneDriveSyncRoot.FindContainingRoot(path, syncRoots);
            string relativePath;
            var wasCopiedToSyncFolder = false;
            if (syncRoot is not null)
            {
                relativePath = Path.GetRelativePath(syncRoot, path);
            }
            else
            {
                status($"Copying {index + 1} of {paths.Count} into OneDrive Pictures/{albumName}: {name}");
                var (destinationPath, wasCopied) = await EnsureInAlbumFolderAsync(path, primaryRoot, albumName);
                relativePath = Path.GetRelativePath(primaryRoot, destinationPath);
                wasCopiedToSyncFolder = wasCopied;
            }

            var item = await api.WaitForItemByPathAsync(relativePath, status);
            if (!item.TryGetProperty("photo", out _))
                throw new InvalidOperationException($"OneDrive did not identify '{name}' as a photo, so it cannot be added to a photo album.");

            if (wasCopiedToSyncFolder)
                uploadedFiles.Add(Path.GetFileName(relativePath));
            else
                existingFiles.Add(name);

            var itemId = GetId(item);
            if (processed.Add(itemId))
                selectedItems.Add(item);
        }

        status("Finding or creating the OneDrive photo album...");
        var albumId = await api.GetOrCreateAlbumAsync(albumName);
        var addedToAlbum = 0;
        var albumItems = await api.GetChildrenAsync($"bundles/{Uri.EscapeDataString(albumId)}");
        var albumItemIds = albumItems.Select(GetId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        status($"Added {addedToAlbum} of {selectedItems.Count} selected photos to the album...");
        foreach (var item in selectedItems)
        {
            var itemId = GetId(item);
            if (albumItemIds.Add(itemId))
            {
                await api.AddToAlbumAsync(albumId, itemId);
                addedToAlbum++;
            }
            status($"Added {addedToAlbum} of {selectedItems.Count} selected photos to the album...");
        }

        return new AlbumWorkflowResult(uploadedFiles, existingFiles, addedToAlbum);
    }

    private static async Task<(string DestinationPath, bool WasCopied)> EnsureInAlbumFolderAsync(string sourcePath, string syncRoot, string albumName)
    {
        var albumDirectory = Path.Combine(syncRoot, "Pictures", albumName);
        Directory.CreateDirectory(albumDirectory);

        var name = Path.GetFileName(sourcePath);
        var extension = Path.GetExtension(name);
        var baseName = Path.GetFileNameWithoutExtension(name);
        for (var suffix = 1; ; suffix++)
        {
            var candidateName = suffix == 1 ? name : $"{baseName} ({suffix}){extension}";
            var destinationPath = Path.Combine(albumDirectory, candidateName);
            if (File.Exists(destinationPath))
            {
                if (await AreFilesEqualAsync(sourcePath, destinationPath))
                    return (destinationPath, false);
                continue;
            }

            File.Copy(sourcePath, destinationPath);
            return (destinationPath, true);
        }
    }

    private static async Task<bool> AreFilesEqualAsync(string firstPath, string secondPath)
    {
        if (new FileInfo(firstPath).Length != new FileInfo(secondPath).Length)
            return false;

        using var first = File.OpenRead(firstPath);
        using var second = File.OpenRead(secondPath);
        var firstBuffer = new byte[81920];
        var secondBuffer = new byte[81920];
        while (true)
        {
            var firstRead = await ReadBlockAsync(first, firstBuffer);
            var secondRead = await ReadBlockAsync(second, secondBuffer);
            if (firstRead != secondRead || !firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead)))
                return false;
            if (firstRead == 0)
                return true;
        }
    }

    private static async Task<int> ReadBlockAsync(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private static async Task<string> GetAccessTokenAsync(string clientId, IWin32Window owner)
    {
        var cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlbumShellExtension", "TokenCache");
        Directory.CreateDirectory(cacheDirectory);
        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority("https://login.microsoftonline.com/common")
            .WithDefaultRedirectUri()
            .Build();
        var cacheProperties = new StorageCreationPropertiesBuilder("albumshell.msalcache", cacheDirectory).Build();
        var cache = await MsalCacheHelper.CreateAsync(cacheProperties);
        cache.RegisterCache(app.UserTokenCache);
        var accounts = await app.GetAccountsAsync();
        try
        {
            return (await app.AcquireTokenSilent(Scopes, accounts.FirstOrDefault()).ExecuteAsync()).AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            return (await app.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false)
                .WithParentActivityOrWindow(owner.Handle)
                .ExecuteAsync()).AccessToken;
        }
    }

    private static string GetId(JsonElement item) => item.GetProperty("id").GetString() ?? throw new InvalidOperationException("OneDrive returned an item without an ID.");
    private static string GetName(JsonElement item) => item.GetProperty("name").GetString() ?? string.Empty;

    internal sealed record AlbumWorkflowResult(IReadOnlyList<string> UploadedFiles, IReadOnlyList<string> ExistingFiles, int AddedToAlbumCount);

    private sealed class GraphApi(string accessToken)
    {
        private const string BaseUrl = "https://graph.microsoft.com/v1.0/me/drive/";

        public async Task<List<JsonElement>> GetChildrenAsync(string relativePath)
        {
            var items = new List<JsonElement>();
            string? next = BaseUrl + relativePath + "?$select=id,name,size,file,folder,photo";
            while (next is not null)
            {
                using var response = await SendAbsoluteAsync(HttpMethod.Get, next);
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                if (document.RootElement.TryGetProperty("value", out var values))
                    items.AddRange(values.EnumerateArray().Select(value => value.Clone()));
                next = document.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            }
            return items;
        }

        public async Task EnsurePersonalDriveAsync()
        {
            using var response = await SendAsync(HttpMethod.Get, "?$select=driveType");
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var driveType = document.RootElement.TryGetProperty("driveType", out var value) ? value.GetString() : null;
            if (!string.Equals(driveType, "personal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Microsoft Graph photo albums are supported only in personal OneDrive. This sign-in is using a work or school OneDrive. Sign in with the personal Microsoft account linked to your OneDrive instead.");
        }

        public async Task<JsonElement> WaitForItemByPathAsync(string relativePath, Action<string> status)
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    var encodedPath = string.Join('/', relativePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
                    using var response = await SendAsync(HttpMethod.Get, $"root:/{encodedPath}?$select=id,name,size,file,folder,photo");
                    using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                    return document.RootElement.Clone();
                }
                catch (GraphException exception) when (exception.StatusCode == HttpStatusCode.NotFound && attempt < 59)
                {
                    status($"Waiting for OneDrive to sync {Path.GetFileName(relativePath)}...");
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            throw new InvalidOperationException($"OneDrive did not sync '{relativePath}' within two minutes. The file remains in the local OneDrive folder; wait for sync and try again.");
        }

        public async Task<string> GetOrCreateAlbumAsync(string name)
        {
            string? next = BaseUrl + "bundles";
            while (next is not null)
            {
                using var response = await SendAbsoluteAsync(HttpMethod.Get, next);
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                foreach (var item in document.RootElement.GetProperty("value").EnumerateArray())
                {
                    if (GetName(item).Equals(name, StringComparison.OrdinalIgnoreCase) &&
                        item.TryGetProperty("bundle", out var bundle) && bundle.TryGetProperty("album", out _))
                        return GetId(item);
                }
                next = document.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            }

            using var created = await SendJsonAsync(HttpMethod.Post, "bundles", new Dictionary<string, object>
            {
                ["name"] = name,
                ["bundle"] = new { album = new { } }
            });
            using var result = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync());
            return GetId(result.RootElement);
        }

        public async Task AddToAlbumAsync(string bundleId, string itemId)
        {
            using var response = await SendJsonAsync(HttpMethod.Post,
                $"bundles/{Uri.EscapeDataString(bundleId)}/children", new { id = itemId });
        }

        private Task<HttpResponseMessage> SendAsync(HttpMethod method, string route) => SendAbsoluteAsync(method, BaseUrl + route);
        private Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string route, object body) =>
            SendAbsoluteAsync(method, BaseUrl + route, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        private async Task<HttpResponseMessage> SendAbsoluteAsync(HttpMethod method, string url, HttpContent? content = null,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return await SendAndValidateAsync(request, completionOption);
        }
        private static async Task<HttpResponseMessage> SendAndValidateAsync(HttpRequestMessage request,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
        {
            var response = await Http.SendAsync(request, completionOption);
            if (response.IsSuccessStatusCode)
                return response;
            var body = await response.Content.ReadAsStringAsync();
            var message = body.Length > 1000 ? body[..1000] : body;
            var statusCode = response.StatusCode;
            response.Dispose();
            throw new GraphException(statusCode, $"OneDrive request failed ({(int)statusCode}): {message}");
        }
    }

    private sealed class GraphException(HttpStatusCode statusCode, string message) : Exception(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}
