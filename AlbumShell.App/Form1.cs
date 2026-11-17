namespace AlbumShell.App;

public partial class Form1 : Form
{
    private readonly TextBox _albumName = new() { Width = 360 };
    private readonly ListBox _files = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, Text = "Enter a OneDrive album name." };
    private readonly Button _upload = new() { Text = "Add to OneDrive album", AutoSize = true };

    public Form1(IReadOnlyList<string> filePaths)
    {
        Text = "Add to OneDrive album";
        MinimumSize = new Size(560, 360);
        StartPosition = FormStartPosition.CenterScreen;
        AcceptButton = _upload;
        _albumName.Text = LastAlbumNameStore.Load();

        var albumRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12) };
        albumRow.Controls.Add(new Label { Text = "Album name:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) });
        albumRow.Controls.Add(_albumName);

        var bottomRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12) };
        bottomRow.Controls.Add(_upload);
        bottomRow.Controls.Add(_status);

        Controls.Add(_files);
        Controls.Add(bottomRow);
        Controls.Add(albumRow);

        foreach (var filePath in filePaths)
        {
            _files.Items.Add(filePath);
        }

        _upload.Enabled = filePaths.Count > 0;
        _status.Text = filePaths.Count == 0
            ? "No selected image files were received. Reinstall the context-menu command and try again."
            : "Enter a OneDrive album name.";
        _upload.Click += UploadClicked;
    }

    private async void UploadClicked(object? sender, EventArgs e)
    {
        var albumName = _albumName.Text.Trim();
        if (!AlbumWorkflow.IsValidAlbumName(albumName, out var validationMessage))
        {
            MessageBox.Show(this, validationMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _upload.Enabled = false;
        var albumNameRemembered = LastAlbumNameStore.TrySave(albumName);
        _status.Text = albumNameRemembered ? "Connecting to OneDrive..." : "Connecting to OneDrive; the album name could not be saved.";
        try
        {
            var result = await AlbumWorkflow.AddFilesAsync(_files.Items.Cast<string>().ToArray(), albumName, this,
                message => _status.Text = message);
            _status.Text = $"Added {result.AddedToAlbumCount} file(s); uploaded {result.UploadedFiles.Count} new file(s).";
            var message = $"Finished adding files to the OneDrive album '{albumName}'.\r\n\r\n" +
                $"Added to album: {result.AddedToAlbumCount}\r\n" +
                $"Newly uploaded to Pictures/{albumName}: {result.UploadedFiles.Count}\r\n" +
                $"Already found in OneDrive (not uploaded again): {result.ExistingFiles.Count}";
            if (result.UploadedFiles.Count > 0)
                message += $"\r\n\r\nNew uploads:\r\n{FormatFileList(result.UploadedFiles)}";
            if (result.ExistingFiles.Count > 0)
                message += $"\r\n\r\nAlready in OneDrive:\r\n{FormatFileList(result.ExistingFiles)}";
            MessageBox.Show(this, message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception exception)
        {
            _status.Text = "The upload did not complete.";
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed)
                _upload.Enabled = true;
        }
    }

    private static string FormatFileList(IReadOnlyList<string> fileNames)
    {
        const int maximumNames = 10;
        var lines = fileNames.Take(maximumNames).ToList();
        if (fileNames.Count > maximumNames)
            lines.Add($"...and {fileNames.Count - maximumNames} more");
        return string.Join(Environment.NewLine, lines);
    }
}
