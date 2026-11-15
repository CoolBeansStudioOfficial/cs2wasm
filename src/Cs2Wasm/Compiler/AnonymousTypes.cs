using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// Anonymous types (`new { A = 1, B = "x" }`), as Roslyn defines them: a class with a read-only property per member,
// value equality over the members, and ToString "{ A = 1, B = x }". Each property's value lives in a slot of the
// object (Types.Compute gives them a layout); the members are synthesized.
sealed partial class FunctionCompiler
{
    INamedTypeSymbol? _anonType;

    /// <summary>Anonymous types have no ToString/Equals/GetHashCode symbols: their overrides are registered as
    /// a synthetic type's implementations of object's methods (as iterator objects' are).</summary>
    void RegisterAnonymous(INamedTypeSymbol at, int tid)
    {
        if (D.SyntheticById.ContainsKey(tid)) return;
        var st = new SyntheticType { Name = "<>f__AnonymousType" };
        var obj = D.LibType("System.Object");
        foreach (var name in new[] { "ToString", "Equals", "GetHashCode" })
        {
            var slot = D.CanonicalMethod(obj.GetMembers(name).OfType<IMethodSymbol>().First(m => m.IsVirtual && m.Parameters.Length == (name == "Equals" ? 1 : 0)));
            var fn = D.Module.AddFunction("anonymous " + name + " " + Types.Key(at), Driver.Signature(slot, T));
            var mi = new MethodInst { Sym = slot, Fn = fn, Key = "anon " + Types.Key(at) + " " + name, Impl = MethodImpl.Synthesized };
            mi.Compile = () => { var fc = new FunctionCompiler(D, fn, slot, mi) { _anonType = at }; fc.CompileMethod(); };
            D.EnqueueExtra(mi);
            st.Impls[Types.MethodKey(slot)] = fn;
        }
        D.SyntheticById[tid] = st;
    }

    Own EmitAnonymousCreation(IAnonymousObjectCreationOperation ac)
    {
        var at = (INamedTypeSymbol)T.Canonical(TypeOf(ac));
        int tid = D.MarkConstructed(at);
        RegisterAnonymous(at, tid);
        int obj = E.AddLocal(ValType.I32);
        E.I32(Math.Max(T.LayoutOf(at).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
        var slot = new Slot { Local = obj, Type = at };
        RegisterTemp(slot);
        foreach (var init in ac.Initializers)
        {
            if (init is not ISimpleAssignmentOperation { Target: IPropertyReferenceOperation pr } sa) throw new CompileException("anonymous type member", init.Syntax.GetLocation());
            var fs = T.LayoutOf(at).Fields.First(f => SymbolEqualityComparer.Default.Equals(f.Symbol, pr.Property) || f.Symbol.Name == pr.Property.Name);
            BeginTemps();
            var o = EmitValueAs(sa.Value, fs.Type);
            StoreToAddressFromStack(() => E.Get(obj), fs.Offset, fs.Type, o, pub: Publish.None);
            EndTemps();
        }
        _temps[^1].Remove(slot);
        E.Get(obj);
        return Own.Owned;
    }

    void EmitAnonymousMember()
    {
        var def = M.OriginalDefinition;
        var at = _anonType ?? (INamedTypeSymbol)T.Canonical(def.ContainingType);
        var fields = T.LayoutOf(at).Fields;
        void Load(Action recv, FieldSlot f)
        {
            recv();
            if (Types.IsStruct(f.Type)) { E.I32(f.Offset); E.Op(Op.I32Add); } else E.Load(Types.LoadOp(f.Type), f.Offset);
        }
        switch (def.MethodKind, def.Name)
        {
            case (MethodKind.PropertyGet, _):
                {
                    var f = fields.First(x => x.Symbol.Name == ((IPropertySymbol)def.AssociatedSymbol!).Name);
                    if (StructRet) { E.Get(_structRetLocal); Load(() => E.Get(_thisLocal), f); CopyStructInit(f.Type); EmitReturnSequence(false); return; }
                    Load(() => E.Get(_thisLocal), f);
                    if (Types.IsRef(f.Type)) E.Call(Rt.Retain);
                    _retLocal = E.AddLocal(Types.WasmType(f.Type) ?? ValType.I32); E.Set(_retLocal);
                    EmitReturnSequence(true);
                    return;
                }
            case (MethodKind.Constructor, _):
                EmitReturnSequence(false);
                return;
            case (_, "Equals"):
                {
                    var other = _vars[M.Parameters[0]];
                    var done = E.Block(ValType.I32);
                    int o2 = E.AddLocal(ValType.I32);
                    LoadVar(other); E.Set(o2);
                    E.Get(_thisLocal); E.Get(o2); E.Op(Op.I32Eq); E.If(); E.I32(1); E.Br(done); E.End();
                    E.Get(o2); E.Op(Op.I32Eqz); E.If(); E.I32(0); E.Br(done); E.End();
                    E.Get(o2); E.Call(Rt.IsInst(at)); E.Op(Op.I32Eqz); E.If(); E.I32(0); E.Br(done); E.End();
                    foreach (var f in fields)
                    {
                        Load(() => E.Get(_thisLocal), f);
                        Load(() => E.Get(o2), f);
                        E.Call(Rt.GenericEquals(f.Type));
                        E.Op(Op.I32Eqz); E.If(); E.I32(0); E.Br(done); E.End();
                    }
                    E.I32(1);
                    E.End();
                    _retLocal = E.AddLocal(ValType.I32); E.Set(_retLocal);
                    EmitReturnSequence(true);
                    return;
                }
            case (_, "GetHashCode"):
                {
                    // Roslyn's: a seed from the member names (FNV), then h = h * -1521134295 + hash(member)
                    int seed = 0;
                    foreach (var f in fields) seed = unchecked(seed * -1521134295 + Fnv("<" + f.Symbol.Name + ">i__Field"));
                    int h = E.AddLocal(ValType.I32);
                    E.I32(seed); E.Set(h);
                    foreach (var f in fields)
                    {
                        E.Get(h); E.I32(-1521134295); E.Op(Op.I32Mul);
                        Load(() => E.Get(_thisLocal), f);
                        E.Call(Rt.GenericHash(f.Type));
                        E.Op(Op.I32Add); E.Set(h);
                    }
                    _retLocal = h;
                    EmitReturnSequence(true);
                    return;
                }
            case (_, "ToString"):
                {
                    var sbT = D.LibType("System.Text.StringBuilder");
                    var ctor = sbT.InstanceConstructors.First(c => c.Parameters.Length == 0);
                    int tid = D.MarkConstructed(sbT);
                    int sb = E.AddLocal(ValType.I32);
                    E.I32(Math.Max(T.LayoutOf(sbT).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(sb);
                    AddOwned(new Slot { Local = sb, Type = sbT });
                    CallWith(D.CanonicalMethod(ctor), false, () => E.Get(sb), () => { }, null, null);
                    var appendS = SbAppend("System.String"); var appendO = SbAppend("System.Object");
                    void Str(string s) { var o = CallWith(appendS, false, () => E.Get(sb), () => E.I32Addr(D.StringLiteral(s)), null, null); Drop(o, sbT); }
                    Str("{");
                    for (int i = 0; i < fields.Count; i++)
                    {
                        var f = fields[i];
                        Str((i == 0 ? " " : ", ") + f.Symbol.Name + " = ");
                        var o = CallWith(appendO, false, () => E.Get(sb), () =>
                        {
                            Load(() => E.Get(_thisLocal), f);
                            var vo = Types.IsRef(f.Type) ? Own.Unstable : Own.None;
                            ConvertTop(f.Type, D.ObjectType, ref vo);
                            ToCallSafe(vo, D.ObjectType);
                        }, null, null);
                        Drop(o, sbT);
                    }
                    Str(fields.Count == 0 ? " }" : " }");
                    var ts = sbT.GetMembers("ToString").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 0);
                    CallWith(D.CanonicalMethod(ts), false, () => E.Get(sb), () => { }, null, null);
                    _retLocal = E.AddLocal(ValType.I32); E.Set(_retLocal);
                    EmitReturnSequence(true);
                    return;
                }
        }
        throw new CompileException($"anonymous type member '{def.Name}' is not supported", def.Locations.FirstOrDefault());
    }

    static int Fnv(string s)
    {
        int h = unchecked((int)2166136261);
        foreach (var c in s) h = unchecked((h ^ c) * 16777619);
        return h;
    }
}
