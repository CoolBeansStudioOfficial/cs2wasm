using System.Diagnostics;
using System.Text;
using Cs2Wasm;
using Microsoft.CodeAnalysis;

/// <summary>
/// Godot proofs against stock Godot 4.7.2 with .NET (SPEC section 10, proof 3): each folder of tests/Godot with an
/// expected-calls.txt is a Godot project whose scripts are compiled with stock GodotSharp (its NuGet package as the
/// reference, the library from Godot's sources) and Godot's source generators, run on the stub engine
/// (host/godot-run.mjs), and whose engine-call log must equal expected-calls.txt, the log stock Godot wrote for the
/// same project (Oracle/CallLog.cs). Skipped when the stock packages are not on this machine.
/// </summary>
static class GodotChecks
{
    const string Version = "4.7.2";

    public static IEnumerable<(string name, string? failure, string? skip)> Run(string root, string outDir)
    {
        var dir = Path.Combine(root, "tests", "Godot");
        if (!Directory.Exists(dir)) yield break;
        var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var godotSharp = Path.Combine(nuget, "godotsharp", Version, "lib", "net8.0", "GodotSharp.dll");
        var generators = Path.Combine(nuget, "godot.sourcegenerators", Version, "analyzers", "dotnet", "cs", "Godot.SourceGenerators.dll");
        var cache = Environment.GetEnvironmentVariable("CS2WASM_CACHE") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cs2wasm");
        var api = Path.Combine(cache, "godot-" + Version, "extension_api.json");
        foreach (var project in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
        {
            var expectedFile = Path.Combine(project, "expected-calls.txt");
            if (!File.Exists(expectedFile)) continue;
            var name = "godot_" + Path.GetFileName(project);
            if (!File.Exists(godotSharp) || !File.Exists(generators)) { yield return (name, null, $"stock GodotSharp {Version} packages not in {nuget}"); continue; }
            if (!File.Exists(api) && !DumpApi(api)) { yield return (name, null, $"no extension_api.json at {api} (the stock editor writes it: --headless --dump-extension-api)"); continue; }
            yield return (name, Check(project, name, godotSharp, generators, api, outDir, File.ReadAllText(expectedFile).Replace("\r\n", "\n")), null);
        }
    }

    static string? Check(string project, string name, string godotSharp, string generators, string api, string outDir, string expected)
    {
        // the project's scripts (not the oracle's call log, which only the stock build compiles)
        var scripts = Directory.GetFiles(project, "*.cs", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).Select(f => (f, File.ReadAllText(f))).ToList();
        var comp = Frontend.CreateCompilation(name, scripts, [MetadataReference.CreateFromFile(godotSharp)]);
        // the properties Godot.NET.Sdk gives its generators; GodotPluginsInitializer writes stock Godot's start-up,
        // which cs2wasm's GodotPlugins replaces
        comp = Frontend.RunGenerators(comp, [generators], new Dictionary<string, string>
        {
            ["GodotProjectDir"] = project.Replace('\\', '/').TrimEnd('/') + "/",
            ["GodotDisabledSourceGenerators"] = "GodotPluginsInitializer",
        }, out var gdiags);
        if (gdiags.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } ge) return "source generators: " + ge;
        var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errs.Count > 0) return "C# errors:\n" + string.Join("\n", errs.Take(20));
        var result = Cs2WasmCompiler.Compile(comp, new CompileOptions());
        if (!result.Success) return "cs2wasm failed:\n" + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20));
        var wasm = Path.Combine(outDir, name + ".wasm");
        File.WriteAllBytes(wasm, result.Module!);
        var log = Path.Combine(outDir, name + ".calls.txt");
        var main = File.ReadAllText(Path.Combine(project, "main.tscn"));
        var script = System.Text.RegularExpressions.Regex.Match(main, "path=\"(res://[^\"]+\\.cs)\"").Groups[1].Value;
        var nodeClass = System.Text.RegularExpressions.Regex.Match(main, "\\[node name=\"[^\"]*\" type=\"([^\"]+)\"").Groups[1].Value;
        var psi = new ProcessStartInfo("node", [Path.GetFullPath(Path.Combine(project, "..", "..", "..", "host", "godot-run.mjs")), wasm, api, script, nodeClass, log])
        { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60000)) { p.Kill(); return "timeout"; }
        if (p.ExitCode != 0 || !File.Exists(log)) return $"stub engine run failed (exit {p.ExitCode}):\n{e.Result}{o.Result}";
        var actual = File.ReadAllText(log);
        if (actual == expected) return null;
        var a = expected.Split('\n'); var b = actual.Split('\n');
        var sb = new StringBuilder("engine calls differ from stock Godot's:\n");
        for (int i = 0, shown = 0; i < Math.Max(a.Length, b.Length) && shown < 20; i++)
        {
            var x = i < a.Length ? a[i] : "<missing>"; var y = i < b.Length ? b[i] : "<missing>";
            if (x != y) { sb.AppendLine($"line {i + 1}: stock «{x}»\n           module «{y}»"); shown++; }
        }
        return sb.ToString();
    }

    /// <summary>The engine's API description, from a stock editor (CS2WASM_GODOT_EDITOR or the standard install).</summary>
    static bool DumpApi(string api)
    {
        const string install = @"C:\Program Files\Godot";
        var editor = Environment.GetEnvironmentVariable("CS2WASM_GODOT_EDITOR") is { Length: > 0 } ed ? ed
            : Directory.Exists(install) ? Directory.EnumerateFiles(install, $"Godot_v{Version}-stable_mono_*_console.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (editor == null) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(api)!);
        var psi = new ProcessStartInfo(editor, ["--headless", "--dump-extension-api"]) { WorkingDirectory = Path.GetDirectoryName(api)!, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return File.Exists(api);
    }
}
