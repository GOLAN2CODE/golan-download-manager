using System.Net;
using System.Text;
using System.Text.Json;

namespace Golan.Gdm.Tests.Render;

public static class HtmlReportGenerator
{
    public static void WriteReports(string outDir, IReadOnlyList<RenderedOutput> outputs)
    {
        Directory.CreateDirectory(outDir);

        // 1. Write structured JSON report
        var allIssues = outputs.SelectMany(o => o.Issues).ToList();
        var jsonPath = Path.Combine(outDir, "issues.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(allIssues, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

        // 2. Write index.html showing all rendered windows with ar & en side-by-side
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\"/>");
        sb.AppendLine("  <title>Golan Download Manager v2.2 — Offscreen UI Visual &amp; Layout Verification</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root { --bg: #0e0e10; --card: #161619; --line: #2c2c33; --ink: #f4f4f6; --muted: #a1a1aa; --accent: #fdd103; --warn: #f59e0b; --err: #ef4444; --ok: #22c55e; }");
        sb.AppendLine("    * { box-sizing: border-box; }");
        sb.AppendLine("    body { margin: 0; padding: 24px; font-family: 'Segoe UI', system-ui, sans-serif; background: var(--bg); color: var(--ink); }");
        sb.AppendLine("    h1 { margin: 0 0 8px 0; font-size: 24px; }");
        sb.AppendLine("    .summary { color: var(--muted); margin-bottom: 24px; font-size: 14px; }");
        sb.AppendLine("    .window-section { background: var(--card); border: 1px solid var(--line); border-radius: 12px; padding: 18px; margin-bottom: 24px; }");
        sb.AppendLine("    .window-title { font-size: 18px; font-weight: 600; margin: 0 0 14px 0; display: flex; align-items: center; justify-content: space-between; }");
        sb.AppendLine("    .theme-row { margin-bottom: 16px; }");
        sb.AppendLine("    .theme-label { font-size: 13px; font-weight: 600; color: var(--muted); text-transform: uppercase; margin-bottom: 8px; }");
        sb.AppendLine("    .pair-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }");
        sb.AppendLine("    .shot-card { background: #0b1120; border: 1px solid var(--line); border-radius: 8px; padding: 10px; }");
        sb.AppendLine("    .shot-caption { font-size: 13px; font-weight: 600; margin-bottom: 8px; display: flex; justify-content: space-between; color: var(--ink); }");
        sb.AppendLine("    .shot-caption span.meta { color: var(--muted); font-weight: normal; font-size: 12px; }");
        sb.AppendLine("    .shot-card img { width: 100%; height: auto; border-radius: 4px; display: block; border: 1px solid #1e293b; }");
        sb.AppendLine("    .issues-list { margin-top: 8px; padding: 8px 12px; background: rgba(239, 68, 68, 0.12); border: 1px solid rgba(239, 68, 68, 0.35); border-radius: 6px; font-size: 12px; }");
        sb.AppendLine("    .issues-list div { margin: 3px 0; }");
        sb.AppendLine("    .badge-ok { background: rgba(16, 185, 129, 0.15); color: var(--ok); padding: 2px 8px; border-radius: 999px; font-size: 12px; }");
        sb.AppendLine("    .badge-warn { background: rgba(245, 158, 11, 0.18); color: var(--warn); padding: 2px 8px; border-radius: 999px; font-size: 12px; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <h1>Golan Download Manager v2.2 — Offscreen UI Render Matrix (AR / EN × Light / Dark)</h1>");

        var windowGroups = outputs.GroupBy(o => o.WindowKey).ToList();
        sb.AppendLine($"  <div class=\"summary\">Rendered <strong>{outputs.Count}</strong> images across <strong>{windowGroups.Count}</strong> window states · Total automated layout findings: <strong>{allIssues.Count}</strong></div>");

        foreach (var group in windowGroups)
        {
            string winKey = group.Key;
            int groupIssues = group.Sum(x => x.Issues.Count);
            string badge = groupIssues == 0
                ? "<span class=\"badge-ok\">0 issues</span>"
                : $"<span class=\"badge-warn\">{groupIssues} finding(s)</span>";

            sb.AppendLine("  <div class=\"window-section\">");
            sb.AppendLine($"    <div class=\"window-title\"><span>{WebUtility.HtmlEncode(winKey)}</span>{badge}</div>");

            foreach (string theme in new[] { "light", "dark" })
            {
                sb.AppendLine("    <div class=\"theme-row\">");
                sb.AppendLine($"      <div class=\"theme-label\">{WebUtility.HtmlEncode(theme)} theme (Arabic vs English)</div>");
                sb.AppendLine("      <div class=\"pair-grid\">");

                foreach (string lang in new[] { "ar", "en" })
                {
                    var item = group.FirstOrDefault(x => x.Lang == lang && x.Theme == theme);
                    if (item == null) continue;
                    string fileName = Path.GetFileName(item.PngPath);
                    sb.AppendLine("        <div class=\"shot-card\">");
                    sb.AppendLine($"          <div class=\"shot-caption\"><span>{WebUtility.HtmlEncode($"{winKey} ({lang}.{theme})")}</span><span class=\"meta\">{item.PixelWidth}×{item.PixelHeight}px · {item.Issues.Count} issue(s)</span></div>");
                    sb.AppendLine($"          <a href=\"{WebUtility.HtmlEncode(fileName)}\" target=\"_blank\"><img src=\"{WebUtility.HtmlEncode(fileName)}\" loading=\"lazy\" alt=\"{WebUtility.HtmlEncode($"{winKey}.{lang}.{theme}")}\"/></a>");
                    if (item.Issues.Count > 0)
                    {
                        sb.AppendLine("          <div class=\"issues-list\">");
                        foreach (var iss in item.Issues)
                        {
                            sb.AppendLine($"            <div><strong>[{iss.Kind}]</strong> {WebUtility.HtmlEncode(iss.ElementType)}{(string.IsNullOrEmpty(iss.ElementName) ? "" : "#" + WebUtility.HtmlEncode(iss.ElementName))}: {WebUtility.HtmlEncode(iss.Detail)}</div>");
                        }
                        sb.AppendLine("          </div>");
                    }
                    sb.AppendLine("        </div>");
                }

                sb.AppendLine("      </div>");
                sb.AppendLine("    </div>");
            }

            sb.AppendLine("  </div>");
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        File.WriteAllText(Path.Combine(outDir, "index.html"), sb.ToString(), Encoding.UTF8);
    }

    public static void WriteCompareReport(string outDir, string beforeDir, IReadOnlyList<RenderedOutput> afterOutputs)
    {
        Directory.CreateDirectory(outDir);
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\"/>");
        sb.AppendLine("  <title>Golan Download Manager v2.2 — Before vs After Visual Comparison</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root { --bg: #0e0e10; --card: #161619; --line: #2c2c33; --ink: #f4f4f6; --muted: #a1a1aa; --accent: #fdd103; --ok: #22c55e; --warn: #f59e0b; }");
        sb.AppendLine("    * { box-sizing: border-box; }");
        sb.AppendLine("    body { margin: 0; padding: 24px; font-family: 'Segoe UI', system-ui, sans-serif; background: var(--bg); color: var(--ink); }");
        sb.AppendLine("    h1 { margin: 0 0 8px 0; font-size: 24px; }");
        sb.AppendLine("    .summary { color: var(--muted); margin-bottom: 24px; font-size: 14px; }");
        sb.AppendLine("    .window-section { background: var(--card); border: 1px solid var(--line); border-radius: 12px; padding: 18px; margin-bottom: 24px; }");
        sb.AppendLine("    .window-title { font-size: 18px; font-weight: 600; margin: 0 0 14px 0; display: flex; align-items: center; justify-content: space-between; }");
        sb.AppendLine("    .variant-row { margin-bottom: 16px; }");
        sb.AppendLine("    .variant-label { font-size: 13px; font-weight: 600; color: var(--accent); text-transform: uppercase; margin-bottom: 8px; }");
        sb.AppendLine("    .pair-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }");
        sb.AppendLine("    .shot-card { background: #0a0a0c; border: 1px solid var(--line); border-radius: 8px; padding: 10px; }");
        sb.AppendLine("    .shot-caption { font-size: 13px; font-weight: 600; margin-bottom: 8px; display: flex; justify-content: space-between; color: var(--ink); }");
        sb.AppendLine("    .shot-caption span.meta { color: var(--muted); font-weight: normal; font-size: 12px; }");
        sb.AppendLine("    .shot-card img { width: 100%; height: auto; border-radius: 4px; display: block; border: 1px solid #222; }");
        sb.AppendLine("    .badge-ok { background: rgba(34, 197, 94, 0.15); color: var(--ok); padding: 2px 8px; border-radius: 999px; font-size: 12px; }");
        sb.AppendLine("    .badge-warn { background: rgba(245, 158, 11, 0.18); color: var(--warn); padding: 2px 8px; border-radius: 999px; font-size: 12px; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <h1>Golan Download Manager v2.2 — Before vs After Visual Comparison</h1>");

        var windowGroups = afterOutputs.GroupBy(o => o.WindowKey).ToList();
        int totalAfterIssues = afterOutputs.Sum(o => o.Issues.Count);
        sb.AppendLine($"  <div class=\"summary\">Comparing <strong>{afterOutputs.Count}</strong> before/after renders across <strong>{windowGroups.Count}</strong> window states · After layout findings: <strong>{totalAfterIssues}</strong></div>");

        foreach (var group in windowGroups)
        {
            string winKey = group.Key;
            int groupIssues = group.Sum(x => x.Issues.Count);
            string badge = groupIssues == 0
                ? "<span class=\"badge-ok\">0 issues</span>"
                : $"<span class=\"badge-warn\">{groupIssues} finding(s)</span>";

            sb.AppendLine("  <div class=\"window-section\">");
            sb.AppendLine($"    <div class=\"window-title\"><span>{WebUtility.HtmlEncode(winKey)}</span>{badge}</div>");

            foreach (string theme in new[] { "light", "dark" })
            {
                foreach (string lang in new[] { "ar", "en" })
                {
                    var item = group.FirstOrDefault(x => x.Lang == lang && x.Theme == theme);
                    if (item == null) continue;
                    string fileName = Path.GetFileName(item.PngPath);
                    string beforeRel = $"before/{fileName}";
                    sb.AppendLine("    <div class=\"variant-row\">");
                    sb.AppendLine($"      <div class=\"variant-label\">{WebUtility.HtmlEncode($"{winKey} — {lang}.{theme}")}</div>");
                    sb.AppendLine("      <div class=\"pair-grid\">");
                    sb.AppendLine("        <div class=\"shot-card\">");
                    sb.AppendLine($"          <div class=\"shot-caption\"><span>BEFORE ({WebUtility.HtmlEncode($"{lang}.{theme}")})</span><span class=\"meta\">GDM 2.1 baseline</span></div>");
                    sb.AppendLine($"          <a href=\"{WebUtility.HtmlEncode(beforeRel)}\" target=\"_blank\"><img src=\"{WebUtility.HtmlEncode(beforeRel)}\" loading=\"lazy\" alt=\"Before {WebUtility.HtmlEncode($"{winKey}.{lang}.{theme}")}\"/></a>");
                    sb.AppendLine("        </div>");
                    sb.AppendLine("        <div class=\"shot-card\">");
                    sb.AppendLine($"          <div class=\"shot-caption\"><span>AFTER ({WebUtility.HtmlEncode($"{lang}.{theme}")})</span><span class=\"meta\">{item.PixelWidth}×{item.PixelHeight}px · {item.Issues.Count} issue(s)</span></div>");
                    sb.AppendLine($"          <a href=\"{WebUtility.HtmlEncode(fileName)}\" target=\"_blank\"><img src=\"{WebUtility.HtmlEncode(fileName)}\" loading=\"lazy\" alt=\"After {WebUtility.HtmlEncode($"{winKey}.{lang}.{theme}")}\"/></a>");
                    sb.AppendLine("        </div>");
                    sb.AppendLine("      </div>");
                    sb.AppendLine("    </div>");
                }
            }

            sb.AppendLine("  </div>");
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        File.WriteAllText(Path.Combine(outDir, "compare.html"), sb.ToString(), Encoding.UTF8);
    }
}
