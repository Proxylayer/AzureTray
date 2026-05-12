namespace AzureTray.Shell;

internal sealed class FileDialogService : IFileDialogService
{
    public string? OpenFile(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            CheckPathExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
