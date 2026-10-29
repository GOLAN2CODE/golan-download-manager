using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;

namespace Golan.Gdm.UI;

public partial class MainWindow
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    const int WM_CLIPBOARDUPDATE = 0x031D;

    void HookClipboard()
    {
        if (_hwnd != null) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        _hwnd = HwndSource.FromHwnd(handle);
        _hwnd.AddHook(WndProc);
        AddClipboardFormatListener(handle);
        try { _lastClipboard = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; }
        catch (ExternalException) { /* clipboard locked by another process */ }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) Dispatcher.BeginInvoke(OnClipboardChanged);
        return IntPtr.Zero;
    }

    void OnClipboardChanged()
    {
        if (!_settings.ClipboardMonitor || DateTime.Now < _ignoreClipboardUntil) return;
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; }
        catch (ExternalException) { return; /* clipboard locked by another process */ }
        if (text.Length == 0 || text == _lastClipboard || text.Contains('\n')) return;
        _lastClipboard = text;
        if (MagnetValidator.IsMagnetUri(text))
        {
            App.Instance.ShowAddUrl(text);
            return;
        }
        if (!FileNames.IsHttpUrl(text) || _settings.IsException(text)) return;

        var ext = Path.GetExtension(new Uri(text).AbsolutePath).TrimStart('.').ToLowerInvariant();
        if (ext.Length > 0 && _settings.ExtensionList.Contains(ext))
            App.Instance.ShowAddUrl(text);
    }

    /// <summary>Called when Golan copies something itself, so it doesn't pop a dialog for its own copy.</summary>
    public void SuppressClipboard() => _ignoreClipboardUntil = DateTime.Now.AddSeconds(2);

    void Clipboard_PasteUrl()
    {
        if (Keyboard.FocusedElement is TextBox) { ApplicationCommands.Paste.Execute(null, Keyboard.FocusedElement as IInputElement); return; }
        try
        {
            var t = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            App.Instance.ShowAddUrl((FileNames.IsHttpUrl(t) || MagnetValidator.IsMagnetUri(t)) ? t : null);
        }
        catch (ExternalException ex)
        {
            Log.Write("clipboard paste failed: " + ex.Message);
        }
    }

    static string? ClipboardUrl()
    {
        try
        {
            var t = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            return (FileNames.IsHttpUrl(t) || MagnetValidator.IsMagnetUri(t)) ? t : null;
        }
        catch (ExternalException)
        {
            // Clipboard temporarily locked by another process.
            return null;
        }
    }
}
