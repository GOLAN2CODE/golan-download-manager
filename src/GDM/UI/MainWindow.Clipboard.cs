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
        if (ShouldCaptureClipboardUrl(text, _settings))
            App.Instance.ShowAddUrl(text);
    }

    public static bool ShouldCaptureClipboardUrl(string? text, AppSettings settings)
        => FileNames.ShouldCaptureClipboardUrl(text, settings);

    /// <summary>Called when Golan copies something itself, so it doesn't pop a dialog for its own copy.</summary>
    public void SuppressClipboard() => _ignoreClipboardUntil = DateTime.Now.AddSeconds(2);

    public static List<BatchLink> ExtractLinksFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var rx = new System.Text.RegularExpressions.Regex(@"(?:https?|ftps?)://[^\s""'<>]+",
            System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromSeconds(2));
        try
        {
            var list = new List<BatchLink>();
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(text))
            {
                var u = TrimExtractedUrl(m.Value);
                if (FileNames.IsNetworkDownloadUrl(u))
                    list.Add(new BatchLink(u, ""));
            }
            return list.DistinctBy(l => l.Url).ToList();
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex)
        {
            Log.Write("batch clipboard regex timeout: " + ex.Message);
            return [];
        }
    }

    static string TrimExtractedUrl(string raw)
    {
        var u = raw;
        while (u.Length > 0)
        {
            char last = u[^1];
            if (last is '.' or ',' or ';') { u = u[..^1]; continue; }
            if (last == ')' && u.Count(c => c == ')') > u.Count(c => c == '(')) { u = u[..^1]; continue; }
            if (last == ']' && u.Count(c => c == ']') > u.Count(c => c == '[')) { u = u[..^1]; continue; }
            break;
        }
        return u;
    }

    void Clipboard_PasteUrl()
    {
        if (Keyboard.FocusedElement is TextBox) { ApplicationCommands.Paste.Execute(null, Keyboard.FocusedElement as IInputElement); return; }
        try
        {
            var t = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            if (FileNames.IsDownloadableUrl(t) || MagnetValidator.IsMagnetUri(t))
            {
                App.Instance.ShowAddUrl(t);
                return;
            }
            var links = ExtractLinksFromText(t);
            if (links.Count > 1) App.Instance.ShowBatch(links, "", "");
            else if (links.Count == 1) App.Instance.ShowAddUrl(links[0].Url);
            else App.Instance.ShowAddUrl(null);
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
            return (FileNames.IsDownloadableUrl(t) || MagnetValidator.IsMagnetUri(t)) ? t : null;
        }
        catch (ExternalException)
        {
            // Clipboard temporarily locked by another process.
            return null;
        }
    }
}
