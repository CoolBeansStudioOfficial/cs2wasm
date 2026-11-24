using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

/// <summary>`bindings [DIR]`: il2wasm.natives.txt against the framework directory. Every row's target exists in
/// CoreLib with the same WASM signature as what it binds (pointers, byrefs, IntPtr and function pointers are i32;
/// an enum is its underlying type), and every P/Invoke and internal call of the stock libraries is bound, or its
/// library is one the runtime does not provide (reported: those throw DllNotFoundException when called).</summary>
static class Bindings
{
    /// <summary>A method's signature as WASM value types, "i32 i64 -> i32".</summary>
    sealed class Wasm(Func<MetadataReader, EntityHandle, string> valueType) : ISignatureTypeProvider<string, MetadataReader>
    {
        public string GetPrimitiveType(PrimitiveTypeCode t) => t switch
        {
            PrimitiveTypeCode.Void => "void",
            PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 => "i64",
            PrimitiveTypeCode.Single => "f32",
            PrimitiveTypeCode.Double => "f64",
            PrimitiveTypeCode.Object or PrimitiveTypeCode.String or PrimitiveTypeCode.TypedReference => "ref:" + t,
            _ => "i32",
        };
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => k == (byte)SignatureTypeKind.ValueType ? valueType(r, h) : "ref:" + Meta.Name(r, h);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => k == (byte)SignatureTypeKind.ValueType ? valueType(r, h) : "ref:" + Meta.Name(r, h);
        public string GetTypeFromSpecification(MetadataReader r, MetadataReader c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
        public string GetSZArrayType(string e) => "ref:array";
        public string GetArrayType(string e, ArrayShape s) => "ref:array";
        public string GetByReferenceType(string e) => "i32";
        public string GetPointerType(string e) => "i32";
        public string GetPinnedType(string e) => e;
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) => "struct:generic";
        public string GetGenericTypeParameter(MetadataReader c, int i) => "generic";
        public string GetGenericMethodParameter(MetadataReader c, int i) => "generic";
        public string GetModifiedType(string m, string u, bool req) => u;
        public string GetFunctionPointerType(MethodSignature<string> s) => "i32";
    }

    static readonly Dictionary<string, (PEReader Pe, MetadataReader Md)?> s_asms = [];
    static string s_dir = "";

    static (PEReader Pe, MetadataReader Md)? Load(string name)
    {
        if (s_asms.TryGetValue(name, out var a)) return a;
        var p = Path.Combine(s_dir, name + ".dll");
        if (!File.Exists(p)) return s_asms[name] = null;
        var pe = new PEReader(File.OpenRead(p));
        return s_asms[name] = (pe, pe.GetMetadataReader());
    }

    static (MetadataReader, TypeDefinitionHandle)? Resolve(MetadataReader r, EntityHandle h, int depth = 0)
    {
        if (h.Kind == HandleKind.TypeDefinition) return (r, (TypeDefinitionHandle)h);
        if (h.Kind != HandleKind.TypeReference || depth > 8) return null;
        var name = Meta.Name(r, (TypeReferenceHandle)h);
        var scope = Meta.Scope(r, (TypeReferenceHandle)h);
        if (scope == null || Load(scope) is not var (_, md)) return null;
        foreach (var t in md.TypeDefinitions) if (Meta.Name(md, t) == name) return (md, t);
        int slash = name.IndexOf('/');
        var outer = slash < 0 ? name : name[..slash];
        foreach (var eh in md.ExportedTypes)
        {
            var e = md.GetExportedType(eh);
            var ns = md.GetString(e.Namespace);
            if ((ns.Length > 0 ? ns + "." : "") + md.GetString(e.Name) != outer || e.Implementation.Kind != HandleKind.AssemblyReference) continue;
            var target = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)e.Implementation).Name);
            if (Load(target) is not var (_, tmd)) return null;
            foreach (var t in tmd.TypeDefinitions) if (Meta.Name(tmd, t) == name) return (tmd, t);
        }
        return null;
    }

    /// <summary>A value type in a signature: an enum is its underlying type; IntPtr/UIntPtr i32; a primitive struct
    /// its primitive; any other struct by name (a struct passed by value is not a WASM value).</summary>
    static string ValueType(MetadataReader r, EntityHandle h)
    {
        if (Resolve(r, h) is not var (md, th)) return "struct:?" + (h.Kind == HandleKind.TypeReference ? Meta.Name(r, (TypeReferenceHandle)h) : "");
        var name = Meta.Name(md, th);
        switch (name)
        {
            case "System.IntPtr" or "System.UIntPtr" or "System.Int32" or "System.UInt32" or "System.Int16" or "System.UInt16" or "System.Byte" or "System.SByte" or "System.Boolean" or "System.Char": return "i32";
            case "System.Int64" or "System.UInt64": return "i64";
            case "System.Single": return "f32";
            case "System.Double": return "f64";
        }
        var td = md.GetTypeDefinition(th);
        bool isEnum = !td.BaseType.IsNil && (td.BaseType.Kind == HandleKind.TypeReference ? Meta.Name(md, (TypeReferenceHandle)td.BaseType) : td.BaseType.Kind == HandleKind.TypeDefinition ? Meta.Name(md, (TypeDefinitionHandle)td.BaseType) : "") == "System.Enum";
        if (isEnum)
            foreach (var fh in td.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                if ((f.Attributes & FieldAttributes.Static) != 0) continue;
                var br = md.GetBlobReader(f.Signature);
                return new SignatureDecoder<string, MetadataReader>(new Wasm(ValueType), md, md).DecodeFieldSignature(ref br);
            }
        return "struct:" + name;
    }

    static string Sig(MetadataReader md, MethodDefinition m, bool withThis)
    {
        var br = md.GetBlobReader(m.Signature);
        var s = new SignatureDecoder<string, MetadataReader>(new Wasm(ValueType), md, md).DecodeMethodSignature(ref br);
        var ps = s.ParameterTypes.ToList();
        if (withThis && s.Header.IsInstance) ps.Insert(0, "i32");
        return string.Join(" ", ps) + " -> " + s.ReturnType;
    }

    public static int Run(string dir)
    {
        s_dir = dir;
        var table = File.ReadAllLines(Path.Combine(dir, "il2wasm.natives.txt")).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t')).ToList();
        var libraries = table.Where(p => p[0] == "library").Select(p => p[1]).ToHashSet();
        var pinvokes = table.Where(p => p[0] == "pinvoke").ToDictionary(p => (p[1], p[2]), p => (p[3], p[4]));
        var icalls = table.Where(p => p[0] == "internalcall").ToDictionary(p => (p[1], p[2], p[3]), p => (p[4], p[5]));
        var (_, core) = Load("System.Private.CoreLib")!.Value;
        int bad = 0, bound = 0;
        var unbound = new SortedSet<string>(StringComparer.Ordinal);

        string? TargetSig((string Type, string Method) t, out string error)
        {
            error = "";
            var th = core.TypeDefinitions.FirstOrDefault(h => Meta.Name(core, h) == t.Type);
            if (th.IsNil) { error = "no type " + t.Type; return null; }
            var ms = core.GetTypeDefinition(th).GetMethods().Select(core.GetMethodDefinition).Where(m => core.GetString(m.Name) == t.Method).ToList();
            if (ms.Count != 1) { error = $"{ms.Count} methods {t.Type}::{t.Method}"; return null; }
            if ((ms[0].Attributes & MethodAttributes.Static) == 0) { error = "not static: " + t.Type + "::" + t.Method; return null; }
            return Sig(core, ms[0], false);
        }

        foreach (var f in Directory.EnumerateFiles(dir, "*.dll").Order(StringComparer.Ordinal))
        {
            var asm = Path.GetFileNameWithoutExtension(f);
            if (Load(asm) is not var (_, md) || asm == "System.Private.CoreLib") continue;
            foreach (var th in md.TypeDefinitions)
                foreach (var mh in md.GetTypeDefinition(th).GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    var imp = m.GetImport();
                    string who = $"{asm}: {Meta.Name(md, th)}::{md.GetString(m.Name)}";
                    (string, string) target;
                    string what;
                    if (!imp.Module.IsNil)
                    {
                        var lib = md.GetString(md.GetModuleReference(imp.Module).Name);
                        var entry = md.GetString(imp.Name);
                        if (entry.Length == 0) entry = md.GetString(m.Name);
                        what = lib + "!" + entry;
                        if (!pinvokes.TryGetValue((lib, entry), out target))
                        {
                            unbound.Add((libraries.Contains(lib) ? "EntryPointNotFoundException  " : "DllNotFoundException  ") + what + "  (" + who + ")");
                            continue;
                        }
                    }
                    else if ((m.ImplAttributes & MethodImplAttributes.InternalCall) != 0)
                    {
                        what = "internal call";
                        if (!icalls.TryGetValue((asm, Meta.Name(md, th), md.GetString(m.Name)), out target)) { unbound.Add("internal call not bound  (" + who + ")"); continue; }
                    }
                    else continue;
                    var want = Sig(md, m, true);
                    var have = TargetSig(target, out var err);
                    if (have == null) { Console.WriteLine($"BAD  {who} ({what}): {err}"); bad++; continue; }
                    if (have != want) { Console.WriteLine($"BAD  {who} ({what}): wants {want}, {target.Item1}::{target.Item2} is {have}"); bad++; continue; }
                    bound++;
                }
        }
        // (rows nothing uses are fine: a package's library may use them; a row whose target is missing is not)
        foreach (var t in pinvokes.Values.Concat(icalls.Values).Distinct())
            if (TargetSig(t, out var err) == null) { Console.WriteLine("BAD  row target: " + err); bad++; }
        Console.WriteLine($"# {bound} stock P/Invokes and internal calls bound with matching signatures, {bad} bad, {unbound.Count} not bound");
        foreach (var u in unbound) Console.WriteLine("  " + u);
        return bad == 0 ? 0 : 1;
    }
}
