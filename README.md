# Album Shell Extension

This .NET 8 Windows Forms app adds selected image files to a OneDrive photo album. It detects the local OneDrive sync root. Files already under that root are resolved in OneDrive by their existing relative path. Files outside it are copied into the local `OneDrive/Pictures/<album name>` folder, then the app waits for OneDrive sync and adds the synced file to the album. Existing destination files are reused only when their contents match; name conflicts get a numeric suffix instead of being overwritten. The last submitted album name is remembered for the next launch, and Enter in the album-name field submits the operation.

## Configure OneDrive sign-in

1. The app is configured with its Microsoft Entra application (client) ID; no environment-variable setup is needed.
2. The corresponding app registration must support personal Microsoft accounts, include the `http://localhost` mobile/desktop redirect URI, and have delegated Microsoft Graph `Files.ReadWrite` permission. Photo album creation is supported only in personal OneDrive, not OneDrive for Business. Complete any consent required by the account.

MSAL stores its token cache under the current user's local application data and attempts silent token acquisition before opening the system browser for sign-in.

## Install the Explorer menu item

Build the solution, then run the built application once with `--install-context-menu`, for example:

```powershell
& ".\AlbumShell.App\bin\Debug\net8.0-windows\AlbumShell.App.exe" --install-context-menu
```

The registration is per-user and does not require administrator rights. It adds **Add to OneDrive album** to image-file context menus. On Windows 11, it might be under **Show more options**. Re-run the install command after updating or moving the app. The first use prompts for an album name and signs in if no cached session is available. To remove the menu item, run the same executable with `--uninstall-context-menu`.
