namespace AlbumShell.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length == 1 && args[0].Equals("--install-context-menu", StringComparison.OrdinalIgnoreCase))
        {
            RunRegistrationAction(ShellContextMenuRegistration.Install, "The OneDrive album context-menu item is installed for the current Windows user.");
            return;
        }

        if (args.Length == 1 && args[0].Equals("--uninstall-context-menu", StringComparison.OrdinalIgnoreCase))
        {
            RunRegistrationAction(ShellContextMenuRegistration.Uninstall, "The OneDrive album context-menu item was removed for the current Windows user.");
            return;
        }

        Application.Run(new AlbumForm(ActivationRequest.ReadFiles(args)));
    }

    private static void RunRegistrationAction(Action action, string successMessage)
    {
        try
        {
            action();
            MessageBox.Show(successMessage, "OneDrive album", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "OneDrive album", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}