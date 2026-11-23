using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

/// <summary>The native entry points: every MethodDef with an ImplMap (DllImport, and LibraryImport's generated inner
/// DllImport) in the stock libraries, by library; and what our CoreLib leaves without IL (extern methods by kind).</summary>
static class Natives
{
    public static int Run(string corelibPath, string packLib)
    {
        var byModule = new SortedDictionary<string, SortedDictionary<string, SortedSet<string>>>(StringComparer.Ordinal);
        int total = 0;
        foreach (var f in Directory.EnumerateFiles(packLib, "*.dll").Order(StringComparer.Ordinal).Append(corelibPath))
        {
            using var pe = new PEReader(File.OpenRead(f));
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            var who = Path.GetFileNameWithoutExtension(f) + (f == corelibPath ? " (ours)" : "");
            foreach (var th in md.TypeDefinitions)
                foreach (var mh in md.GetTypeDefinition(th).GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    var imp = m.GetImport();
                    if (f != corelibPath)
                    {
                        // the stock libraries' other ways out of IL: an extern without ImplMap (internal calls,
                        // runtime-implemented), and [JSImport] (the JavaScript interop the browser build uses)
                        var tdef = md.GetTypeDefinition(th);
                        bool isDelegate = !tdef.BaseType.IsNil && tdef.BaseType.Kind == HandleKind.TypeReference && Meta.Name(md, (TypeReferenceHandle)tdef.BaseType) == "System.MulticastDelegate";
                        string desc = who + ": " + Meta.Name(md, th) + "::" + md.GetString(m.Name) + " " + Meta.MethodSig(md, m.Signature).Replace("System.", "");
                        if (imp.Module.IsNil && m.RelativeVirtualAddress == 0 && (m.Attributes & MethodAttributes.Abstract) == 0 && !isDelegate)
                        {
                            // (an unsafe accessor: its kind and name, and the types [UnsafeAccessorType] names)
                            var acc = new List<string>();
                            foreach (var ca in m.GetCustomAttributes().Concat(m.GetParameters().SelectMany(p => md.GetParameter(p).GetCustomAttributes())))
                                if (AttrName(md, ca) is "System.Runtime.CompilerServices.UnsafeAccessorAttribute" or "System.Runtime.CompilerServices.UnsafeAccessorTypeAttribute" && md.GetCustomAttribute(ca).DecodeValue(new StringArgs()) is var v)
                                    acc.Add(string.Join(" ", v.FixedArguments.Select(a => a.Value)) + string.Concat(v.NamedArguments.Select(a => " " + a.Name + "=" + a.Value)));
                            Other.Add(((m.ImplAttributes & MethodImplAttributes.InternalCall) != 0 ? "internal call " : acc.Count > 0 ? "unsafe accessor " : "extern ") + desc + (acc.Count > 0 ? "  {" + string.Join("; ", acc) + "}" : ""));
                        }
                        foreach (var ca in m.GetCustomAttributes())
                            if (AttrName(md, ca) == "System.Runtime.InteropServices.JavaScript.JSImportAttribute")
                            {
                                var v = md.GetCustomAttribute(ca).DecodeValue(new StringArgs());
                                JsImports.Add(desc + "  -> " + string.Join(" ", v.FixedArguments.Select(a => a.Value)));
                            }
                    }
                    if (imp.Module.IsNil) continue;
                    var module = md.GetString(md.GetModuleReference(imp.Module).Name);
                    var entry = md.GetString(imp.Name);
                    if (entry.Length == 0) entry = md.GetString(m.Name);
                    if (!byModule.TryGetValue(module, out var entries)) byModule[module] = entries = new(StringComparer.Ordinal);
                    var key = entry + "  " + Meta.MethodSig(md, m.Signature).Replace("System.", "");
                    if (!entries.TryGetValue(key, out var users)) entries[key] = users = new(StringComparer.Ordinal);
                    users.Add(who);
                    total++;
                }
        }
        Console.WriteLine($"# native entry points (ImplMap rows) of {packLib} and {corelibPath}: {total} methods, {byModule.Sum(m => m.Value.Count)} distinct (library, entry, signature)");
        foreach (var (module, entries) in byModule)
        {
            Console.WriteLine();
            Console.WriteLine($"## {module} ({entries.Count} entry points)");
            foreach (var (e, users) in entries) Console.WriteLine($"{e}    [{string.Join(", ", users)}]");
        }

        Console.WriteLine();
        Console.WriteLine($"# the stock libraries' methods without IL and without ImplMap (delegates aside): {Other.Count}");
        foreach (var s in Other.Order(StringComparer.Ordinal)) Console.WriteLine(s);
        Console.WriteLine();
        Console.WriteLine($"# the stock libraries' [JSImport] methods (served by the browser build's JavaScript): {JsImports.Count}");
        foreach (var s in JsImports.Order(StringComparer.Ordinal)) Console.WriteLine(s);

        // our CoreLib: the methods with no IL, by what supplies them
        using var cpe = new PEReader(File.OpenRead(corelibPath));
        var cmd = cpe.GetMetadataReader();
        var kinds = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var th in cmd.TypeDefinitions)
        {
            var t = cmd.GetTypeDefinition(th);
            bool isDelegate = !t.BaseType.IsNil && t.BaseType.Kind == HandleKind.TypeDefinition && Meta.Name(cmd, (TypeDefinitionHandle)t.BaseType) == "System.MulticastDelegate";
            foreach (var mh in t.GetMethods())
            {
                var m = cmd.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress != 0 || (m.Attributes & MethodAttributes.Abstract) != 0) continue;
                var attrs = m.GetCustomAttributes().Select(a => AttrName(cmd, a)).ToList();
                string kind =
                    attrs.Contains("Il2Wasm.Runtime.IntrinsicAttribute") ? "il2wasm intrinsic ([Il2Wasm.Runtime.Intrinsic])"
                    : attrs.Contains("Il2Wasm.Runtime.ImportAttribute") ? "host import ([Il2Wasm.Runtime.Import])"
                    : !m.GetImport().Module.IsNil ? "platform invoke (ImplMap)"
                    : (m.ImplAttributes & MethodImplAttributes.InternalCall) != 0 ? "internal call (MethodImplOptions.InternalCall)"
                    : (m.ImplAttributes & MethodImplAttributes.Runtime) != 0 && isDelegate ? "delegate (runtime-implemented)"
                    : (m.ImplAttributes & MethodImplAttributes.Runtime) != 0 ? "runtime-implemented"
                    : attrs.Contains("System.Runtime.CompilerServices.UnsafeAccessorAttribute") ? "unsafe accessor"
                    : attrs.Contains("Il2Wasm.Runtime.MissingCapabilityAttribute") ? "missing capability"
                    : "other extern";
                if (kind.StartsWith("delegate")) { kinds[kind] = kinds.GetValueOrDefault(kind) ?? []; kinds[kind].Add(""); continue; }
                if (!kinds.TryGetValue(kind, out var list)) kinds[kind] = list = [];
                list.Add(Meta.Name(cmd, th) + "::" + cmd.GetString(m.Name) + " " + Meta.MethodSig(cmd, m.Signature));
            }
        }
        Console.WriteLine();
        Console.WriteLine("# our CoreLib's methods without IL, by kind");
        foreach (var (k, list) in kinds)
        {
            Console.WriteLine();
            Console.WriteLine($"## {k}: {list.Count}");
            if (!k.StartsWith("delegate")) foreach (var s in list.Order(StringComparer.Ordinal)) Console.WriteLine(s);
        }
        return 0;
    }

    static readonly List<string> Other = [], JsImports = [];

    /// <summary>Custom attribute arguments, for JSImport's strings (enums and types as text).</summary>
    sealed class StringArgs : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string e) => e + "[]";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => Meta.Name(r, h);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => Meta.Name(r, h);
        public string GetTypeFromSerializedName(string n) => n;
        public PrimitiveTypeCode GetUnderlyingEnumType(string t) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string t) => t == "System.Type";
    }

    public static string AttrName(MetadataReader md, CustomAttributeHandle h)
    {
        var a = md.GetCustomAttribute(h);
        EntityHandle t = a.Constructor.Kind switch
        {
            HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor).GetDeclaringType(),
            HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent,
            _ => default,
        };
        return t.Kind switch
        {
            HandleKind.TypeDefinition => Meta.Name(md, (TypeDefinitionHandle)t),
            HandleKind.TypeReference => Meta.Name(md, (TypeReferenceHandle)t),
            _ => "",
        };
    }
}
