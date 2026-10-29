using System.Diagnostics;
using System.Runtime.CompilerServices;
using Golan.Gdm.Tests.Render;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var gdmHome = Path.Combine(Path.GetTempPath(), $"gdm-render-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("GDM_HOME", gdmHome);
        try
        {
            return RunInSta(gdmHome, args);
        }
        finally
        {
            try { if (Directory.Exists(gdmHome)) Directory.Delete(gdmHome, true); } catch { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunInSta(string gdmHome, string[] args)
    {
        var sw = Stopwatch.StartNew();

        if (!Golan.Gdm.Core.AppPaths.DataDir.StartsWith(gdmHome, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"FAIL isolation: AppPaths.DataDir='{Golan.Gdm.Core.AppPaths.DataDir}' expected under '{gdmHome}'");
            return 99;
        }

        // 1. Run TDD self-tests
        var (p1, f1) = LayoutInspectorTests.RunAll();
        var (p2, f2) = RendererTests.RunAll(gdmHome);
        int passed = p1 + p2, failed = f1 + f2;
        Console.WriteLine($"TDD Summary: {passed} passed, {failed} failed");
        if (failed > 0) return 1;
        if (args.Any(a => string.Equals(a, "--tdd-only", StringComparison.OrdinalIgnoreCase))) return 0;

        // 2. Determine output directory (default: GDM/tests/render/out)
        var nonFlagArgs = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        string outDir = nonFlagArgs.Length > 0 && !string.IsNullOrWhiteSpace(nonFlagArgs[0])
            ? Path.GetFullPath(nonFlagArgs[0])
            : FindDefaultOutDir();

        Console.WriteLine($"Output directory: {outDir}");
        Directory.CreateDirectory(outDir);

        // 3. Render all windows & variants offscreen and run layout checks
        var outputs = WindowHarnesses.RenderAllVariants(outDir);
        HtmlReportGenerator.WriteReports(outDir, outputs);
        var beforeDir = Path.Combine(outDir, "before");
        if (Directory.Exists(beforeDir))
        {
            HtmlReportGenerator.WriteCompareReport(outDir, beforeDir, outputs);
        }

        int totalIssues = outputs.Sum(o => o.Issues.Count);
        Console.WriteLine($"Rendered {outputs.Count} window images in {sw.Elapsed.TotalSeconds:0.1}s.");
        Console.WriteLine($"Layout inspection found {totalIssues} total issue instances across {outputs.Count(o => o.Issues.Count > 0)} rendered views.");

        foreach (var byKind in outputs.SelectMany(o => o.Issues).GroupBy(i => i.Kind).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  - {byKind.Key}: {byKind.Count()}");
        }

        return 0;
    }

    static string FindDefaultOutDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RenderTest.csproj")))
                return Path.Combine(dir.FullName, "out");
            dir = dir.Parent;
        }
        return Path.Combine(Environment.CurrentDirectory, "out");
    }
}
