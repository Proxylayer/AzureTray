namespace AzureTray.Shell;

// Thin wrapper around Microsoft.Win32.OpenFileDialog so the rest of the app
// (ViewModels in particular) can be unit-tested without WPF dialog dependencies.
public interface IFileDialogService
{
    // Returns the selected file path, or null if the user cancelled.
    string? OpenFile(string title, string filter);
}
