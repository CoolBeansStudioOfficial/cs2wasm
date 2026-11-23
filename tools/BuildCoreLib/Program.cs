// tools/BuildCoreLib: il2wasm's framework, ahead of time (SPEC 9, docs/IL-FRONTEND.md step 1, docs/CORELIB.md).
//
//   build  [--out DIR]   System.Private.CoreLib.dll + .pdb from dotnet/runtime v11.0.0 and the manifest, then the
//                        framework directory: that CoreLib, the stock IL of the browser-wasm runtime pack, and
//                        il2wasm.substitutions.txt. Default DIR: artifacts/framework (under the repository).
//   refs   [DIR]         every TypeRef/MemberRef the stock libraries make into System.Private.CoreLib, checked
//                        against DIR's CoreLib (default: the framework directory); `refs --stock` checks the pack's own.
//   natives [DIR]        every native entry point (ImplMap) of the stock libraries and the extern methods left in
//                        DIR's CoreLib, grouped by library.
//   reach  DLL [DIR]     the assemblies a program's entry point reaches (a type-and-call walk over IL).
//
// Sources: IL2WASM_RUNTIME_SOURCES, else the compiler's cache (IL2WASM_CACHE or %LOCALAPPDATA%/il2wasm, then
// runtime-v11.0.0), fetched by a sparse clone as the compiler does when files are missing.

using System.Reflection;

static class Program
{
    public static string RepoRoot = FindRepoRoot();

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "il2wasm.sln"))) return d.FullName;
        for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "il2wasm.sln"))) return d.FullName;
        throw new Exception("il2wasm.sln not found above the tool or the current directory");
    }

    public static string Meta(string key) =>
        typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value!;

    public static string PackDir => Path.Combine(Meta("NuGetPackageRoot"), Meta("BrowserPackId").ToLowerInvariant(), Meta("BrowserPackVersion"));
    public static string PackLib => Path.Combine(PackDir, "runtimes", "browser-wasm", "lib", "net11.0");
    public static string PackCoreLib => Path.Combine(PackDir, "runtimes", "browser-wasm", "native", "System.Private.CoreLib.dll");
    public static string DefaultOut => Path.Combine(RepoRoot, "artifacts", "framework");

    static int Main(string[] args)
    {
        try
        {
            string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var rest = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--out") { i++; continue; }
                if (!args[i].StartsWith("--")) rest.Add(args[i]);
            }
            switch (args.FirstOrDefault())
            {
                case "build": return Build.Run(Opt("--out") ?? DefaultOut);
                case "refs": return RefCheck.Run(args.Contains("--stock") ? PackCoreLib : Path.Combine(rest.FirstOrDefault() ?? DefaultOut, "System.Private.CoreLib.dll"), PackLib);
                case "natives": return Natives.Run(Path.Combine(rest.FirstOrDefault() ?? DefaultOut, "System.Private.CoreLib.dll"), PackLib);
                case "reach": return Reach.Run(rest[0], rest.Count > 1 ? rest[1] : DefaultOut);
                default:
                    Console.Error.WriteLine("usage: BuildCoreLib build [--out DIR] | refs [DIR] [--stock] | natives [DIR] | reach PROGRAM.dll [DIR]");
                    return 2;
            }
        }
        catch (ToolException e) { Console.Error.WriteLine("BuildCoreLib: " + e.Message); return 1; }
    }
}

sealed class ToolException(string message) : Exception(message) { }
