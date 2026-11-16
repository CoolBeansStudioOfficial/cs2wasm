using System.Text;
using System.Xml.Linq;

// CoreLibManifest roslyn <roslyn-root> <upstream-dir>: manifests for Roslyn's compiler libraries (Microsoft.CodeAnalysis
// and Microsoft.CodeAnalysis.CSharp, section 9: the compiler compiles itself, Roslyn included), from dotnet/roslyn's
// projects at the version cs2wasm is built with. Their files are relative to the Roslyn sources (`root roslyn-<version>`),
// the projects' default globs plus their shared projects; the resource classes are generated (constants, and a
// ResourceManager over the same table for lookups by name), with the InternalsVisibleTo the build would write.
static class RoslynLibraries
{
    public const string Version = "5.12.0";
    public const string Commit = "1324e1d34a0490454837a22370e75bbaeecffb87";

    static readonly (string Lib, string Dir, string Project, string Resx, string Ns, string[] Friends)[] Projects =
    [
        ("Microsoft.CodeAnalysis", "src/Compilers/Core/Portable", "Microsoft.CodeAnalysis.csproj", "CodeAnalysisResources.resx", "Microsoft.CodeAnalysis", ["Microsoft.CodeAnalysis.CSharp"]),
        ("Microsoft.CodeAnalysis.CSharp", "src/Compilers/CSharp/Portable", "Microsoft.CodeAnalysis.CSharp.csproj", "CSharpResources.resx", "Microsoft.CodeAnalysis.CSharp", []),
    ];

    public static int Run(string roslynRoot, string upstream)
    {
        foreach (var (lib, dir, project, resx, ns, friends) in Projects)
        {
            var projDir = Path.Combine(roslynRoot, dir);
            var files = new List<string>();
            var defines = new List<string> { "NET", "NETCOREAPP", "NET11_0", "RELEASE" };
            defines.AddRange(Enumerable.Range(5, 7).Select(v => $"NET{v}_0_OR_GREATER"));
            defines.AddRange(["NETCOREAPP1_0_OR_GREATER", "NETCOREAPP1_1_OR_GREATER", "NETCOREAPP2_0_OR_GREATER", "NETCOREAPP2_1_OR_GREATER", "NETCOREAPP2_2_OR_GREATER", "NETCOREAPP3_0_OR_GREATER", "NETCOREAPP3_1_OR_GREATER"]);
            var doc = XDocument.Load(Path.Combine(projDir, project));
            foreach (var dc in doc.Descendants("DefineConstants"))
                defines.AddRange(dc.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(d => !d.StartsWith('$')));
            // the SDK's default glob (the generator's outputs under Generated/ included: cs2wasm does not run generators)
            files.AddRange(Directory.EnumerateFiles(projDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)));
            // (PerformanceSensitiveAttribute.cs is removed only where an analyzer package supplies the attribute)
            foreach (var imp in doc.Descendants("Import").Select(i => (string?)i.Attribute("Project")).OfType<string>().Where(p => p.EndsWith(".projitems")))
            {
                var pi = Path.GetFullPath(Path.Combine(projDir, imp.Replace('\\', Path.DirectorySeparatorChar)));
                var piDir = Path.GetDirectoryName(pi)! + Path.DirectorySeparatorChar;
                foreach (var c in XDocument.Load(pi).Descendants().Where(e => e.Name.LocalName == "Compile"))
                {
                    var inc = ((string?)c.Attribute("Include") ?? "").Replace("$(MSBuildThisFileDirectory)", piDir).Replace('\\', Path.DirectorySeparatorChar);
                    if (inc.Contains('*')) files.AddRange(Directory.EnumerateFiles(piDir, "*.cs", SearchOption.AllDirectories));
                    else if (inc.EndsWith(".cs")) files.Add(Path.GetFullPath(inc));
                }
            }
            var rel = files.Select(f => Path.GetRelativePath(roslynRoot, f).Replace('\\', '/')).Distinct().Order(StringComparer.Ordinal).ToList();
            var outDir = Path.Combine(upstream, lib);
            Directory.CreateDirectory(Path.Combine(outDir, "Generated"));
            var sb = new StringBuilder();
            sb.AppendLine($"# {lib} for cs2wasm, from dotnet/roslyn's project ({Version}, {Commit}) by tools/CoreLibManifest roslyn.");
            sb.AppendLine("# root <dir> (the sources' root under the cache) | define <symbol> | file <path>");
            sb.AppendLine($"root roslyn-{Version}");
            sb.AppendLine("nullable enable");
            foreach (var d in defines.Distinct()) sb.AppendLine("define " + d);
            foreach (var f in rel) sb.AppendLine("file " + f);
            // the project's embedded resources, by the names the build gives them (root namespace, folders, file; a .resx
            // compiled to .resources)
            var rootNs = doc.Descendants("RootNamespace").FirstOrDefault()?.Value ?? lib;
            foreach (var er in doc.Descendants("EmbeddedResource"))
            {
                var item = ((string?)er.Attribute("Include") ?? (string?)er.Attribute("Update") ?? "").Replace('\\', '/');
                if (item.Length == 0 || item.Contains('*')) continue;
                var name = rootNs + "." + (item.EndsWith(".resx") ? item[..^5] + ".resources" : item).Replace('/', '.');
                sb.AppendLine($"resource {name} {Path.GetRelativePath(roslynRoot, Path.Combine(projDir, item)).Replace('\\', '/')}");
            }
            File.WriteAllText(Path.Combine(outDir, "manifest.txt"), sb.ToString());
            WriteResources(Path.Combine(projDir, resx), Path.Combine(outDir, "Generated", Path.GetFileNameWithoutExtension(resx) + ".g.cs"), ns, Path.GetFileNameWithoutExtension(resx));
            var ai = new StringBuilder("// <auto-generated> what the build writes for the project (tools/CoreLibManifest roslyn) </auto-generated>\n");
            foreach (var fr in friends) ai.AppendLine($"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{fr}\")]");
            File.WriteAllText(Path.Combine(outDir, "Generated", "AssemblyInfo.g.cs"), ai.ToString());
            Console.WriteLine($"{lib}: {rel.Count} files");
        }
        return 0;
    }

    static void WriteResources(string resx, string output, string ns, string cls)
    {
        var doc = XDocument.Load(resx);
        var entries = doc.Root!.Elements("data").Where(d => d.Attribute("type") == null && (string?)d.Attribute("name") != null)
            .Select(d => ((string)d.Attribute("name")!, (string?)d.Element("value") ?? "")).ToList();
        string Lit(string s) => "@\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine($"// <auto-generated> from dotnet/roslyn's {Path.GetFileName(resx)} by tools/CoreLibManifest roslyn: the strings as constants (one");
        sb.AppendLine("// culture, invariant), and a ResourceManager over the same table for lookups by name. </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine($"    internal static partial class {cls}");
        sb.AppendLine("    {");
        sb.AppendLine("        private static global::System.Resources.ResourceManager? s_resourceManager;");
        sb.AppendLine("        internal static global::System.Resources.ResourceManager ResourceManager => s_resourceManager ??= new Table();");
        sb.AppendLine("        internal static global::System.Globalization.CultureInfo? Culture { get; set; }");
        sb.AppendLine("        internal static string? GetResourceString(string resourceKey, string? defaultValue = null) => ResourceManager.GetString(resourceKey, Culture) ?? defaultValue;");
        foreach (var (name, value) in entries) sb.AppendLine($"        internal const string @{name} = {Lit(value)};");
        sb.AppendLine("        private sealed class Table : global::System.Resources.ResourceManager");
        sb.AppendLine("        {");
        sb.AppendLine("            public override string? GetString(string name) => GetString(name, null);");
        sb.AppendLine("            public override string? GetString(string name, global::System.Globalization.CultureInfo? culture) => name switch");
        sb.AppendLine("            {");
        foreach (var (name, _) in entries) sb.AppendLine($"                \"{name}\" => @{name},");
        sb.AppendLine("                _ => null,");
        sb.AppendLine("            };");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(output, sb.ToString());
        Console.WriteLine($"  {entries.Count} strings in {cls}");
    }
}
