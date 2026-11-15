using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Reflection metadata (section 8), kept for what the program's reflection calls can be proven to reach: the trimming
/// analysis. `typeof(X).GetMethod("M")` keeps X.M; `GetMethods()` on `typeof(X)` keeps all of X's methods; a type
/// marked [DynamicallyAccessedMembers] keeps everything; reflection on a Type the compiler cannot see is warning
/// CW0201. Kept members get a record in the data segment and a thunk (target, object[] args) -> object that unboxes
/// the arguments, calls the member and boxes the result; the library's MemberInfo classes read the records.
/// Programs that do not reflect pay nothing.
/// </summary>
sealed class ReflectionKeep
{
    public readonly Dictionary<string, (INamedTypeSymbol type, bool all, HashSet<string> names, bool ctors)> Types = [];
    public void Keep(INamedTypeSymbol t, string? name, bool ctors = false)
    {
        var k = Compiler.Types.Key(t);
        if (!Types.TryGetValue(k, out var e)) e = (t, false, [], false);
        if (name == null && !ctors) e.all = true;
        else if (name != null) e.names.Add(name);
        if (ctors) e.ctors = true;
        Types[k] = e;
    }
}

internal sealed partial class Driver
{
    ReflectionKeep? _reflect;
    readonly Dictionary<string, WasmFunction> _reflThunks = [];
    WasmFunction? _metaOf;
    public WasmFunction MetaOf => _metaOf ??= Module.AddFunction("rt.meta_of", new FuncType([ValType.I32], [ValType.I32]));
    public static readonly FuncType ThunkType = new([ValType.I32, ValType.I32], [ValType.I32]);

    static readonly HashSet<string> s_reflectNames = ["GetMethod", "GetMethods", "GetProperty", "GetProperties", "GetField", "GetFields", "GetEvent", "GetEvents",
        "GetConstructor", "GetConstructors", "GetMember", "GetMembers", "GetCustomAttributes", "GetCustomAttribute", "IsDefined", "GetInterfaces"];

    /// <summary>Finds the reflection the program does (FinishExports, before the last compile round).</summary>
    internal void AnalyzeReflection()
    {
        var keep = new ReflectionKeep();
        foreach (var mi in AllMethods.ToList())
        {
            if (!mi.Compiled || mi.Impl != MethodImpl.Body) continue;
            var def = mi.Sym.OriginalDefinition;
            var map = TypeMap.For(mi.Sym);
            foreach (var r in def.DeclaringSyntaxReferences)
            {
                // (the library's reflection too: System.Linq.Expressions asks delegate types for Invoke)
                bool input = IsInput(r.SyntaxTree);
                if (!input && !r.GetSyntax().DescendantTokens().Any(t => t.ValueText is "GetMethod" or "GetProperty" or "GetField" or "GetMember" or "GetConstructor" or "GetConstructors")) continue;
                var body = ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax());
                if (body == null) continue;
                foreach (var op in body.DescendantsAndSelf().OfType<IInvocationOperation>()) Visit(op, map, keep, input);
            }
        }
        // types marked [DynamicallyAccessedMembers] keep everything
        foreach (var comp in Inputs)
            foreach (var t in AllTypes(comp.Assembly.GlobalNamespace))
                if (t.GetAttributes().Any(a => a.AttributeClass?.Name == "DynamicallyAccessedMembersAttribute")) { keep.Keep(t, null); keep.Keep(t, null, ctors: true); }
        foreach (var (t, name, ctors) in _keepFromTrees) keep.Keep(t, name, ctors);
        if (keep.Types.Count == 0) return;
        _reflect = keep;
        // thunks for the kept members (compiled in the next round)
        foreach (var (t, all, names, ctors) in keep.Types.Values.ToList())
            foreach (var m in KeptMembers(t, all, names, ctors)) ThunkFor(m);
    }

    void Visit(IInvocationOperation op, TypeMap map, ReflectionKeep keep, bool input = true)
    {
        var m = op.TargetMethod;
        var owner = m.ContainingType?.Name;
        if (owner == "Activator" && m.Name == "CreateInstance")
        {
            if (m.IsGenericMethod && Types.Resolve(m.TypeArguments[0], map) is INamedTypeSymbol gt) { keep.Keep(gt, null, ctors: true); return; }
            if (op.Arguments.Length > 0 && ReceiverType(op.Arguments[0].Value, map) is { } ct) { keep.Keep(ct, null, ctors: true); return; }
            Report(Diags.ReflectionUnknown, op.Syntax.GetLocation(), "Activator.CreateInstance");
            return;
        }
        // Delegate.CreateDelegate(type, target, "Name", ...): the named method, on every type the program names
        if (owner == "Delegate" && m.Name == "CreateDelegate" && op.Arguments.FirstOrDefault(a => a.Parameter?.Type.SpecialType == SpecialType.System_String)?.Value.ConstantValue is { HasValue: true, Value: string dn })
        {
            for (int id = 0; id < TypeById.Count; id++)
                if (TypeById[id] is INamedTypeSymbol nt && !nt.IsUnboundGenericType && !nt.TypeArguments.Any(a => a is ITypeParameterSymbol) && !SyntheticById.ContainsKey(id) && !DisplayById.ContainsKey(id)
                    && nt.GetMembers(dn).Any())
                    keep.Keep(nt, dn);
            return;
        }
        if (owner is not ("Type" or "TypeInfo" or "MemberInfo" or "CustomAttributeExtensions" or "Attribute") || !s_reflectNames.Contains(m.Name)) return;
        if (owner is "MemberInfo" or "CustomAttributeExtensions" or "Attribute") return; // attributes of kept members are kept with them
        var t = op.Instance != null ? ReceiverType(op.Instance, map) : null;
        string? name = null;
        if (m.Name is "GetMethod" or "GetProperty" or "GetField" or "GetMember" or "GetEvent" && op.Arguments.Length > 0 && op.Arguments[0].Value.ConstantValue is { HasValue: true, Value: string s }) name = s;
        if (t == null && name != null)
        {
            // a member named by a constant on a type the compiler cannot see: that member of every type the program
            // names (delegate types' Invoke)
            for (int id = 0; id < TypeById.Count; id++)
                if (TypeById[id] is INamedTypeSymbol nt && !nt.IsUnboundGenericType && !nt.TypeArguments.Any(a => a is ITypeParameterSymbol) && !SyntheticById.ContainsKey(id) && !DisplayById.ContainsKey(id)
                    && nt.GetMembers(name).Any())
                    keep.Keep(nt, name);
            if (!input) return;
        }
        if (t == null) { if (input) Report(Diags.ReflectionUnknown, op.Syntax.GetLocation(), "Type." + m.Name); return; }
        if (m.Name is "GetConstructor" or "GetConstructors") keep.Keep(t, null, ctors: true);
        else if (m.Name is "GetCustomAttributes" or "GetCustomAttribute" or "IsDefined" or "GetInterfaces") keep.Keep(t, "<type>");
        else keep.Keep(t, name);
    }

    /// <summary>The type a Type-valued operation names, when the compiler can see it.</summary>
    INamedTypeSymbol? ReceiverType(IOperation op, TypeMap map)
    {
        while (op is IConversionOperation { IsImplicit: true } c) op = c.Operand;
        switch (op)
        {
            case ITypeOfOperation to: return Types.Resolve(to.TypeOperand, map) as INamedTypeSymbol;
            case IInvocationOperation { TargetMethod.Name: "GetType", Instance: { } inst, Arguments.Length: 0 }:
                var it = Types.Resolve(inst.Type!, map);
                return it is INamedTypeSymbol n && (n.IsSealed || n.IsValueType) ? n : null;
            case ILocalReferenceOperation lr:
                {
                    // a local assigned once from something visible
                    var decl = lr.Local.DeclaringSyntaxReferences.FirstOrDefault();
                    if (decl == null) return null;
                    var model = ModelFor(decl.SyntaxTree);
                    if (model.GetOperation(decl.GetSyntax()) is IVariableDeclaratorOperation { Initializer.Value: { } init } && !lr.Local.IsRef)
                        return ReceiverType(init, map);
                    return null;
                }
        }
        return null;
    }

    /// <summary>Members of `t` (and its bases, for inherited lookups) a kept entry covers.</summary>
    IEnumerable<ISymbol> KeptMembers(INamedTypeSymbol t, bool all, HashSet<string> names, bool ctors) =>
        KeptMembersRaw(t, all, names, ctors).Distinct(SymbolEqualityComparer.Default);

    IEnumerable<ISymbol> KeptMembersRaw(INamedTypeSymbol t, bool all, HashSet<string> names, bool ctors)
    {
        for (var x = t; x != null; x = x.BaseType)
        {
            // upstream: object's methods are inherited members too (GetType first, as the runtime lists them)
            bool isObject = x.SpecialType == SpecialType.System_Object || Compiler.Types.IsObject(x);
            var members = isObject ? x.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary).OrderBy(m => m.Name == "GetType" ? 0 : 1).Cast<ISymbol>() : x.GetMembers();
            foreach (var mem in members)
            {
                bool want = all || names.Contains(mem.Name) || ctors && mem is IMethodSymbol { MethodKind: MethodKind.Constructor } && SymbolEqualityComparer.Default.Equals(x, t);
                if (!want) continue;
                switch (mem)
                {
                    case IEventSymbol ev:
                        if (ev.AddMethod is { } ea) yield return ea;
                        if (ev.RemoveMethod is { } er) yield return er;
                        break;
                    case IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.DelegateInvoke or MethodKind.EventAdd or MethodKind.EventRemove } ms when !ms.IsGenericMethod && !ms.IsAbstract && ms.Parameters.All(p => p.RefKind == RefKind.None && !p.Type.IsRefLikeType && p.Type is not IPointerTypeSymbol) && !ms.ReturnType.IsRefLikeType && !ms.IsExtern:
                        yield return ms; break;
                    case IMethodSymbol { MethodKind: MethodKind.Constructor } cs when SymbolEqualityComparer.Default.Equals(x, t) && cs.Parameters.All(p => p.RefKind == RefKind.None && !p.Type.IsRefLikeType && p.Type is not IPointerTypeSymbol) && !t.IsAbstract && !t.IsRefLikeType:
                        yield return cs; break;
                    case IPropertySymbol ps when !ps.IsIndexer:
                        if (AutoPropertyField(ps.OriginalDefinition) is { } bf) yield return bf;
                        if (ps.GetMethod is { } g && !g.IsAbstract) yield return g;
                        if (ps.SetMethod is { } s2 && !s2.IsAbstract) yield return s2;
                        break;
                    case IFieldSymbol fs when !fs.IsConst && !fs.IsImplicitlyDeclared:
                        yield return fs; break;
                }
            }
        }
    }

    string ThunkKey(ISymbol m) => m switch { IMethodSymbol ms => Types.MethodKey(CanonicalMethod(ms)), IFieldSymbol f => "F:" + Types.Key(Types.Canonical(f.ContainingType)) + "::" + f.Name, _ => m.ToDisplayString() };

    internal WasmFunction ThunkFor(ISymbol member)
    {
        var key = ThunkKey(member);
        if (_reflThunks.TryGetValue(key, out var f)) return f;
        f = Module.AddFunction("reflect " + member.ToDisplayString(), ThunkType);
        f.TableRoot = true; // the reflection tables refer to it
        _reflThunks[key] = f;
        var mi = new MethodInst { Sym = LibMethod("Cs2Wasm.Runtime.Rt", "ReportUnhandled", 1).Sym, Fn = f, Key = "thunk " + key, Impl = MethodImpl.Body };
        mi.Compile = () => FunctionCompiler.CompileReflectionThunk(this, f, member, mi);
        EnqueueExtra(mi);
        return f;
    }

    readonly Dictionary<string, WasmFunction> _attrFactories = [];
    static string AttrKey(AttributeData a) => a.AttributeClass!.ToDisplayString() + "(" + string.Join(",", a.ConstructorArguments.Select(c => c.ToCSharpString())) + ")" + string.Join(",", a.NamedArguments.Select(n => n.Key + "=" + n.Value.ToCSharpString()));

    WasmFunction AttributeFactory(AttributeData a)
    {
        var key = AttrKey(a);
        if (_attrFactories.TryGetValue(key, out var f)) return f;
        // GetCustomAttributes returns an array of the attribute's type (upstream: Array.CreateInstance)
        {
            for (var at = (INamedTypeSymbol?)a.AttributeClass; at != null && !Compiler.Types.IsObject(at); at = at.BaseType) MarkConstructed(Types.LibCompilationArray(at));
            // and AttributeUsage, which GetCustomAttributes reads of the attribute's type
            MarkConstructed(Types.LibCompilationArray(LibType("System.AttributeUsageAttribute")));
        }
        f = Module.AddFunction("attribute " + key, ThunkType);
        f.TableRoot = true; // the reflection tables refer to it
        _attrFactories[key] = f;
        var mi = new MethodInst { Sym = LibMethod("Cs2Wasm.Runtime.Rt", "ReportUnhandled", 1).Sym, Fn = f, Key = "attr " + key, Impl = MethodImpl.Body };
        mi.Compile = () => FunctionCompiler.CompileAttributeFactory(this, f, a, mi);
        EnqueueExtra(mi);
        return f;
    }

    /// <summary>Attribute factories must exist before the last compile round: requested with the members.</summary>
    internal void RequestAttributeFactories()
    {
        if (_reflect == null) return;
        foreach (var (t, all, names, ctors) in _reflect.Types.Values)
        {
            foreach (var a in KeptAttributes(t)) AttributeFactory(a);
            foreach (var m in KeptMembers(t, all, names, ctors))
                foreach (var a in KeptAttributes(m is IMethodSymbol { AssociatedSymbol: IPropertySymbol p } ? p : m)) AttributeFactory(a);
        }
    }

    static IEnumerable<AttributeData> KeptAttributes(ISymbol s) => s.GetAttributes().Where(a => a.AttributeClass != null && a.AttributeConstructor != null
        && a.AttributeClass.ContainingNamespace?.ToDisplayString() is not ("System.Runtime.CompilerServices" or "System.Diagnostics.CodeAnalysis" or "System.Runtime.InteropServices"));

    // ---------------- the tables (Finish, before memory layout) ----------------
    internal void EmitReflectionTables()
    {
        if (_metaOf == null) return;
        var e = new Emitter(_metaOf);
        if (_reflect == null) { e.I32(0); return; }
        var records = new Dictionary<int, IAddress>();
        foreach (var (t, all, names, ctors) in _reflect.Types.Values)
        {
            int id = TypeId(t);
            records[id] = TypeRecord(t, KeptMembers(t, all, names, ctors).ToList());
        }
        int tid = e.AddLocal(ValType.I32);
        e.Get(0); e.Set(tid);
        foreach (var (id, addr) in records)
        {
            e.Get(tid); e.Tid(id); e.Op(Op.I32Eq);
            e.If(); e.I32Addr(addr); e.Op(Op.Return); e.End();
        }
        e.I32(0);
    }

    int ExtTypeId(ITypeSymbol t) => ExtId(TypeId(t));

    /// <summary>The types the reflection records name (numbered for the module: RuntimeGen.AssignTypeIds).</summary>
    internal IEnumerable<ITypeSymbol> ReflectedTypes()
    {
        if (_reflect == null) yield break;
        foreach (var (t, all, names, ctors) in _reflect.Types.Values)
        {
            yield return t;
            if (t.BaseType != null) yield return t.BaseType;
            foreach (var i in t.AllInterfaces) yield return i;
            foreach (var a in KeptAttributes(t)) yield return a.AttributeClass!;
            foreach (var m in KeptMembers(t, all, names, ctors))
            {
                yield return m.ContainingType;
                foreach (var a in KeptAttributes(m is IMethodSymbol { AssociatedSymbol: IPropertySymbol pp } ? pp : m)) yield return a.AttributeClass!;
                switch (m)
                {
                    case IFieldSymbol f: yield return f.Type; break;
                    case IMethodSymbol ms:
                        if (!ms.ReturnsVoid) yield return ms.ReturnType;
                        foreach (var p in ms.Parameters) yield return p.Type;
                        if (ms.AssociatedSymbol is IPropertySymbol prop) yield return prop.Type;
                        break;
                }
            }
        }
    }

    int Thunk(ISymbol m) => _reflThunks.TryGetValue(ThunkKey(m), out var f) && f.Body.Count > 0 ? RootSlot(f) + 1 : 0;
    int RootSlot(WasmFunction f) { f.TableRoot = true; return Module.TableSlot(f); }
    int TypeIdOrVoid(ITypeSymbol t) => ExtTypeId(t);
    int Str(string s) => StringLiteral(s).Address;

    IAddress Ints(IEnumerable<int> values) { var b = values.SelectMany(BitConverter.GetBytes).ToArray(); return DataBlob(b.Length == 0 ? new byte[4] : b, 4); }

    (int count, int addr) Attrs(ISymbol s)
    {
        var list = KeptAttributes(s).Where(a => _attrFactories.ContainsKey(AttrKey(a))).ToList();
        var ints = new List<int>();
        foreach (var a in list)
        {
            ints.Add(ExtTypeId(a.AttributeClass!));
            ints.Add(RootSlot(AttributeFactory(a)) + 1);
        }
        return (list.Count, Ints(ints).Address);
    }

    const int FStatic = 1, FPublic = 2, FVirtual = 4, FAbstract = 8, FReadOnly = 16, FPrivate = 32, FFamily = 64, FAssembly = 128;
    /// <summary>A method that is an event's add or remove accessor (GetEvents pairs them).</summary>
    const int FEventAccessor = 1 << 12;
    static int Access(ISymbol s) => s.DeclaredAccessibility switch
    {
        Accessibility.Public => FPublic,
        Accessibility.Private => FPrivate,
        Accessibility.Protected => FFamily,
        Accessibility.Internal => FAssembly,
        _ => FFamily | FAssembly,
    };

    IAddress TypeRecord(INamedTypeSymbol t, List<ISymbol> members)
    {
        var fields = new List<int>(); int nf = 0;
        var props = new List<int>(); int np = 0;
        var methods = new List<int>(); int nm = 0;
        var ctors = new List<int>(); int nc = 0;
        var seenProps = new HashSet<string>();
        foreach (var m in members)
        {
            switch (m)
            {
                case IFieldSymbol f:
                    {
                        var (na, aa) = Attrs(f);
                        fields.AddRange([Str(f.Name), ExtTypeId(f.Type), (f.IsStatic ? FStatic : 0) | Access(f) | (f.IsReadOnly ? FReadOnly : 0), Thunk(f), ExtTypeId(f.ContainingType), na, aa]);
                        nf++;
                        break;
                    }
                case IMethodSymbol { MethodKind: MethodKind.Constructor } c:
                    {
                        var (na, aa) = Attrs(c);
                        ctors.AddRange([(c.IsStatic ? FStatic : 0) | Access(c), Thunk(c), c.Parameters.Length, Ints(c.Parameters.Select(p => ExtTypeId(p.Type))).Address, Ints(c.Parameters.Select(p => Str(p.Name))).Address, na, aa]);
                        ctors.Add(ExtTypeId(c.ContainingType));
                        nc++;
                        break;
                    }
                case IMethodSymbol ms:
                    {
                        var (na, aa) = Attrs(ms);
                        int flags = (ms.IsStatic ? FStatic : 0) | Access(ms) | (ms.IsVirtual || ms.IsOverride || ms.IsAbstract ? FVirtual : 0) | (ms.IsAbstract ? FAbstract : 0)
                            | (ms.MethodKind is MethodKind.EventAdd or MethodKind.EventRemove ? FEventAccessor : 0);
                        methods.AddRange([Str(ms.Name), ms.ReturnsVoid ? -1 : ExtTypeId(ms.ReturnType), flags, Thunk(ms), ms.Parameters.Length,
                            Ints(ms.Parameters.Select(p => ExtTypeId(p.Type))).Address, Ints(ms.Parameters.Select(p => Str(p.Name))).Address, na, aa, ExtTypeId(ms.ContainingType)]);
                        nm++;
                        if (ms.AssociatedSymbol is IPropertySymbol p && seenProps.Add(Types.Key(p.ContainingType) + "." + p.Name))
                        {
                            var (pa, pb) = Attrs(p);
                            int g = p.GetMethod != null ? Thunk(p.GetMethod) : 0, s = p.SetMethod != null ? Thunk(p.SetMethod) : 0;
                            int pflags = (p.IsStatic ? FStatic : 0) | Access(p.GetMethod ?? p.SetMethod!) | (p.GetMethod?.DeclaredAccessibility == Accessibility.Public ? 256 : 0) | (p.SetMethod?.DeclaredAccessibility == Accessibility.Public ? 512 : 0);
                            props.AddRange([Str(p.Name), ExtTypeId(p.Type), pflags, g, s, ExtTypeId(p.ContainingType), pa, pb]);
                            np++;
                        }
                        break;
                    }
            }
        }
        var (ta, tb) = Attrs(t);
        var rec = new[] { nf, Ints(fields).Address, np, Ints(props).Address, nm, Ints(methods).Address, nc, Ints(ctors).Address, ta, tb,
            t.BaseType != null ? ExtTypeId(t.BaseType) + 1 : 0, t.Interfaces.Length, Ints(t.AllInterfaces.Select(i => ExtTypeId(i))).Address };
        return Ints(rec);
    }
}

sealed partial class FunctionCompiler
{
    public static void CompileReflectionThunk(Driver d, WasmFunction fn, ISymbol member, MethodInst ctx)
    {
        var fc = new FunctionCompiler(d, fn, ctx.Sym, ctx);
        fc.EmitThunk(member);
    }

    public static void CompileAttributeFactory(Driver d, WasmFunction fn, AttributeData a, MethodInst ctx)
    {
        var fc = new FunctionCompiler(d, fn, ctx.Sym, ctx);
        fc.EmitAttributeFactory(a);
    }

    void PushArgAs(int i, ITypeSymbol pt)
    {
        pt = T.Canonical(pt);
        E.Get(1); E.Load(Op.I32Load, Types.ArrayData + 4 * i);
        var o = Own.Unstable;
        ConvertValue(D.ObjectType, pt, false, ref o, explicitConv: true, null);
        if (Types.IsStruct(pt))
        {
            int src = E.AddLocal(ValType.I32); E.Set(src);
            int off = TempStruct(pt);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.Get(src); CopyStructInit(pt);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
        }
    }

    void PushTargetAs(ITypeSymbol owner)
    {
        owner = T.Canonical(owner);
        E.Get(0);
        var o = Own.Unstable;
        ConvertValue(D.ObjectType, owner, false, ref o, explicitConv: true, null);
    }

    void BoxResult(ITypeSymbol rt, Own o)
    {
        rt = T.Canonical(rt);
        ConvertValue(rt, D.ObjectType, false, ref o, false, null);
        ToOwned(o);
    }

    void EmitThunk(ISymbol member)
    {
        using (BodyScope())
        {
            _retLocal = E.AddLocal(ValType.I32);
            BeginTemps();
            switch (member)
            {
                case IMethodSymbol { MethodKind: MethodKind.Constructor } c:
                    {
                        var ctor = D.CanonicalMethod(c);
                        var t = T.Canonical(ctor.ContainingType);
                        int tid = D.MarkConstructed(t);
                        int obj = E.AddLocal(ValType.I32);
                        if (t.IsValueType)
                        {
                            int dataOff = FunctionCompilerHelpers.BoxDataOffset(T, t);
                            E.I32(dataOff + T.StorageOf(t).size); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
                            if (!ctor.IsImplicitlyDeclared)
                                CallWith(ctor, false, () => { E.Get(obj); E.I32(dataOff); E.Op(Op.I32Add); }, () => { for (int i = 0; i < ctor.Parameters.Length; i++) PushArgAs(i, ctor.Parameters[i].Type); }, null, null);
                        }
                        else
                        {
                            E.I32(Math.Max(T.LayoutOf(t).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
                            CallWith(ctor, false, () => E.Get(obj), () => { for (int i = 0; i < ctor.Parameters.Length; i++) PushArgAs(i, ctor.Parameters[i].Type); }, null, null);
                        }
                        E.Get(obj);
                        break;
                    }
                case IMethodSymbol ms:
                    {
                        var m = D.CanonicalMethod(ms);
                        bool virt = !m.IsStatic && (m.IsVirtual || m.IsOverride || m.IsAbstract || m.ContainingType.TypeKind == TypeKind.Interface);
                        var own = CallWith(m, virt, m.IsStatic ? null : () => PushTargetAs(m.ContainingType),
                            () => { for (int i = 0; i < m.Parameters.Length; i++) PushArgAs(i, m.Parameters[i].Type); }, null, null);
                        if (m.ReturnsVoid) E.I32(0); else BoxResult(m.ReturnType, own);
                        break;
                    }
                case IFieldSymbol f:
                    {
                        // args null: read; else write args[0]
                        var owner = (INamedTypeSymbol)T.Canonical(f.ContainingType);
                        var ft = T.Resolve(f.Type, Map);
                        int off;
                        void PushAddr()
                        {
                            if (f.IsStatic) { EmitStaticInitGuard(owner); E.I32Addr(D.StaticField(owner, f, ft)); return; }
                            PushTargetAs(owner);
                        }
                        off = f.IsStatic ? 0 : T.FieldIn(owner, f).Offset;
                        E.Get(1); E.Op(Op.I32Eqz);
                        E.If(ValType.I32);
                        {
                            PushAddr();
                            Own o;
                            if (Types.IsStruct(ft)) { if (off != 0) { E.I32(off); E.Op(Op.I32Add); } o = Own.None; }
                            else { E.Load(Types.LoadOp(ft), off); o = Types.IsRef(ft) ? Own.Unstable : Own.None; }
                            BoxResult(ft, o);
                        }
                        E.Else();
                        {
                            int a = E.AddLocal(ValType.I32);
                            PushAddr(); E.Set(a);
                            E.Get(1); E.Load(Op.I32Load, Types.ArrayData);
                            var vo = Own.Unstable;
                            ConvertValue(D.ObjectType, ft, false, ref vo, explicitConv: true, null);
                            StoreToAddressFromStack(() => E.Get(a), off, ft, vo, pub: f.IsStatic ? Publish.Always : owner.IsValueType ? Publish.Unknown : Publish.Container, pubContainer: () => E.Get(a));
                            E.I32(0);
                        }
                        E.End();
                        break;
                    }
            }
            E.Set(_retLocal);
            EndTemps();
            EmitReturnSequence(true);
        }
        Finish();
    }

    void EmitAttributeFactory(AttributeData a)
    {
        using (BodyScope())
        {
            _retLocal = E.AddLocal(ValType.I32);
            BeginTemps();
            var ctor = D.CanonicalMethod(a.AttributeConstructor!);
            var t = T.Canonical(ctor.ContainingType);
            int tid = D.MarkConstructed(t);
            int obj = E.AddLocal(ValType.I32);
            E.I32(Math.Max(T.LayoutOf(t).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
            CallWith(ctor, false, () => E.Get(obj), () =>
            {
                for (int i = 0; i < ctor.Parameters.Length; i++) PushTypedConstant(a.ConstructorArguments[i], ctor.Parameters[i].Type);
            }, null, null);
            foreach (var (name, val) in a.NamedArguments)
            {
                var mem = t.GetMembers(name).FirstOrDefault() ?? ((INamedTypeSymbol)t).BaseType?.GetMembers(name).FirstOrDefault();
                if (mem is IPropertySymbol { SetMethod: { } set })
                    CallWith(D.CanonicalMethod(set), false, () => E.Get(obj), () => PushTypedConstant(val, set.Parameters[0].Type), null, null);
                else if (mem is IFieldSymbol fld)
                {
                    var ft = T.Resolve(fld.Type, Map);
                    PushTypedConstant(val, ft);
                    var o = Types.IsRef(ft) ? Own.Stable : Own.None;
                    int v = E.AddLocal(Types.WasmType(ft) ?? ValType.I32); E.Set(v);
                    StoreToAddressFromStack(() => E.Get(obj), T.FieldIn((INamedTypeSymbol)t, fld).Offset, ft, Types.IsRef(ft) ? EmitGetOwn(v) : EmitGetPlain(v), pub: Publish.Container, pubContainer: () => E.Get(obj));
                }
            }
            E.Get(obj);
            E.Set(_retLocal);
            EndTemps();
            EmitReturnSequence(true);
        }
        Finish();
    }

    Own EmitGetOwn(int l) { E.Get(l); return Own.Stable; }
    Own EmitGetPlain(int l) { E.Get(l); return Own.None; }

    void PushTypedConstant(TypedConstant c, ITypeSymbol target)
    {
        target = T.Canonical(target);
        if (c.Kind == TypedConstantKind.Type && c.Value is ITypeSymbol ts)
        {
            E.Tid(D.TypeId(T.Canonical(ts)));
            var fromId = D.LibType("System.Type").GetMembers("FromId").OfType<IMethodSymbol>().First();
            E.Call(D.Method(fromId).Fn);
            int tl = TempRef(D.ObjectType); E.Tee(tl);
            return;
        }
        if (c.Kind == TypedConstantKind.Array || c.IsNull) { E.I32(0); return; }
        var ct = c.Type != null ? T.Canonical(c.Type) : target;
        var o = EmitConstant(c.Value, ct is INamedTypeSymbol { TypeKind: TypeKind.Enum } en ? T.Canonical(en.EnumUnderlyingType!) : ct);
        var from = ct is INamedTypeSymbol { TypeKind: TypeKind.Enum } en2 ? T.Canonical(en2.EnumUnderlyingType!) : ct;
        if (Types.Key(from) != Types.Key(target)) ConvertValue(from, target, false, ref o, true, null);
    }
}
