using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;

namespace Golan.Gdm.Core;

/// <summary>
/// UI strings in two languages (Modern Standard Arabic "ar" and English "en") inside one build.
/// Tables are embedded JSON files named i18n/&lt;area&gt;.&lt;lang&gt;.json (flat "key": "text"); all areas are merged.
/// The language is chosen in View ▸ Language and applied at startup (a restart switches it).
/// </summary>
public static class Loc
{
    static Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    static Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

    static readonly CultureInfo ArCulture = CreateArCulture();
    static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-US");

    static CultureInfo CreateArCulture()
    {
        var c = (CultureInfo)CultureInfo.GetCultureInfo("ar").Clone();
        c.NumberFormat = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        c.NumberFormat.DigitSubstitution = DigitShapes.None;
        return CultureInfo.ReadOnly(c);
    }

    /// <summary>"ar" or "en".</summary>
    public static string Language { get; private set; } = "ar";
    public static bool IsRtl => Language == "ar";
    public static FlowDirection FlowDirection => IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public static CultureInfo Culture => IsRtl ? ArCulture : EnCulture;

    static Loc() => Init("ar");

    /// <summary>"auto" follows the Windows display language.</summary>
    public static string Resolve(string setting)
        => setting is "ar" or "en" ? setting
           : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? "ar" : "en";

    public static void Init(string setting)
    {
        Language = Resolve(setting);
        _strings = LoadTables(Language);
        _fallback = Language == "en" ? _strings : LoadTables("en");
    }

    static Dictionary<string, string> LoadTables(string lang)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("i18n/") && n.EndsWith($".{lang}.json")))
        {
            try
            {
                using var s = asm.GetManifestResourceStream(name)!;
                var table = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
                foreach (var (k, v) in table) result[k] = v;
            }
            catch (Exception ex) { Log.Write($"i18n table {name} failed: {ex.Message}"); }
        }
        if (result.Count == 0)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "GDM", "src", "GDM", "i18n");
                if (!Directory.Exists(candidate)) candidate = Path.Combine(dir.FullName, "src", "GDM", "i18n");
                if (!Directory.Exists(candidate)) candidate = Path.Combine(dir.FullName, "i18n");
                if (Directory.Exists(candidate))
                {
                    foreach (var file in Directory.EnumerateFiles(candidate, $"*.{lang}.json"))
                    {
                        try
                        {
                            using var s = File.OpenRead(file);
                            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
                            foreach (var (k, v) in table) result[k] = v;
                        }
                        catch (Exception ex) { Log.Write($"i18n file {file} failed: {ex.Message}"); }
                    }
                    if (result.Count > 0) break;
                }
            }
        }
        return result;
    }

    /// <summary>Text for a key; missing keys fall back to English, then to the key itself (so gaps are visible).</summary>
    public static string T(string key)
        => _strings.TryGetValue(key, out var v) ? v : _fallback.TryGetValue(key, out var f) ? f : key;

    /// <summary>Formatted text: T("queue.added", count) with {0} placeholders.</summary>
    public static string T(string key, params object?[] args)
    {
        var raw = T(key);
        if (args is null || args.Length == 0) return raw;
        try { return string.Format(Culture, raw, args); }
        catch (FormatException) { return raw; }
    }
}

/// <summary>XAML: Text="{loc:T main.menu.tasks}" (xmlns:loc="clr-namespace:Golan.Gdm.Core").</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
