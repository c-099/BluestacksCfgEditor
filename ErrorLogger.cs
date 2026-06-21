using System.Text;

namespace BluestacksCfgEditor;

internal static class ErrorLogger
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    internal static string LogPath => Path.Combine(AppContext.BaseDirectory, "cfg_editor_error.log");

    internal static string LogException(Exception exception)
    {
        string text = exception.ToString();
        File.WriteAllText(LogPath, text, Utf8NoBom);
        return LogPath;
    }

    internal static void ShowUnexpectedError(IWin32Window? owner, string title, Exception exception)
    {
        string logPath = LogException(exception);
        MessageBox.Show(
            owner,
            $"An unexpected error occurred.\n\nDetails were written to:\n{logPath}",
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
