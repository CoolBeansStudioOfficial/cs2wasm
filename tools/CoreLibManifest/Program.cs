// CoreLibManifest <runtime-root> <out-file>                       evaluate CoreLib's projects into the manifest
// CoreLibManifest resx <Strings.resx> <out.cs>                     generate the SR strings
// CoreLibManifest check <runtime-root> <manifest> [extra-dir]       compile the manifest as a corlib, summarize errors
// CoreLibManifest externs <runtime-root> <manifest> <corelib-dir>   the platform invokes the boundary does not implement
//
// Evaluates Mono's System.Private.CoreLib.csproj, with the shared projitems it imports (dotnet/runtime), for
// cs2wasm's target and writes the manifest the compiler builds the library from: the defines and the source files
// (relative to the runtime root), in project order. Only what those files use of MSBuild is implemented:
// properties with conditions, $(X) expansion, projitems imports, and Compile items (Include/Remove, conditions).
//
// The target: a 32-bit WASM build like Mono's browser one, with no hardware intrinsics and no event tracing. The
// runtime-specific half (Mono's src/mono/System.Private.CoreLib/src) is listed as `runtime`: cs2wasm supplies its
// own versions of those files, against its runtime (src/Cs2Wasm/CoreLib/Runtime).
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length >= 3 && args[0] == "resx") return Resx.Run(args[1], args[2]);
if (args.Length >= 4 && args[0] == "externs") return Externs.Run(args[1], args[2], args[3]);
if (args.Length >= 3 && args[0] == "check") return Check.Run(args[1], args[2], args.Length > 3 ? args[3] : null);
if (args.Length < 2) { Console.Error.WriteLine("usage: CoreLibManifest <runtime-root> <out-file> | resx <resx> <out> | check <runtime-root> <manifest> [extra-dir]"); return 2; }
return Manifest.Run(Path.GetFullPath(args[0]), args[1]);

static class Manifest
{
    static readonly Dictionary<string, string> props = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> files = [];
    static readonly SortedSet<string> unknown = [];
    // cs2wasm's choices: the projects may not override them
    static readonly HashSet<string> pinned = new(StringComparer.OrdinalIgnoreCase);

    public static int Run(string root, string output)
    {
        var monoDir = Path.Combine(root, "src", "mono", "System.Private.CoreLib");
        var sep = Path.DirectorySeparatorChar.ToString();
        props["CommonPath"] = Path.Combine(root, "src", "libraries", "Common", "src") + sep;
        props["LibrariesProjectRoot"] = Path.Combine(root, "src", "libraries") + sep;
        props["BclSourcesRoot"] = Path.Combine(monoDir, "src");
        props["Platform"] = "wasm";
        props["TargetArchitecture"] = "wasm";
        props["TargetOS"] = "browser";
        props["TargetsBrowser"] = "true";
        props["Is64Bit"] = "false";
        props["FeaturePerfTracing"] = "false";
        props["RuntimeFlavor"] = "Mono";
        // what the SDK defines for a net11.0 project
        props["DefineConstants"] = "";
        foreach (var k in props.Keys) pinned.Add(k);
        pinned.Remove("DefineConstants");
        Project(Path.Combine(monoDir, "System.Private.CoreLib.csproj"));

        // with what the SDK defines for a net11.0 project
        var sdk = "NET;NETCOREAPP;NET11_0;RELEASE;" + string.Join(";", Enumerable.Range(5, 7).Select(v => $"NET{v}_0_OR_GREATER"))
            + ";NETCOREAPP1_0_OR_GREATER;NETCOREAPP1_1_OR_GREATER;NETCOREAPP2_0_OR_GREATER;NETCOREAPP2_1_OR_GREATER;NETCOREAPP2_2_OR_GREATER;NETCOREAPP3_0_OR_GREATER;NETCOREAPP3_1_OR_GREATER;";
        var defines = (sdk + props["DefineConstants"]).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => d is not ("DEBUG" or "_LOGGING")).Distinct().ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# System.Private.CoreLib for cs2wasm, evaluated from dotnet/runtime's projects by tools/CoreLibManifest.");
        sb.AppendLine("# define <symbol> | file <path> (compiled) | runtime <path> (Mono's runtime half: cs2wasm supplies its own)");
        foreach (var d in defines) sb.AppendLine("define " + d);
        int missing = 0, runtime = 0, n = 0;
        foreach (var f in files.Distinct())
        {
            if (!f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(f)) { missing++; Console.Error.WriteLine("missing " + f); continue; }
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rel.StartsWith("src/mono/")) { runtime++; sb.AppendLine("runtime " + rel); }
            else { n++; sb.AppendLine("file " + rel); }
        }
        File.WriteAllText(output, sb.ToString());
        Console.WriteLine($"{n} files, {runtime} runtime, {missing} missing; defines: {string.Join(";", defines)}");
        foreach (var u in unknown) Console.WriteLine("condition not understood: " + u);
        return 0;
    }

    static string Expand(string s) => Regex.Replace(s, @"\$\(([A-Za-z_][A-Za-z0-9_]*)\)", m => props.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    static bool Cond(XElement e)
    {
        var c = (string?)e.Attribute("Condition");
        return c == null || Eval(c);
    }

    // comparisons of quoted strings, and/or/!, parentheses
    static bool Eval(string raw)
    {
        var s = Expand(raw);
        var toks = Regex.Matches(s, @"'[^']*'|==|!=|\(|\)|!|[^\s()!=']+").Select(m => m.Value).ToList();
        int pos = 0;
        string Val(string t) => t.StartsWith('\'') ? t.Trim('\'') : t;
        bool Or() { bool v = And(); while (pos < toks.Count && toks[pos].Equals("or", StringComparison.OrdinalIgnoreCase)) { pos++; bool r = And(); v = v || r; } return v; }
        bool And() { bool v = Unary(); while (pos < toks.Count && toks[pos].Equals("and", StringComparison.OrdinalIgnoreCase)) { pos++; bool r = Unary(); v = v && r; } return v; }
        bool Unary()
        {
            if (toks[pos] == "!") { pos++; return !Unary(); }
            if (toks[pos] == "(") { pos++; bool v = Or(); pos++; return v; }
            string a = Val(toks[pos++]);
            if (pos < toks.Count && toks[pos] is "==" or "!=")
            {
                var op = toks[pos++]; var b = Val(toks[pos++]);
                bool eq = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                return op == "==" ? eq : !eq;
            }
            unknown.Add(raw);
            return a.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        try { return Or(); } catch { unknown.Add(raw); return false; }
    }

    static void Project(string path)
    {
        var doc = XDocument.Load(path);
        var dir = Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar;
        props.TryGetValue("MSBuildThisFileDirectory", out var saved);
        props["MSBuildThisFileDirectory"] = dir;
        foreach (var el in doc.Root!.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "Import":
                    {
                        var ip = Expand((string?)el.Attribute("Project") ?? "");
                        if (ip.EndsWith(".projitems", StringComparison.OrdinalIgnoreCase) && Cond(el)) Project(Path.GetFullPath(ip));
                        props["MSBuildThisFileDirectory"] = dir;
                        break;
                    }
                case "PropertyGroup":
                    if (!Cond(el)) break;
                    foreach (var p in el.Elements()) if (!pinned.Contains(p.Name.LocalName) && Cond(p)) props[p.Name.LocalName] = Expand(p.Value);
                    break;
                case "ItemGroup":
                    if (!Cond(el)) break;
                    foreach (var it in el.Elements().Where(x => x.Name.LocalName == "Compile"))
                    {
                        if (!Cond(it)) continue;
                        if ((string?)it.Attribute("Include") is { } inc)
                            foreach (var f in inc.Split(';', StringSplitOptions.RemoveEmptyEntries)) files.Add(Path.GetFullPath(Expand(f.Trim())));
                        if ((string?)it.Attribute("Remove") is { } rem)
                            foreach (var f in rem.Split(';', StringSplitOptions.RemoveEmptyEntries)) files.Remove(Path.GetFullPath(Expand(f.Trim())));
                    }
                    break;
            }
        }
        props["MSBuildThisFileDirectory"] = saved ?? "";
    }
}
