using Microsoft.Win32;

namespace AlbumShell.App;

internal static class ShellContextMenuRegistration
{
    private const string HandlerPath = @"Software\Classes\SystemFileAssociations\image\shellex\ContextMenuHandlers\AlbumShellAddToAlbum";
    private const string PreviousVerbPath = @"Software\Classes\SystemFileAssociations\image\shell\AlbumShellAddToAlbum";
    private const string CommandClassId = "B6B843E1-1D33-4E31-91A0-7F915A7E9572";

    public static void Install()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Could not determine the application executable path.");

        var commandDllPath = Path.Combine(AppContext.BaseDirectory, "AlbumShell.ShellExtension.Native.dll");
        if (!File.Exists(commandDllPath))
            throw new FileNotFoundException("The native Explorer context-menu handler is missing. Build or install the application before registering the context menu.", commandDllPath);

        var classPath = $@"Software\Classes\CLSID\{{{CommandClassId}}}";
        using (var commandClass = Registry.CurrentUser.CreateSubKey(classPath)
            ?? throw new InvalidOperationException("Could not create the Explorer context-menu handler registration."))
        {
            commandClass.SetValue("AppPath", executablePath, RegistryValueKind.String);
            using var inprocServer = commandClass.CreateSubKey("InprocServer32")
                ?? throw new InvalidOperationException("Could not create the Explorer context-menu handler COM server registration.");
            inprocServer.SetValue(string.Empty, commandDllPath, RegistryValueKind.String);
            inprocServer.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
        }

        using var handler = Registry.CurrentUser.CreateSubKey(HandlerPath)
            ?? throw new InvalidOperationException("Could not create the current-user Explorer context-menu registration.");
        handler.SetValue(string.Empty, $"{{{CommandClassId}}}", RegistryValueKind.String);
        Registry.CurrentUser.DeleteSubKeyTree(PreviousVerbPath, throwOnMissingSubKey: false);
    }

    public static void Uninstall()
    {
        Registry.CurrentUser.DeleteSubKeyTree(HandlerPath, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(PreviousVerbPath, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{{{CommandClassId}}}", throwOnMissingSubKey: false);
    }
}