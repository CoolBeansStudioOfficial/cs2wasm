// il2wasm-il <main assembly> -o <module.wasm> [--checked] [--framework <dir>]
//
// Compiles a `dotnet build` output with the IL front end: the main assembly, the assemblies next to it (project and
// package references), and the framework: the IL CoreLib and the stock libraries above it, with the runtime's binding
// table of native libraries (il2wasm.natives.txt). The framework directory is --framework, else $IL2WASM_FRAMEWORK,
// else artifacts/framework of the repository the command line is in (tools/build-corelib.mjs makes it).
using Il2Wasm.IL.Lower;
using Il2Wasm.IL.Types;

string? input = null, output = null, framework = Environment.GetEnvironmentVariable("IL2WASM_FRAMEWORK");
bool isChecked = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o": output = args[++i]; break;
        case "--checked": isChecked = true; break;
        case "--framework": framework = args[++i]; break;
        default:
            if (args[i].StartsWith('-')) { Console.Error.WriteLine($"il2wasm-il: unknown option {args[i]}"); return 2; }
            input = args[i];
            break;
    }
}
if (input == null || output == null)
{
    Console.Error.WriteLine("usage: il2wasm-il <main assembly> -o <module.wasm> [--checked] [--framework <dir>]");
    return 2;
}
framework ??= FindFramework();
if (framework == null || !File.Exists(Path.Combine(framework, "System.Private.CoreLib.dll")))
{
    Console.Error.WriteLine($"il2wasm-il: no framework directory with System.Private.CoreLib.dll ({framework ?? "none found"}); give --framework or IL2WASM_FRAMEWORK");
    return 2;
}
try
{
    var ctx = new IlContext();
    var appDir = Path.GetDirectoryName(Path.GetFullPath(input))!;
    var frameworkFiles = Directory.GetFiles(framework, "*.dll");
    var names = frameworkFiles.Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    // (the framework's assemblies win over a copy next to the program)
    ctx.AddAssemblies(Directory.GetFiles(appDir, "*.dll").Where(f => !names.Contains(Path.GetFileNameWithoutExtension(f))).Concat(frameworkFiles).Append(Path.GetFullPath(input)));
    ctx.SetCoreLib();
    var main = ctx.Load(Path.GetFileNameWithoutExtension(input));
    var entry = main.EntryPoint ?? throw new InvalidOperationException($"{input} has no entry point");
    // (the project's feature switches: its runtimeconfig.json's configProperties)
    Il2Wasm.IL.Reach.Features.LoadRuntimeConfig(Path.ChangeExtension(Path.GetFullPath(input), ".runtimeconfig.json"));
    var c = new IlCompiler(ctx) { Checked = isChecked };
    var natives = Path.Combine(framework, "il2wasm.natives.txt");
    if (File.Exists(natives)) c.LoadNatives(natives);
    var bytes = c.Compile(entry);
    foreach (var w in c.Warnings.Distinct()) Console.Error.WriteLine("warning: " + w);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    File.WriteAllBytes(output, bytes);
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("il2wasm-il: error: " + e.Message);
    if (Environment.GetEnvironmentVariable("IL2WASM_IL_STACK") == "1") Console.Error.WriteLine(e);
    return 1;
}

static string? FindFramework()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "artifacts", "framework", "System.Private.CoreLib.dll"))) return Path.Combine(d.FullName, "artifacts", "framework");
    return null;
}
