using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Integration;

public sealed record ExtensionPackageResult(
    string Version,
    string ChromeEdgeZipPath,
    string FirefoxXpiPath,
    IReadOnlyList<string> IncludedEntries);

/// <summary>
/// Builds deterministic, store-ready browser extension packages for Chrome/Edge (.zip) and Firefox (.xpi)
/// from the unpacked extension folder, validating manifest structure, referenced files, and localization tables.
/// </summary>
public static class ExtensionPackager
{
    public static readonly DateTimeOffset DeterministicTimestamp = new(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Local));

    static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly Regex VersionRegex = new(@"^(0|[1-9]\d{0,4})(\.(0|[1-9]\d{0,4})){0,3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex MsgPlaceholderRegex = new(@"__MSG_([A-Za-z0-9_@]+)__", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ExtensionPackageResult Build(string sourceDir, string outDir)
    {
        if (string.IsNullOrWhiteSpace(sourceDir))
            throw new ArgumentException("Extension source directory is required.", nameof(sourceDir));
        if (string.IsNullOrWhiteSpace(outDir))
            throw new ArgumentException("Output directory is required.", nameof(outDir));

        var fullSourceDir = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(fullSourceDir))
            throw new DirectoryNotFoundException($"Extension source directory not found: {fullSourceDir}");

        var manifestPath = Path.Combine(fullSourceDir, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("manifest.json not found in extension source directory.", manifestPath);

        JsonObject rawManifest;
        try
        {
            rawManifest = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidDataException("manifest.json root must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"manifest.json is not valid JSON: {ex.Message}", ex);
        }

        var chromeManifest = TransformManifestForChromeEdge(rawManifest);
        var firefoxManifest = TransformManifestForFirefox(rawManifest);

        ValidateManifest(chromeManifest, fullSourceDir, isFirefox: false);
        ValidateManifest(firefoxManifest, fullSourceDir, isFirefox: true);

        var version = chromeManifest["version"]!.GetValue<string>().Trim();
        var entries = CollectRelativeFiles(fullSourceDir);

        Directory.CreateDirectory(outDir);
        var fullOutDir = Path.GetFullPath(outDir);
        var chromeZipPath = Path.Combine(fullOutDir, $"gdm-{version}-chrome-edge.zip");
        var firefoxXpiPath = Path.Combine(fullOutDir, $"gdm-{version}-firefox.xpi");

        WriteArchiveDeterministic(fullSourceDir, chromeZipPath, chromeManifest, entries);
        WriteArchiveDeterministic(fullSourceDir, firefoxXpiPath, firefoxManifest, entries);

        return new ExtensionPackageResult(version, chromeZipPath, firefoxXpiPath, entries);
    }

    /// <summary>Builds a deterministic Firefox .xpi package at the specified file path.</summary>
    public static void BuildFirefoxXpi(string sourceDir, string xpiPath, JsonObject? sourceManifest = null)
    {
        var fullSourceDir = Path.GetFullPath(sourceDir);
        var manifestPath = Path.Combine(fullSourceDir, "manifest.json");
        if (sourceManifest == null && !File.Exists(manifestPath))
            throw new FileNotFoundException("manifest.json not found in extension source directory.", manifestPath);

        var rawManifest = sourceManifest ?? JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)) as JsonObject
            ?? throw new InvalidDataException("manifest.json root must be a JSON object.");
        var firefoxManifest = TransformManifestForFirefox(rawManifest);
        ValidateManifest(firefoxManifest, fullSourceDir, isFirefox: true);
        var entries = CollectRelativeFiles(fullSourceDir);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(xpiPath))!);
        WriteArchiveDeterministic(fullSourceDir, Path.GetFullPath(xpiPath), firefoxManifest, entries);
    }

    public static JsonObject TransformManifestForChromeEdge(JsonObject sourceManifest)
    {
        var clone = JsonNode.Parse(sourceManifest.ToJsonString())!.AsObject();
        clone.Remove("key");
        clone.Remove("browser_specific_settings");
        if (clone["background"] is JsonObject bg)
        {
            if (!bg.ContainsKey("service_worker") && bg["scripts"] is JsonArray scripts && scripts.Count > 0 && scripts[0] is JsonValue sv && sv.TryGetValue<string>(out var firstScript))
                bg["service_worker"] = firstScript;
            bg.Remove("scripts");
        }
        return clone;
    }

    /// <summary>
    /// Manifest for the folder users load with "Load unpacked" in Chrome/Edge: the store transform (no Firefox-only keys,
    /// so Chrome shows no warnings) but keeping "key", which fixes the extension ID the native host trusts.
    /// </summary>
    public static JsonObject TransformManifestForUnpackedChromium(JsonObject sourceManifest)
    {
        var clone = TransformManifestForChromeEdge(sourceManifest);
        if (sourceManifest["key"] is JsonValue key && key.TryGetValue<string>(out var k) && k.Length > 0)
            clone["key"] = k;
        return clone;
    }

    public static JsonObject TransformManifestForFirefox(JsonObject sourceManifest)
    {
        var clone = JsonNode.Parse(sourceManifest.ToJsonString())!.AsObject();
        clone.Remove("key");
        if (clone["background"] is JsonObject bg)
        {
            if (bg["scripts"] is not JsonArray { Count: > 0 } && bg["service_worker"] is JsonValue swVal && swVal.TryGetValue<string>(out var sw) && !string.IsNullOrWhiteSpace(sw))
                bg["scripts"] = new JsonArray(sw);
            bg.Remove("service_worker");
        }
        return clone;
    }

    public static void ValidateManifest(JsonObject manifest, string sourceDir, bool isFirefox)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var fullSourceDir = Path.GetFullPath(sourceDir);

        if (manifest["manifest_version"] is not JsonValue mvVal || !mvVal.TryGetValue<int>(out var mv) || mv != 3)
            throw new InvalidDataException("manifest.json must declare manifest_version = 3.");

        string[] requiredStrings = ["name", "version", "description"];
        foreach (var key in requiredStrings)
        {
            if (manifest[key] is not JsonValue val || !val.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s))
                throw new InvalidDataException($"manifest.json is missing required string property '{key}'.");
        }

        var version = manifest["version"]!.GetValue<string>().Trim();
        if (!VersionRegex.IsMatch(version) || version.Split('.').Any(part => !int.TryParse(part, out var n) || n > 65535))
            throw new InvalidDataException($"manifest.json has invalid store version '{version}'. Expected 1-4 integers (0..65535).");

        if (manifest.ContainsKey("key"))
            throw new InvalidDataException("Store manifest must not include 'key'.");

        if (manifest["permissions"] is not JsonArray)
            throw new InvalidDataException("manifest.json must include a 'permissions' array.");

        if (manifest["background"] is not JsonObject bg)
            throw new InvalidDataException("manifest.json must include a 'background' object.");

        if (isFirefox)
        {
            var geckoId = manifest["browser_specific_settings"]?["gecko"]?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(geckoId))
                throw new InvalidDataException("Firefox manifest must include browser_specific_settings.gecko.id.");
            if (bg.ContainsKey("service_worker"))
                throw new InvalidDataException("Firefox store manifest must drop background.service_worker when background.scripts is used.");
            if (bg["scripts"] is not JsonArray scripts || scripts.Count == 0)
                throw new InvalidDataException("Firefox manifest must include non-empty background.scripts.");
            foreach (var scriptNode in scripts)
            {
                var scriptRel = scriptNode?.GetValue<string>() ?? "";
                EnsureReferencedFileExists(fullSourceDir, scriptRel, "background.scripts");
            }
        }
        else
        {
            if (manifest.ContainsKey("browser_specific_settings"))
                throw new InvalidDataException("Chrome/Edge store manifest must not include browser_specific_settings.");
            if (bg.ContainsKey("scripts"))
                throw new InvalidDataException("Chrome/Edge store manifest must not include background.scripts.");
            var sw = bg["service_worker"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(sw))
                throw new InvalidDataException("Chrome/Edge manifest must include background.service_worker.");
            EnsureReferencedFileExists(fullSourceDir, sw, "background.service_worker");
        }

        if (manifest["icons"] is not JsonObject icons || icons.Count == 0)
            throw new InvalidDataException("manifest.json must include a non-empty 'icons' object.");
        foreach (var (sizeKey, iconNode) in icons)
        {
            var iconPath = iconNode?.GetValue<string>() ?? "";
            EnsureReferencedFileExists(fullSourceDir, iconPath, $"icons.{sizeKey}");
        }

        if (manifest["action"] is not JsonObject action)
            throw new InvalidDataException("manifest.json must include an 'action' object.");
        if (action["default_popup"] is JsonValue popupVal && popupVal.TryGetValue<string>(out var popupPath) && !string.IsNullOrWhiteSpace(popupPath))
            EnsureReferencedFileExists(fullSourceDir, popupPath, "action.default_popup");
        if (action["default_icon"] is JsonObject actionIcons)
        {
            foreach (var (sizeKey, iconNode) in actionIcons)
            {
                var iconPath = iconNode?.GetValue<string>() ?? "";
                EnsureReferencedFileExists(fullSourceDir, iconPath, $"action.default_icon.{sizeKey}");
            }
        }
        else if (action["default_icon"] is JsonValue singleIcon && singleIcon.TryGetValue<string>(out var singleIconPath))
        {
            EnsureReferencedFileExists(fullSourceDir, singleIconPath, "action.default_icon");
        }

        if (manifest["content_scripts"] is JsonArray contentScripts)
        {
            foreach (var csNode in contentScripts)
            {
                if (csNode is not JsonObject cs) continue;
                if (cs["js"] is JsonArray jsArr)
                {
                    foreach (var js in jsArr)
                        EnsureReferencedFileExists(fullSourceDir, js?.GetValue<string>() ?? "", "content_scripts.js");
                }
                if (cs["css"] is JsonArray cssArr)
                {
                    foreach (var css in cssArr)
                        EnsureReferencedFileExists(fullSourceDir, css?.GetValue<string>() ?? "", "content_scripts.css");
                }
            }
        }

        // Validate _locales/<default_locale>/messages.json if default_locale or __MSG_ placeholders are used
        var manifestJsonText = manifest.ToJsonString();
        var msgMatches = MsgPlaceholderRegex.Matches(manifestJsonText);
        var defaultLocale = manifest["default_locale"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(defaultLocale) || msgMatches.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(defaultLocale))
                throw new InvalidDataException("manifest.json uses __MSG_ placeholders but does not specify default_locale.");
            var defaultMessagesRel = $"_locales/{defaultLocale}/messages.json";
            var defaultMessagesPath = EnsureReferencedFileExists(fullSourceDir, defaultMessagesRel, "default_locale");
            JsonObject messagesObj;
            try
            {
                messagesObj = JsonNode.Parse(File.ReadAllText(defaultMessagesPath, Encoding.UTF8)) as JsonObject
                    ?? throw new InvalidDataException($"{defaultMessagesRel} root must be a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{defaultMessagesRel} is invalid JSON: {ex.Message}", ex);
            }

            foreach (Match m in msgMatches)
            {
                var msgKey = m.Groups[1].Value;
                var msgVal = messagesObj[msgKey]?["message"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(msgVal))
                    throw new InvalidDataException($"Placeholder __MSG_{msgKey}__ in manifest.json is missing from {defaultMessagesRel}.");
            }
        }
    }

    static string EnsureReferencedFileExists(string fullSourceDir, string relativePath, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException($"Manifest field '{fieldName}' contains an empty file path.");
        if (relativePath.Contains('\\') || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Manifest field '{fieldName}' must use relative forward-slash paths: '{relativePath}'.");

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".." || s.StartsWith('.')))
            throw new InvalidDataException($"Manifest field '{fieldName}' contains an invalid or dotfile path segment: '{relativePath}'.");

        var candidate = Path.GetFullPath(Path.Combine(fullSourceDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = fullSourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manifest field '{fieldName}' escapes extension directory: '{relativePath}'.");

        if (!File.Exists(candidate))
            throw new FileNotFoundException($"Referenced file '{relativePath}' in manifest field '{fieldName}' does not exist.", candidate);

        return candidate;
    }

    static List<string> CollectRelativeFiles(string fullSourceDir)
    {
        var list = new List<string> { "manifest.json" };
        foreach (var file in Directory.EnumerateFiles(fullSourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(fullSourceDir, file).Replace('\\', '/').TrimStart('/');
            if (string.Equals(rel, "manifest.json", StringComparison.OrdinalIgnoreCase))
                continue;
            var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                continue;
            if (string.Equals(segments[0], "tests", StringComparison.OrdinalIgnoreCase))
                continue;
            if (segments.Any(s => s.StartsWith('.')))
                continue;
            list.Add(rel);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    static void WriteArchiveDeterministic(string fullSourceDir, string targetArchivePath, JsonObject manifest, IReadOnlyList<string> sortedEntries)
    {
        var tmpPath = targetArchivePath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var entryName in sortedEntries)
                {
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    entry.LastWriteTime = DeterministicTimestamp;
                    using var dst = entry.Open();
                    if (entryName == "manifest.json")
                    {
                        var json = manifest.ToJsonString(ManifestJsonOptions).Replace("\r\n", "\n") + "\n";
                        var bytes = new UTF8Encoding(false).GetBytes(json);
                        dst.Write(bytes, 0, bytes.Length);
                    }
                    else
                    {
                        var srcPath = Path.Combine(fullSourceDir, entryName.Replace('/', Path.DirectorySeparatorChar));
                        using var src = File.OpenRead(srcPath);
                        src.CopyTo(dst);
                    }
                }
            }
            File.Move(tmpPath, targetArchivePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); }
                catch (IOException) { /* best-effort temporary archive cleanup */ }
            }
        }
    }
}
