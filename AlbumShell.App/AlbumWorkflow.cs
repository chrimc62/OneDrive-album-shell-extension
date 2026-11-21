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
    private const int MaxConcurrentFileResolutions = 4;
    private const string SelectedItemFields = "?$select=id,name,size,file,folder,photo";
    private static readonly string[] Scopes = ["Files.ReadWrite"];
    private static readonly HttpClient Http = new();

    public static bool IsValidAlbumName(string name, out string message)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 120 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            message = "Enter a valid album name (up to 120 characters, without Windows-invalid filename characters).";
            return false;
        }
        message = string.Empty;
        return true;
    }

    public static async Task<AlbumWorkflowResult> AddFilesAsync(IReadOnlyList<string> paths, string albumName, IWin32Window owner, Action<string> status)
    {
        var token = await GetAccessTokenAsync(owner);
        var api = new GraphApi(token);
        await api.EnsurePersonalDriveAsync();
        var syncRoots = OneDriveSyncRoot.Discover();
        if (syncRoots.Count == 0)
            throw new InvalidOperationException("No local OneDrive sync folder was found. Sign in to the OneDrive desktop app and make sure its folder is available.");
        var primaryRoot = syncRoots[0];

        var filesToResolve = new List<(string Name, string RelativePath, bool WasCopiedToSyncFolder)>();
        var uploadedFiles = new List<string>();
        var existingFiles = new List<string>();

        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            var name = Path.GetFileName(path);
            status($"Preparing file {index + 1} of {paths.Count}: {name}...");
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

            filesToResolve.Add((name, relativePath, wasCopiedToSyncFolder));
        }

        status($"Resolving {filesToResolve.Count} file(s) in OneDrive...");
        using var resolutionGate = new SemaphoreSlim(MaxConcurrentFileResolutions);
        var resolutionTasks = filesToResolve.Select(async (file, index) =>
        {
            await resolutionGate.WaitAsync();
            try
            {
                status($"Resolving file {index + 1} of {filesToResolve.Count} in OneDrive: {file.Name}...");
                var item = await api.WaitForItemByPathAsync(file.RelativePath, status);
                if (!item.TryGetProperty("photo", out _))
                    throw new InvalidOperationException($"OneDrive did not identify '{file.Name}' as a photo, so it cannot be added to a photo album.");

                return (File: file, Item: item);
            }
            finally
            {
                resolutionGate.Release();
            }
        }).ToArray();
        var resolvedFiles = await Task.WhenAll(resolutionTasks);

        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedItems = new List<JsonElement>();
        foreach (var resolved in resolvedFiles)
        {
            if (resolved.File.WasCopiedToSyncFolder)
                uploadedFiles.Add(resolved.File.Name);
            else
                existingFiles.Add(resolved.File.Name);

            var itemId = GetId(resolved.Item);
            if (processed.Add(itemId))
                selectedItems.Add(resolved.Item);
        }

        status("Finding or creating the OneDrive photo album...");
        var albumId = await api.GetOrCreateAlbumAsync(albumName, status);
        var addedToAlbum = 0;
        var albumItems = await api.GetChildrenAsync($"bundles/{Uri.EscapeDataString(albumId)}");
        var albumItemIds = albumItems.Select(GetId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        status($"Added {addedToAlbum} of {selectedItems.Count} selected photos to the album...");
        foreach (var item in selectedItems)
        {
            var itemId = GetId(item);
            if (albumItemIds.Add(itemId))
            {
                if (await api.AddToAlbumAsync(albumId, itemId))
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

    private static async Task<string> GetAccessTokenAsync(IWin32Window owner)
    {
        var cacheDirectory = AppDataPaths.TokenCacheDirectory;
        Directory.CreateDirectory(cacheDirectory);
        var app = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority("https://login.microsoftonline.com/common")
            .WithDefaultRedirectUri()
            .Build();
        var cacheProperties = new StorageCreationPropertiesBuilder("albumshell.msalcache", cacheDirectory).Build();
        var cache = await MsalCacheHelper.CreateAsync(cacheProperties);
        cache.RegisterCache(app.UserTokenCache);
        var accounts = await app.GetAccountsAsync();
        try
        {
            var token = await app.AcquireTokenSilent(Scopes, accounts.FirstOrDefault()).ExecuteAsync();
            return token.AccessToken;
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
            await foreach (var item in GetItemsAsync(BaseUrl + relativePath + SelectedItemFields))
                items.Add(item);
            return items;
        }

        public async Task EnsurePersonalDriveAsync()
        {
            using var response = await SendAsync(HttpMethod.Get, "?$select=driveType");
            using var document = await ReadJsonAsync(response);
            var driveType = document.RootElement.TryGetProperty("driveType", out var value) ? value.GetString() : null;
            if (!string.Equals(driveType, "personal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Microsoft Graph photo albums are supported only in personal OneDrive. This sign-in is using a work or school OneDrive. Sign in with the personal Microsoft account linked to your OneDrive instead.");
        }

        public async Task<JsonElement> WaitForItemByPathAsync(string relativePath, Action<string> status)
        {
            var timeoutAt = DateTimeOffset.UtcNow.AddMinutes(2);
            while (DateTimeOffset.UtcNow < timeoutAt)
            {
                try
                {
                    var encodedPath = string.Join('/', relativePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
                    using var response = await SendAsync(HttpMethod.Get, $"root:/{encodedPath}{SelectedItemFields}");
                    using var document = await ReadJsonAsync(response);
                    return document.RootElement.Clone();
                }
                catch (GraphException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    status($"Waiting for OneDrive to sync {Path.GetFileName(relativePath)}...");
                    await DelayBeforeRetryAsync(TimeSpan.FromSeconds(2));
                }
                catch (GraphException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    status($"OneDrive is busy; retrying lookup for {Path.GetFileName(relativePath)}...");
                    var retryDelay = exception.RetryAfter is { } delay && delay > TimeSpan.Zero
                        ? delay
                        : TimeSpan.FromSeconds(2);
                    await DelayBeforeRetryAsync(retryDelay);
                }
            }

            throw new InvalidOperationException($"OneDrive did not sync '{relativePath}' within two minutes. The file remains in the local OneDrive folder; wait for sync and try again.");

            async Task DelayBeforeRetryAsync(TimeSpan delay)
            {
                var remaining = timeoutAt - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(delay < remaining ? delay : remaining);
            }
        }

        public async Task<string> GetOrCreateAlbumAsync(string name, Action<string> status)
        {
            string? existingAlbumId = null;
            await foreach (var item in GetItemsAsync(BaseUrl + "bundles"))
            {
                if (GetName(item).Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    item.TryGetProperty("bundle", out var bundle) && bundle.TryGetProperty("album", out _))
                {
                    existingAlbumId = GetId(item);
                    break;
                }
            }

            if (existingAlbumId is not null)
                return existingAlbumId;

            status($"Creating album '{name}'...");
            using var created = await SendJsonAsync(HttpMethod.Post, "bundles", new Dictionary<string, object>
            {
                ["name"] = name,
                ["bundle"] = new { album = new { } }
            });
            using var result = await ReadJsonAsync(created);
            return GetId(result.RootElement);
        }

        public async Task<bool> AddToAlbumAsync(string bundleId, string itemId)
        {
            try
            {
                using var response = await SendJsonAsync(HttpMethod.Post,
                    $"bundles/{Uri.EscapeDataString(bundleId)}/children", new { id = itemId });
                return true;
            }
            catch (GraphException exception) when (
                exception.StatusCode == HttpStatusCode.Conflict ||
                exception.StatusCode == HttpStatusCode.BadRequest &&
                string.Equals(exception.ErrorCode, "invalidRequest", StringComparison.OrdinalIgnoreCase) &&
                exception.ErrorMessage?.Contains("An Album item with same name already exists", StringComparison.OrdinalIgnoreCase) == true)
            {
                return false;
            }
        }

        private Task<HttpResponseMessage> SendAsync(HttpMethod method, string route) => SendAbsoluteAsync(method, BaseUrl + route);
        private Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string route, object body) =>
            SendAbsoluteAsync(method, BaseUrl + route, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        private async Task<HttpResponseMessage> SendAbsoluteAsync(HttpMethod method, string url, HttpContent? content = null)
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return await SendAndValidateAsync(request);
        }
        private static async Task<HttpResponseMessage> SendAndValidateAsync(HttpRequestMessage request)
        {
            var response = await Http.SendAsync(request);
            if (response.IsSuccessStatusCode)
                return response;
            var body = await response.Content.ReadAsStringAsync();
            var message = body.Length > 1000 ? body[..1000] : body;
            var statusCode = response.StatusCode;
            string? errorCode = null;
            string? errorMessage = null;
            try
            {
                using var errorDocument = JsonDocument.Parse(body);
                if (errorDocument.RootElement.TryGetProperty("error", out var error))
                {
                    errorCode = error.TryGetProperty("code", out var code) ? code.GetString() : null;
                    errorMessage = error.TryGetProperty("message", out var errorText) ? errorText.GetString() : null;
                }
            }
            catch (JsonException)
            {
            }
            var retryAfterHeader = response.Headers.RetryAfter;
            var retryAfter = retryAfterHeader?.Delta ??
                (retryAfterHeader?.Date is { } retryDate ? retryDate - DateTimeOffset.UtcNow : null);
            if (retryAfter is { } retryDelay && retryDelay < TimeSpan.Zero)
                retryAfter = TimeSpan.Zero;
            response.Dispose();
            throw new GraphException(statusCode, retryAfter, errorCode, errorMessage,
                $"OneDrive request failed ({(int)statusCode}): {message}");
        }

        private async IAsyncEnumerable<JsonElement> GetItemsAsync(string url)
        {
            string? next = url;
            while (next is not null)
            {
                using var response = await SendAbsoluteAsync(HttpMethod.Get, next);
                using var document = await ReadJsonAsync(response);
                if (document.RootElement.TryGetProperty("value", out var values))
                {
                    foreach (var item in values.EnumerateArray())
                        yield return item.Clone();
                }
                next = document.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            }
        }

        private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
            await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    private sealed class GraphException(HttpStatusCode statusCode, TimeSpan? retryAfter, string? errorCode,
        string? errorMessage, string message) : Exception(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
        public TimeSpan? RetryAfter { get; } = retryAfter;
        public string? ErrorCode { get; } = errorCode;
        public string? ErrorMessage { get; } = errorMessage;
    }
}
