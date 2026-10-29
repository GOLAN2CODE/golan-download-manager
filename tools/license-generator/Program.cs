using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Golan.Gdm.Core.Licensing;
using Microsoft.Win32;

namespace Golan.Gdm.LicenseGenerator;

/// <summary>
/// The owner's private tool: turns a customer's name + machine code + duration into a signed activation key.
/// The signing key never leaves this PC: it is stored DPAPI-protected (current Windows user) under %USERPROFILE%\.golan-keys
/// and can be exported only as a password-encrypted backup.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new GeneratorWindow());
    }
}

static class SigningKey
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Golan.GDM.LicenseSigning.v1");

    public static string KeyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".golan-keys", "gdm-license-signing.key");

    public static ECDsa? Load()
    {
        if (!File.Exists(KeyPath)) return null;
        var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }

    public static void Save(ECDsa key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try { File.WriteAllBytes(KeyPath, ProtectedData.Protect(pkcs8, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }

    /// <summary>True when this private key belongs to the public key compiled into the app.</summary>
    public static bool MatchesApp(ECDsa key)
        => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) == LicensePublicKey.SubjectPublicKeyInfo;

    public static byte[] ExportBackup(ECDsa key, string password)
        => key.ExportEncryptedPkcs8PrivateKey(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));

    public static ECDsa ImportBackup(byte[] data, string password)
    {
        var key = ECDsa.Create();
        key.ImportEncryptedPkcs8PrivateKey(password, data, out _);
        return key;
    }
}

static class History
{
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Golan", "GDM-licenses.csv");

    public static void Append(LicenseInfo info, string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        bool isNew = !File.Exists(FilePath);
        using var w = new StreamWriter(FilePath, append: true, new UTF8Encoding(true));
        if (isNew) w.WriteLine("التاريخ,الاسم,رمز الجهاز,ينتهي في,المفتاح");
        w.WriteLine(string.Join(",", Csv(info.IssuedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)), Csv(info.Name),
            Csv(info.Machine), Csv(info.ExpiresUtc?.ToLocalTime().ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) ?? "مدى الحياة"), Csv(key)));
    }

    public static List<string[]> Read()
    {
        var rows = new List<string[]>();
        if (!File.Exists(FilePath)) return rows;
        foreach (var line in File.ReadAllLines(FilePath).Skip(1))
            rows.Add(SplitCsv(line));
        rows.Reverse();
        return rows;
    }

    static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells.ToArray();
    }
}

sealed class GeneratorWindow : Window
{
    static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(0xFD, 0xD1, 0x03));
    static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x13, 0x13, 0x13));
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x5F, 0x5A, 0x4E));
    static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xC0, 0x1C, 0x28));
    static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34));

    /// <summary>The licence lengths the owner can issue; "Expires" null means lifetime.</summary>
    sealed record Duration(string Label, Func<DateTime, DateTime?> Expires, bool Custom = false);

    static readonly Duration[] Durations =
    [
        new("شهر واحد", now => now.AddMonths(1)),
        new("شهران", now => now.AddMonths(2)),
        new("ثلاثة أشهر", now => now.AddMonths(3)),
        new("ستة أشهر", now => now.AddMonths(6)),
        new("سنة", now => now.AddYears(1)),
        new("سنتان", now => now.AddYears(2)),
        new("مدى الحياة", _ => null),
        new("حتى تاريخ محدد…", _ => null, Custom: true),
    ];

    Duration SelectedDuration => Durations[Math.Clamp(_duration.SelectedIndex, 0, Durations.Length - 1)];

    ECDsa? _key;
    readonly TextBox _name = new() { Padding = new Thickness(6) };
    readonly TextBox _machine = new() { Padding = new Thickness(6), FlowDirection = FlowDirection.LeftToRight, FontFamily = new FontFamily("Consolas"), FontSize = 15 };
    readonly ComboBox _duration = new() { Padding = new Thickness(6) };
    readonly DatePicker _date = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBox _output = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 80, FlowDirection = FlowDirection.LeftToRight, FontFamily = new FontFamily("Consolas"), Padding = new Thickness(6) };
    readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    readonly DataGrid _history = new() { AutoGenerateColumns = false, IsReadOnly = true, Height = 170, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false };

    public GeneratorWindow()
    {
        Title = "مولّد تراخيص جولان - مدير التنزيل";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FlowDirection = FlowDirection.RightToLeft;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xF8, 0xF2));

        var root = new StackPanel { Margin = new Thickness(22) };
        var header = new Border { Background = Yellow, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12) };
        header.Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "مولّد تراخيص جولان", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Ink },
                new TextBlock { Text = "أدخل اسم المستخدم ورمز جهازه واختر المدة، ثم أرسل له المفتاح.", Foreground = Ink },
            },
        };
        root.Children.Add(header);

        root.Children.Add(Label("اسم المستخدم"));
        root.Children.Add(_name);
        root.Children.Add(Label("رمز الجهاز (يظهر له في «المساعدة ← التفعيل»)"));
        root.Children.Add(_machine);
        root.Children.Add(Label("المدة"));
        foreach (var d in Durations) _duration.Items.Add(d.Label);
        _duration.SelectedIndex = 0;
        _duration.SelectionChanged += (_, _) => _date.Visibility = SelectedDuration.Custom ? Visibility.Visible : Visibility.Collapsed;
        root.Children.Add(_duration);
        _date.SelectedDate = DateTime.Today.AddMonths(1);
        root.Children.Add(_date);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var generate = Btn("إنشاء المفتاح", primary: true);
        generate.Click += (_, _) => Generate();
        var copy = Btn("نسخ المفتاح");
        copy.Click += (_, _) => { if (_output.Text.Length > 0) { Clipboard.SetText(_output.Text); Say("تم نسخ المفتاح ✓", Ok); } };
        var copyMsg = Btn("نسخ رسالة جاهزة");
        copyMsg.Click += (_, _) =>
        {
            if (_output.Text.Length == 0) return;
            Clipboard.SetText("مفتاح تفعيل جولان - مدير التنزيل:\r\n" + _output.Text + "\r\n\r\nافتح البرنامج ← المساعدة ← التفعيل، والصق المفتاح ثم اضغط «تفعيل».");
            Say("تم نسخ رسالة جاهزة للإرسال ✓", Ok);
        };
        actions.Children.Add(generate);
        actions.Children.Add(copy);
        actions.Children.Add(copyMsg);
        root.Children.Add(actions);
        root.Children.Add(Label("المفتاح"));
        root.Children.Add(_output);
        root.Children.Add(_message);

        root.Children.Add(Label("التراخيص الصادرة"));
        foreach (var (title, i) in new[] { ("التاريخ", 0), ("الاسم", 1), ("رمز الجهاز", 2), ("ينتهي في", 3) })
            _history.Columns.Add(new DataGridTextColumn { Header = title, Binding = new System.Windows.Data.Binding($"[{i}]"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        root.Children.Add(_history);

        var keyTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var backup = Btn("حفظ نسخة احتياطية من مفتاح التوقيع");
        backup.Click += (_, _) => Backup();
        var restore = Btn("استيراد نسخة احتياطية");
        restore.Click += (_, _) => Restore();
        var openCsv = Btn("فتح سجل التراخيص");
        openCsv.Click += (_, _) =>
        {
            if (File.Exists(History.FilePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + History.FilePath + "\"") { UseShellExecute = true });
        };
        keyTools.Children.Add(backup);
        keyTools.Children.Add(restore);
        keyTools.Children.Add(openCsv);
        root.Children.Add(keyTools);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => LoadKey();
    }

    void LoadKey()
    {
        try { _key = SigningKey.Load(); }
        catch (CryptographicException) { _key = null; }
        if (_key == null)
            Say("لم يُعثر على مفتاح التوقيع على هذا الجهاز. استورد النسخة الاحتياطية لتتمكن من إصدار المفاتيح.", Danger);
        else if (!SigningKey.MatchesApp(_key))
            Say("تنبيه: مفتاح التوقيع لا يطابق المفتاح العام داخل البرنامج؛ المفاتيح الصادرة لن تعمل.", Danger);
        else
            Say("مفتاح التوقيع جاهز ✓  — احفظ له نسخة احتياطية في مكان آمن إن لم تفعل.", Ok);
        RefreshHistory();
    }

    void RefreshHistory()
    {
        try { _history.ItemsSource = History.Read(); }
        catch (IOException) { _history.ItemsSource = null; }
    }

    void Generate()
    {
        if (_key == null) { Say("لا يوجد مفتاح توقيع. استورد النسخة الاحتياطية أولًا.", Danger); return; }
        var name = _name.Text.Trim();
        var machine = LicenseKey.NormalizeMachine(_machine.Text);
        if (name.Length == 0) { Say("اكتب اسم المستخدم.", Danger); return; }
        if (machine.Length != 16) { Say("رمز الجهاز غير صحيح: يجب أن يتكون من 16 حرفًا ورقمًا (مثل ABCD-EFGH-JKLM-NPQR).", Danger); return; }

        var now = DateTime.UtcNow;
        var duration = SelectedDuration;
        DateTime? expires = duration.Custom
            ? (_date.SelectedDate is { } d ? DateTime.SpecifyKind(d.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Local).ToUniversalTime() : null)
            : duration.Expires(now);
        if (duration.Custom && expires is null) { Say("اختر التاريخ أولًا.", Danger); return; }
        if (expires is { } e && e <= now) { Say("التاريخ المحدد في الماضي.", Danger); return; }

        var formatted = string.Join("-", Enumerable.Range(0, 4).Select(i => machine.Substring(i * 4, 4)));
        var info = new LicenseInfo(name, formatted, now, expires);
        var key = LicenseKey.Create(info, _key);

        // Self-check with the public key that ships inside the app.
        using var pub = LicensePublicKey.Load();
        if (!LicenseKey.TryVerify(key, pub, formatted, out _, out _)) { Say("فشل التحقق من المفتاح الناتج. لا ترسله.", Danger); return; }

        _output.Text = key;
        try { History.Append(info, key); } catch (IOException ex) { Say("أُنشئ المفتاح لكن تعذّر حفظه في السجل: " + ex.Message, Danger); return; }
        RefreshHistory();
        Say(expires is null ? $"تم إنشاء مفتاح مدى الحياة لـ «{name}» ✓" : $"تم إنشاء مفتاح لـ «{name}» صالح حتى {expires.Value.ToLocalTime():yyyy/MM/dd} ✓", Ok);
    }

    void Backup()
    {
        if (_key == null) { Say("لا يوجد مفتاح توقيع لحفظه.", Danger); return; }
        var pw = PasswordPrompt.Ask(this, "اختر كلمة مرور قوية لحماية النسخة الاحتياطية (احفظها جيدًا):", confirm: true);
        if (pw == null) return;
        var dlg = new SaveFileDialog { FileName = "gdm-license-signing-backup.gdmkey", Filter = "نسخة مفتاح جولان|*.gdmkey" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllBytes(dlg.FileName, SigningKey.ExportBackup(_key, pw));
        Say("تم حفظ النسخة الاحتياطية ✓ — احفظها مع كلمة المرور في مكان آمن خارج هذا الجهاز.", Ok);
    }

    void Restore()
    {
        var dlg = new OpenFileDialog { Filter = "نسخة مفتاح جولان|*.gdmkey" };
        if (dlg.ShowDialog(this) != true) return;
        var pw = PasswordPrompt.Ask(this, "أدخل كلمة مرور النسخة الاحتياطية:", confirm: false);
        if (pw == null) return;
        try
        {
            var key = SigningKey.ImportBackup(File.ReadAllBytes(dlg.FileName), pw);
            if (!SigningKey.MatchesApp(key)) { Say("هذه النسخة لا تطابق المفتاح العام داخل البرنامج.", Danger); return; }
            SigningKey.Save(key);
            LoadKey();
        }
        catch (CryptographicException) { Say("كلمة المرور غير صحيحة أو الملف تالف.", Danger); }
    }

    void Say(string text, Brush color) { _message.Text = text; _message.Foreground = color; }

    static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4), Foreground = Ink };

    static Button Btn(string text, bool primary = false) => new()
    {
        Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0),
        Background = primary ? Yellow : Brushes.White, Foreground = Ink, BorderBrush = primary ? Yellow : new SolidColorBrush(Color.FromRgb(0xDD, 0xD6, 0xC4)),
        FontWeight = primary ? FontWeights.Bold : FontWeights.Normal,
    };
}

static class PasswordPrompt
{
    public static string? Ask(Window owner, string prompt, bool confirm)
    {
        var w = new Window
        {
            Title = "كلمة المرور", Owner = owner, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FlowDirection = FlowDirection.RightToLeft, FontSize = 14,
        };
        var p1 = new PasswordBox { Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6) };
        var p2 = new PasswordBox { Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6) };
        var msg = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "موافق", IsDefault = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 10, 8, 0) };
        var cancel = new Button { Content = "إلغاء", IsCancel = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 10, 0, 0) };
        string? result = null;
        ok.Click += (_, _) =>
        {
            if (p1.Password.Length < 8) { msg.Text = "كلمة المرور يجب أن تكون 8 أحرف على الأقل."; return; }
            if (confirm && p1.Password != p2.Password) { msg.Text = "كلمتا المرور غير متطابقتين."; return; }
            result = p1.Password;
            w.DialogResult = true;
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(p1);
        if (confirm)
        {
            panel.Children.Add(new TextBlock { Text = "أعد كتابة كلمة المرور:", Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(p2);
        }
        panel.Children.Add(msg);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { ok, cancel } });
        w.Content = panel;
        return w.ShowDialog() == true ? result : null;
    }
}
