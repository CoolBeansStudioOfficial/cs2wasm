using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>Records: the members the C# compiler synthesizes, with the same behaviour as Roslyn's.</summary>
sealed partial class FunctionCompiler
{
    INamedTypeSymbol RecordType => (INamedTypeSymbol)T.Resolve(M.ContainingType, Map);
    bool IsRecordStruct => M.ContainingType.IsValueType;

    /// <summary>The base type if it is a record (its synthesized members are chained to).</summary>
    INamedTypeSymbol? BaseRecord => M.ContainingType.BaseType is { IsRecord: true } b ? (INamedTypeSymbol)T.Resolve(b, Map) : null;

    IEnumerable<IFieldSymbol> RecordFields(INamedTypeSymbol t) =>
        t.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic && !f.IsConst);

    IEnumerable<ISymbol> PrintableMembers(INamedTypeSymbol t) =>
        t.GetMembers().Where(m => !m.IsStatic && m.DeclaredAccessibility == Accessibility.Public && !m.IsImplicitlyDeclared &&
            (m is IFieldSymbol { IsConst: false } || m is IPropertySymbol { IsIndexer: false, GetMethod: not null, IsOverride: false } p && p.Name != "EqualityContract"));

    void PushThisValue() => E.Get(_thisLocal);

    void EmitRecordMember()
    {
        var def = M.OriginalDefinition;
        var rt = RecordType;
        switch (def.Name)
        {
            case ".ctor" when def.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(def.Parameters[0].Type.OriginalDefinition, def.ContainingType.OriginalDefinition) && !IsRecordStruct:
                EmitRecordCopyCtor(rt);
                return;
            case ".ctor":
                EmitRecordPrimaryCtor(rt);
                return;
            case "<Clone>$":
                {
                    var copy = rt.InstanceConstructors.First(c => c.Parameters.Length == 1 && Types.Key(T.Canonical(c.Parameters[0].Type)) == Types.Key(rt));
                    int tid = D.MarkConstructed(rt);
                    int obj = E.AddLocal(ValType.I32);
                    E.I32(Math.Max(T.LayoutOf(rt).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
                    CallWith(D.CanonicalMethod(copy), false, () => E.Get(obj), () => E.Get(_thisLocal), null, null);
                    _retLocal = obj;
                    EmitReturnSequence(true);
                    return;
                }
            case "get_EqualityContract":
                {
                    E.Tid(D.TypeId(rt));
                    var fromId = D.LibType("System.Type").GetMembers("FromId").OfType<IMethodSymbol>().First();
                    E.Call(D.Method(fromId).Fn);
                    _retLocal = E.AddLocal(ValType.I32); E.Set(_retLocal);
                    EmitReturnSequence(true);
                    return;
                }
            case "Equals" when def.Parameters.Length == 1 && Types.IsObject(def.Parameters[0].Type):
                {
                    // Equals(object obj) => Equals(obj as R)   (record struct: obj is R r && Equals(r))
                    var eqT = rt.GetMembers("Equals").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 1 && Types.Key(T.Canonical(m.Parameters[0].Type)) == Types.Key(rt));
                    var arg = _vars[M.Parameters[0]];
                    if (IsRecordStruct)
                    {
                        LoadVar(arg); E.Call(Rt.IsExactBox(rt));
                        E.If(ValType.I32);
                        CallWith(D.CanonicalMethod(eqT), false, () => E.Get(_thisLocal), () => { LoadVar(arg); E.I32(BoxDataOffset(T, rt)); E.Op(Op.I32Add); }, null, null);
                        E.Else(); E.I32(0); E.End();
                    }
                    else
                    {
                        CallWith(D.CanonicalMethod(eqT), true, () => E.Get(_thisLocal), () =>
                        {
                            LoadVar(arg); int v = Scratch(ValType.I32); E.Tee(v);
                            E.If(ValType.I32); E.Get(v); E.Call(Rt.IsInst(rt)); E.If(ValType.I32); E.Get(v); E.Else(); E.I32(0); E.End(); E.Else(); E.I32(0); E.End();
                            FreeScratch(ValType.I32, v);
                        }, null, null);
                    }
                    _retLocal = E.AddLocal(ValType.I32); E.Set(_retLocal);
                    EmitReturnSequence(true);
                    return;
                }
            case "Equals" when !IsRecordStruct && Types.Key(T.Canonical(def.Parameters[0].Type)) != Types.Key(rt)
                && rt.GetMembers("Equals").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 1 && Types.IsObject(m.Parameters[0].Type)) is { } eqObj:
                {
                    // a derived record's Equals(Base other) => Equals((object)other): the record's own Equals(R),
                    // which the program may have written, decides
                    var arg = _vars[M.Parameters[0]];
                    CallWith(D.CanonicalMethod(eqObj), false, () => E.Get(_thisLocal), () => LoadVar(arg), null, null);
                    _retLocal = E.AddLocal(ValType.I32); E.Set(_retLocal);
                    EmitReturnSequence(true);
                    return;
                }
            case "Equals":
                EmitRecordEquals(rt);
                return;
            case "GetHashCode":
                EmitRecordHash(rt);
                return;
            case "ToString":
                EmitRecordToString(rt);
                return;
            case "PrintMembers":
                EmitRecordPrintMembers(rt);
                return;
            case "op_Equality":
            case "op_Inequality":
                {
                    // (object)l == r || (l is not null && l.Equals(r))
                    var eqT = rt.GetMembers("Equals").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 1 && Types.Key(T.Canonical(m.Parameters[0].Type)) == Types.Key(rt));
                    var l = _vars[M.Parameters[0]]; var r = _vars[M.Parameters[1]];
                    if (IsRecordStruct)
                        CallWith(D.CanonicalMethod(eqT), false, () => PushVarAddress(l), () => PushVarAddress(r), null, null);
                    else
                    {
                        LoadVar(l); LoadVar(r); E.Op(Op.I32Eq);
                        E.If(ValType.I32); E.I32(1);
                        E.Else();
                        LoadVar(l);
                        E.If(ValType.I32);
                        CallWith(D.CanonicalMethod(eqT), true, () => LoadVar(l), () => LoadVar(r), null, null);
                        E.Else(); E.I32(0); E.End();
                        E.End();
                    }
                    if (def.Name == "op_Inequality") E.Op(Op.I32Eqz);
                    _retLocal = E.AddLocal(ValType.I32); E.Set(_retLocal);
                    EmitReturnSequence(true);
                    return;
                }
            case "Deconstruct":
                {
                    foreach (var p in M.Parameters)
                    {
                        var prop = rt.GetMembers(p.Name).FirstOrDefault(m => m is IPropertySymbol or IFieldSymbol) ?? throw new CompileException("Deconstruct: no member " + p.Name);
                        var dst = _vars[p];
                        var pt = T.Resolve(p.Type, Map);
                        if (prop is IPropertySymbol pp)
                        {
                            var getter = D.CanonicalMethod(pp.GetMethod!);
                            var o = CallWith(getter, IsVirtualDispatch(getter), () => E.Get(_thisLocal), () => { }, null, null);
                            StoreToAddressFromStack(() => E.Get(dst.AddrLocal), 0, pt, o);
                        }
                    }
                    EmitReturnSequence(false);
                    return;
                }
        }
        throw new CompileException($"record member '{M.Name}' is not supported yet", M.ContainingType.Locations.FirstOrDefault());
    }

    void EmitRecordPrimaryCtor(INamedTypeSymbol rt)
    {
        // base(...) from the record's base list, else the implicit base constructor
        var decl = M.ContainingType.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<RecordDeclarationSyntax>().FirstOrDefault();
        var baseSyntax = decl?.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault();
        // positional properties are initialized from the parameters first, as field initializers are
        foreach (var p in M.Parameters)
        {
            var prop = M.ContainingType.GetMembers(p.Name).OfType<IPropertySymbol>().FirstOrDefault();
            var bf = prop != null ? Driver.AutoPropertyField(prop) : null;
            if (bf == null || Driver.PropertyInitializer(prop!) != null) continue;
            var v = _vars[p];
            BeginTemps();
            EmitStoreFieldFrom(bf, () => E.Get(_thisLocal), T.Resolve(p.Type, Map), () => LoadVar(v));
            EndTemps();
        }
        EmitFieldInitializers();
        if (baseSyntax != null && D.ModelFor(baseSyntax.SyntaxTree).GetOperation(baseSyntax) is IOperation bop)
        {
            BeginTemps();
            EmitDiscard(bop is IExpressionStatementOperation es ? es.Operation : bop);
            EndTemps();
        }
        else EmitImplicitBaseCtor();
    }

    void EmitRecordCopyCtor(INamedTypeSymbol rt)
    {
        var src = _vars[M.Parameters[0]];
        foreach (var f in RecordFields((INamedTypeSymbol)T.Canonical(rt)))
        {
            var slot = T.FieldIn(rt, f);
            BeginTemps();
            EmitStoreFieldFrom(f, () => E.Get(_thisLocal), slot.Type, () =>
            {
                LoadVar(src);
                if (Types.IsStruct(slot.Type)) { E.I32(slot.Offset); E.Op(Op.I32Add); return Own.None; }
                E.Load(Types.LoadOp(slot.Type), slot.Offset);
                return Types.IsRef(slot.Type) ? Own.Unstable : Own.None;
            });
            EndTemps();
        }
        if (BaseRecord is { } br)
        {
            var bcopy = br.InstanceConstructors.First(c => c.Parameters.Length == 1 && Types.Key(T.Canonical(c.Parameters[0].Type)) == Types.Key(br));
            CallWith(D.CanonicalMethod(bcopy), false, () => E.Get(_thisLocal), () => LoadVar(src), null, null);
        }
    }

    void EmitRecordEquals(INamedTypeSymbol rt)
    {
        // (object)this == other || (other != null && contract/base check && fields equal)
        var other = _vars[M.Parameters[0]];
        var done = E.Block(ValType.I32);
        if (!IsRecordStruct)
        {
            E.Get(_thisLocal); LoadVar(other); E.Op(Op.I32Eq);
            E.If(); E.I32(1); E.Br(done); E.End();
            LoadVar(other); E.Op(Op.I32Eqz);
            E.If(); E.I32(0); E.Br(done); E.End();
            if (BaseRecord is { } br)
            {
                var beq = br.GetMembers("Equals").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 1 && Types.Key(T.Canonical(m.Parameters[0].Type)) == Types.Key(br));
                CallWith(D.CanonicalMethod(beq), false, () => E.Get(_thisLocal), () => LoadVar(other), null, null);
            }
            else
            {
                var ec = rt.GetMembers("EqualityContract").OfType<IPropertySymbol>().First().GetMethod!;
                var ecm = D.CanonicalMethod(ec);
                CallWith(ecm, IsVirtualDispatch(ecm), () => E.Get(_thisLocal), () => { }, null, null);
                int a = Scratch(ValType.I32); E.Set(a);
                CallWith(ecm, IsVirtualDispatch(ecm), () => LoadVar(other), () => { }, null, null);
                int b = Scratch(ValType.I32); E.Set(b);
                E.Get(a); E.Get(b); E.Op(Op.I32Eq);
                E.Get(a); E.Call(Rt.Release); E.Get(b); E.Call(Rt.Release);
                FreeScratch(ValType.I32, a); FreeScratch(ValType.I32, b);
            }
            E.Op(Op.I32Eqz); E.If(); E.I32(0); E.Br(done); E.End();
        }
        foreach (var f in RecordFields((INamedTypeSymbol)T.Canonical(rt)))
        {
            var slot = T.FieldIn(rt, f);
            void Push(Action recv)
            {
                recv();
                if (Types.IsStruct(slot.Type)) { E.I32(slot.Offset); E.Op(Op.I32Add); }
                else E.Load(Types.LoadOp(slot.Type), slot.Offset);
            }
            Push(() => E.Get(_thisLocal));
            Push(() => { if (IsRecordStruct) PushVarAddress(other); else LoadVar(other); });
            E.Call(Rt.GenericEquals(slot.Type));
            E.Op(Op.I32Eqz); E.If(); E.I32(0); E.Br(done); E.End();
        }
        E.I32(1);
        E.End();
        _retLocal = E.AddLocal(ValType.I32); E.Set(_retLocal);
        EmitReturnSequence(true);
    }

    void EmitRecordHash(INamedTypeSymbol rt)
    {
        int h = E.AddLocal(ValType.I32);
        if (!IsRecordStruct)
        {
            if (BaseRecord is { } br)
            {
                var bh = br.GetMembers("GetHashCode").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 0);
                CallWith(D.CanonicalMethod(bh), false, () => E.Get(_thisLocal), () => { }, null, null);
            }
            else E.I32(D.TypeId(rt) * 16777619);
            E.Set(h);
        }
        foreach (var f in RecordFields((INamedTypeSymbol)T.Canonical(rt)))
        {
            var slot = T.FieldIn(rt, f);
            E.Get(h); E.I32(-1521134295); E.Op(Op.I32Mul);
            E.Get(_thisLocal);
            if (Types.IsStruct(slot.Type)) { E.I32(slot.Offset); E.Op(Op.I32Add); } else E.Load(Types.LoadOp(slot.Type), slot.Offset);
            E.Call(Rt.GenericHash(slot.Type));
            E.Op(Op.I32Add); E.Set(h);
        }
        _retLocal = h;
        EmitReturnSequence(true);
    }

    IMethodSymbol SbAppend(string paramTypeName)
    {
        var sb = D.LibType("System.Text.StringBuilder");
        return sb.GetMembers("Append").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 1 && Types.FullName(m.Parameters[0].Type) == paramTypeName);
    }

    void EmitRecordToString(INamedTypeSymbol rt)
    {
        // new StringBuilder().Append("R").Append(" { "); if (PrintMembers(sb)) sb.Append(' '); sb.Append('}')
        var sbT = D.LibType("System.Text.StringBuilder");
        var ctor = sbT.InstanceConstructors.First(c => c.Parameters.Length == 0);
        int tid = D.MarkConstructed(sbT);
        int sb = E.AddLocal(ValType.I32);
        E.I32(Math.Max(T.LayoutOf(sbT).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(sb);
        AddOwned(new Slot { Local = sb, Type = sbT });
        CallWith(D.CanonicalMethod(ctor), false, () => E.Get(sb), () => { }, null, null);
        var appendS = SbAppend("System.String"); var appendC = SbAppend("System.Char");
        void AppendStr(string s) { var o = CallWith(appendS, false, () => E.Get(sb), () => E.I32Addr(D.StringLiteral(s)), null, null); Drop(o, sbT); }
        AppendStr(M.ContainingType.Name);
        AppendStr(" { ");
        var pm = rt.GetMembers("PrintMembers").OfType<IMethodSymbol>().First();
        var pmm = D.CanonicalMethod(pm);
        CallWith(pmm, IsVirtualDispatch(pmm), () => E.Get(_thisLocal), () => E.Get(sb), null, null);
        E.If(); { var o = CallWith(appendC, false, () => E.Get(sb), () => E.I32(' '), null, null); Drop(o, sbT); } E.End();
        { var o = CallWith(appendC, false, () => E.Get(sb), () => E.I32('}'), null, null); Drop(o, sbT); }
        var ts = sbT.GetMembers("ToString").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 0);
        CallWith(D.CanonicalMethod(ts), false, () => E.Get(sb), () => { }, null, null);
        _retLocal = E.AddLocal(ValType.I32); E.Set(_retLocal);
        EmitReturnSequence(true);
    }

    void EmitRecordPrintMembers(INamedTypeSymbol rt)
    {
        var sbT = D.LibType("System.Text.StringBuilder");
        var sbV = _vars[M.Parameters[0]];
        var appendS = SbAppend("System.String"); var appendO = SbAppend("System.Object");
        int any = E.AddLocal(ValType.I32);
        if (BaseRecord is { } br)
        {
            var bpm = br.GetMembers("PrintMembers").OfType<IMethodSymbol>().First();
            CallWith(D.CanonicalMethod(bpm), false, () => E.Get(_thisLocal), () => LoadVar(sbV), null, null);
            E.Set(any);
        }
        foreach (var m in PrintableMembers((INamedTypeSymbol)T.Canonical(rt)))
        {
            // separator, "Name = ", value
            E.Get(any); E.If(); { var o = CallWith(appendS, false, () => LoadVar(sbV), () => E.I32Addr(D.StringLiteral(", ")), null, null); Drop(o, sbT); } E.End();
            { var o = CallWith(appendS, false, () => LoadVar(sbV), () => E.I32Addr(D.StringLiteral(m.Name + " = ")), null, null); Drop(o, sbT); }
            ITypeSymbol mt;
            Func<Own> produce;
            if (m is IPropertySymbol p)
            {
                var g = D.CanonicalMethod(p.GetMethod!);
                mt = T.Canonical(p.Type);
                produce = () => CallWith(g, IsVirtualDispatch(g), () => E.Get(_thisLocal), () => { }, null, null);
            }
            else
            {
                var f = (IFieldSymbol)m;
                var slot = T.FieldIn(rt, f);
                mt = slot.Type;
                produce = () => { E.Get(_thisLocal); if (Types.IsStruct(mt)) { E.I32(slot.Offset); E.Op(Op.I32Add); return Own.None; } E.Load(Types.LoadOp(mt), slot.Offset); return Types.IsRef(mt) ? Own.Unstable : Own.None; };
            }
            {
                var o = CallWith(appendO, false, () => LoadVar(sbV), () =>
                {
                    var vo = produce();
                    ConvertTop(mt, D.ObjectType, ref vo);
                    ToCallSafe(vo, D.ObjectType);
                }, null, null);
                Drop(o, sbT);
            }
            E.I32(1); E.Set(any);
        }
        _retLocal = any;
        EmitReturnSequence(true);
    }

    /// <summary>`x with { ... }`: clone (or copy, for structs) then apply the initializer.</summary>
    Own EmitWith(IWithOperation w)
    {
        var t = TypeOf(w);
        if (t.IsValueType)
        {
            int off = TempStruct(t);
            EmitValueAs(w.Operand, t);
            int src = Scratch(ValType.I32); E.Set(src);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.Get(src); CopyStructInit(t);
            FreeScratch(ValType.I32, src);
            int a = E.AddLocal(ValType.I32); E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.Set(a);
            if (w.Initializer != null) EmitInitializer(w.Initializer, a, t);
            E.Get(a);
            return Own.None;
        }
        if (w.CloneMethod == null) return EmitAnonymousWith(w, t);
        var clone = SubstMethod(w.CloneMethod);
        var o = EmitCallOps(clone, w.Operand, [], true, w.Syntax, alreadyResolved: true);
        int obj = E.AddLocal(ValType.I32);
        E.Set(obj);
        var slot = new Slot { Local = obj, Type = t };
        RegisterTemp(slot);
        if (w.Initializer != null) EmitInitializer(w.Initializer, obj, t);
        _temps[^1].Remove(slot);
        E.Get(obj); E.I32(0); E.Set(obj);
        return Own.Owned;
    }
    /// <summary>`with` on an anonymous type (it has no clone method and its members no setters): a new object whose
    /// slots hold the given values, and the operand's for the rest.</summary>
    Own EmitAnonymousWith(IWithOperation w, ITypeSymbol t)
    {
        var at = (INamedTypeSymbol)T.Canonical(t);
        int tid = D.MarkConstructed(at);
        RegisterAnonymous(at, tid);
        var so = EmitValue(w.Operand);
        ToTransient(so, t);
        int src = E.AddLocal(ValType.I32);
        E.Set(src);
        int obj = E.AddLocal(ValType.I32);
        E.I32(Math.Max(T.LayoutOf(at).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
        var slot = new Slot { Local = obj, Type = at };
        RegisterTemp(slot);
        var given = new Dictionary<string, IOperation>();
        if (w.Initializer != null)
            foreach (var init in w.Initializer.Initializers)
            {
                if (init is not ISimpleAssignmentOperation { Target: IPropertyReferenceOperation pr } sa) throw new CompileException("anonymous type member", init.Syntax.GetLocation());
                given[pr.Property.Name] = sa.Value;
            }
        foreach (var fs in T.LayoutOf(at).Fields)
        {
            BeginTemps();
            Own vo;
            if (given.TryGetValue(fs.Symbol.Name, out var value)) vo = EmitValueAs(value, fs.Type);
            else
            {
                E.Get(src);
                if (Types.IsStruct(fs.Type)) { E.I32(fs.Offset); E.Op(Op.I32Add); vo = Own.None; }
                else { E.Load(Types.LoadOp(fs.Type), fs.Offset); vo = Own.None; if (Types.IsRef(fs.Type)) { E.Call(Rt.Retain); vo = Own.Owned; } }
            }
            StoreToAddressFromStack(() => E.Get(obj), fs.Offset, fs.Type, vo, pub: Publish.None);
            EndTemps();
        }
        _temps[^1].Remove(slot);
        E.Get(obj);
        return Own.Owned;
    }
}
