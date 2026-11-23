using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

/// <summary>Which assemblies a program's entry point reaches, by a rapid-type-analysis walk over IL: from Main,
/// every method a call, newobj, ldftn or jmp names; the static constructor of every type whose member is used; and
/// every virtual method of every type that is constructed (an over-approximation: an override is taken whether or
/// not its slot is called). Generic instantiations are not followed separately (a MethodSpec is its method). It is a
/// bound on what the IL front end's reachability can need, not that reachability.</summary>
static class Reach
{
    sealed class Asm
    {
        public required string Name;
        public required PEReader Pe;
        public required MetadataReader Md;
        public required Index Index;
    }

    static readonly Dictionary<string, Asm?> s_asms = [];
    static string s_dir = "";

    static Asm? Load(string name)
    {
        if (s_asms.TryGetValue(name, out var a)) return a;
        var path = Path.Combine(s_dir, name + ".dll");
        if (!File.Exists(path)) return s_asms[name] = null;
        var pe = new PEReader(File.OpenRead(path));
        return s_asms[name] = new Asm { Name = name, Pe = pe, Md = pe.GetMetadataReader(), Index = new Index(path) };
    }

    /// <summary>A type by name in an assembly, following its forwarders.</summary>
    static (Asm, TypeDefinitionHandle)? FindType(string asmName, string type, int depth = 0)
    {
        if (depth > 8 || Load(asmName) is not { } a) return null;
        if (a.Index.Types.TryGetValue(type, out var h)) return (a, h);
        int slash = type.IndexOf('/');
        var outer = slash < 0 ? type : type[..slash];
        foreach (var eh in a.Md.ExportedTypes)
        {
            var e = a.Md.GetExportedType(eh);
            var ns = a.Md.GetString(e.Namespace);
            if ((ns.Length > 0 ? ns + "." : "") + a.Md.GetString(e.Name) != outer || e.Implementation.Kind != HandleKind.AssemblyReference) continue;
            return FindType(a.Md.GetString(a.Md.GetAssemblyReference((AssemblyReferenceHandle)e.Implementation).Name), type, depth + 1);
        }
        return null;
    }

    static readonly HashSet<(string, MethodDefinitionHandle)> s_methods = [];
    static readonly HashSet<(string, TypeDefinitionHandle)> s_types = [];
    static readonly HashSet<(string, TypeDefinitionHandle)> s_constructed = [];
    static readonly Queue<(Asm, MethodDefinitionHandle)> s_work = new();
    static readonly SortedDictionary<string, int> s_unresolved = new(StringComparer.Ordinal);

    static void AddMethod(Asm a, MethodDefinitionHandle h)
    {
        if (!s_methods.Add((a.Name, h))) return;
        s_work.Enqueue((a, h));
        TouchType(a, a.Md.GetMethodDefinition(h).GetDeclaringType());
    }

    static void TouchType(Asm a, TypeDefinitionHandle t)
    {
        if (!s_types.Add((a.Name, t))) return;
        var td = a.Md.GetTypeDefinition(t);
        foreach (var mh in td.GetMethods())
            if (a.Md.GetString(a.Md.GetMethodDefinition(mh).Name) == ".cctor") AddMethod(a, mh);
        if (!td.GetDeclaringType().IsNil) TouchType(a, td.GetDeclaringType());
        if (ResolveTypeHandle(a, td.BaseType) is var (ba, bt)) TouchType(ba, bt);
    }

    static void Construct(Asm a, TypeDefinitionHandle t)
    {
        if (!s_constructed.Add((a.Name, t))) return;
        TouchType(a, t);
        var td = a.Md.GetTypeDefinition(t);
        foreach (var mh in td.GetMethods())
        {
            var m = a.Md.GetMethodDefinition(mh);
            if ((m.Attributes & MethodAttributes.Virtual) != 0 && (m.Attributes & MethodAttributes.Abstract) == 0) AddMethod(a, mh);
        }
        if (ResolveTypeHandle(a, td.BaseType) is var (ba, bt)) Construct(ba, bt);
    }

    static (Asm, TypeDefinitionHandle)? ResolveTypeHandle(Asm a, EntityHandle h)
    {
        if (h.IsNil) return null;
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition: return (a, (TypeDefinitionHandle)h);
            case HandleKind.TypeReference:
                {
                    var th = (TypeReferenceHandle)h;
                    var name = Meta.Name(a.Md, th);
                    var scope = Meta.Scope(a.Md, th) ?? a.Name;
                    var r = FindType(scope, name);
                    if (r == null) s_unresolved[scope + " " + name] = s_unresolved.GetValueOrDefault(scope + " " + name) + 1;
                    return r;
                }
            case HandleKind.TypeSpecification:
                {
                    var br = a.Md.GetBlobReader(a.Md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
                    if (br.ReadByte() != 0x15) return null;
                    br.ReadByte();
                    return ResolveTypeHandle(a, br.ReadTypeHandle());
                }
            default: return null;
        }
    }

    static void UseMethodToken(Asm a, EntityHandle h, bool isNew)
    {
        switch (h.Kind)
        {
            case HandleKind.MethodDefinition:
                {
                    var mh = (MethodDefinitionHandle)h;
                    AddMethod(a, mh);
                    if (isNew) Construct(a, a.Md.GetMethodDefinition(mh).GetDeclaringType());
                    break;
                }
            case HandleKind.MethodSpecification: UseMethodToken(a, a.Md.GetMethodSpecification((MethodSpecificationHandle)h).Method, isNew); break;
            case HandleKind.MemberReference:
                {
                    var m = a.Md.GetMemberReference((MemberReferenceHandle)h);
                    if (ResolveTypeHandle(a, m.Parent) is not var (ta, th)) return;
                    if (isNew) Construct(ta, th);
                    var name = a.Md.GetString(m.Name);
                    var sig = Meta.MethodSig(a.Md, m.Signature);
                    for (var cur = (ta, th); ;)
                    {
                        var (ca, ct) = cur;
                        var td = ca.Md.GetTypeDefinition(ct);
                        foreach (var mh in td.GetMethods())
                        {
                            var d = ca.Md.GetMethodDefinition(mh);
                            if (ca.Md.GetString(d.Name) == name && Meta.MethodSig(ca.Md, d.Signature) == sig) { AddMethod(ca, mh); return; }
                        }
                        if (ResolveTypeHandle(ca, td.BaseType) is not { } b) break;
                        cur = b;
                    }
                    s_unresolved["member " + Meta.Name(ta.Md, th) + "::" + name] = s_unresolved.GetValueOrDefault("member " + Meta.Name(ta.Md, th) + "::" + name) + 1;
                    break;
                }
        }
    }

    static void UseFieldToken(Asm a, EntityHandle h)
    {
        if (h.Kind == HandleKind.FieldDefinition) TouchType(a, a.Md.GetFieldDefinition((FieldDefinitionHandle)h).GetDeclaringType());
        else if (h.Kind == HandleKind.MemberReference && ResolveTypeHandle(a, a.Md.GetMemberReference((MemberReferenceHandle)h).Parent) is var (ta, th)) TouchType(ta, th);
    }

    static readonly Dictionary<short, OpCode> s_ops = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    static void Walk(Asm a, MethodDefinitionHandle mh)
    {
        var m = a.Md.GetMethodDefinition(mh);
        if (m.RelativeVirtualAddress == 0) return;
        var il = a.Pe.GetMethodBody(m.RelativeVirtualAddress).GetILReader();
        while (il.RemainingBytes > 0)
        {
            short v = il.ReadByte();
            if (v == 0xFE) v = (short)(0xFE00 | il.ReadByte());
            var op = s_ops[v];
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: il.ReadByte(); break;
                case OperandType.InlineVar: il.ReadInt16(); break;
                case OperandType.InlineBrTarget: case OperandType.InlineI: case OperandType.ShortInlineR: il.ReadInt32(); break;
                case OperandType.InlineI8: case OperandType.InlineR: il.ReadInt64(); break;
                case OperandType.InlineSwitch: { int n = il.ReadInt32(); for (int i = 0; i < n; i++) il.ReadInt32(); break; }
                case OperandType.InlineString: case OperandType.InlineSig: il.ReadInt32(); break;
                case OperandType.InlineMethod:
                    {
                        var h = MetadataTokens.EntityHandle(il.ReadInt32());
                        UseMethodToken(a, h, op == OpCodes.Newobj);
                        break;
                    }
                case OperandType.InlineField: UseFieldToken(a, MetadataTokens.EntityHandle(il.ReadInt32())); break;
                case OperandType.InlineType: case OperandType.InlineTok:
                    {
                        var h = MetadataTokens.EntityHandle(il.ReadInt32());
                        if (h.Kind is HandleKind.MethodDefinition or HandleKind.MemberReference or HandleKind.MethodSpecification)
                        {
                            if (h.Kind != HandleKind.MemberReference || a.Md.GetMemberReference((MemberReferenceHandle)h).GetKind() == MemberReferenceKind.Method) UseMethodToken(a, h, false);
                            else UseFieldToken(a, h);
                        }
                        else if (h.Kind == HandleKind.FieldDefinition) UseFieldToken(a, h);
                        // (a boxed value type is constructed: its virtual methods can be called)
                        else if (ResolveTypeHandle(a, h) is var (ta, th)) { if (op == OpCodes.Box) Construct(ta, th); else TouchType(ta, th); }
                        break;
                    }
                default: throw new ToolException("operand " + op.OperandType);
            }
        }
    }

    public static int Run(string program, string dir)
    {
        s_dir = dir;
        var pe = new PEReader(File.OpenRead(program));
        var md = pe.GetMetadataReader();
        var name = md.GetString(md.GetAssemblyDefinition().Name);
        var main = new Asm { Name = name, Pe = pe, Md = md, Index = new Index(program) };
        s_asms[name] = main;
        int ep = pe.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress;
        if (ep == 0) throw new ToolException(program + " has no entry point");
        AddMethod(main, MetadataTokens.MethodDefinitionHandle(ep & 0xFFFFFF));
        while (s_work.Count > 0) { var (a, h) = s_work.Dequeue(); Walk(a, h); }
        Console.WriteLine($"# reached from {name}'s entry point: {s_methods.Count} methods, {s_types.Count} types, {s_constructed.Count} constructed");
        foreach (var g in s_methods.GroupBy(m => m.Item1).OrderByDescending(g => g.Count()))
            Console.WriteLine($"{g.Key,-48} {g.Count(),6} methods {s_types.Count(t => t.Item1 == g.Key),5} types");
        Console.WriteLine($"# loaded but nothing reached: {string.Join(", ", s_asms.Where(kv => kv.Value != null && !s_methods.Any(m => m.Item1 == kv.Key)).Select(kv => kv.Key).Order())}");
        // the ways out of IL the walk reached: platform invokes, internal calls, host imports
        var outs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (an, mh) in s_methods)
        {
            var a = s_asms[an]!;
            var m = a.Md.GetMethodDefinition(mh);
            var who = an + ": " + Meta.Name(a.Md, m.GetDeclaringType()) + "::" + a.Md.GetString(m.Name);
            if (!m.GetImport().Module.IsNil) outs.Add("platform invoke " + a.Md.GetString(a.Md.GetModuleReference(m.GetImport().Module).Name) + "!" + a.Md.GetString(m.GetImport().Name) + "  (" + who + ")");
            else if ((m.ImplAttributes & MethodImplAttributes.InternalCall) != 0) outs.Add("internal call  " + who);
            else if (m.GetCustomAttributes().Any(c => Natives.AttrName(a.Md, c) == "Il2Wasm.Runtime.ImportAttribute")) outs.Add("host import  " + who);
        }
        Console.WriteLine($"# ways out of IL reached ({outs.Count}):");
        foreach (var s in outs) Console.WriteLine("  " + s);
        if (s_unresolved.Count > 0)
        {
            Console.WriteLine($"# unresolved ({s_unresolved.Count}):");
            foreach (var (k, n) in s_unresolved) Console.WriteLine($"  {k}  x{n}");
        }
        return 0;
    }
}
