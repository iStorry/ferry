using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace Ferry.App.Services;

/// <summary>Text to and from the clipboard, which another program can be holding at any moment.</summary>
public static class ClipboardText
{
    public const string ReadFailed = "Couldn't read the clipboard";
    public const string Held = "Another program is using the clipboard. Try again.";

    /// <returns>the text, "" when there is none, or null while another program holds the clipboard</returns>
    public static async Task<string?> ReadAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : "";
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <returns>false while another program holds the clipboard</returns>
    public static bool TryCopy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        try
        {
            Clipboard.SetContent(package);
            return true;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
