using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace Cs2Wasm.Compiler;

sealed partial class FunctionCompiler
{
    /// <summary>Shared generic code: in an instantiation of a generic definition (WasmFunction.Family), a type as the
    /// definition names it is a type parameter. Decisions that would depend on which reference type it stands for (is
    /// it sealed) are then made as for any reference type, so that the family's members compile to one body (their
    /// exact types still come in as constants: ShareConstants makes those the shared body's hidden parameters).
    /// CS2WASM_NO_CANON=1: decided on the exact type, as before.</summary>
    bool OfTypeParameter(ITypeSymbol? original) => !s_noCanon && original is ITypeParameterSymbol && E.Fn.Family != null;
    static readonly bool s_noCanon = Environment.GetEnvironmentVariable("CS2WASM_NO_CANON") == "1";
    static readonly bool s_noSealedStore = Environment.GetEnvironmentVariable("CS2WASM_NO_SEALED_STORE") == "1";

    ITypeSymbol TypeOf(IOperation op) => T.Resolve(op.Type ?? throw new CompileException($"expression has no type: {op.Syntax}", op.Syntax.GetLocation()), Map);

    void EmitDiscard(IOperation op)
    {
        if (D.Dynamic.Any && D.Dynamic.Find(op, "op") is { } ds
            && op is not (ICompoundAssignmentOperation { Target: not (IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation) }
                or IIncrementOrDecrementOperation { Target: not (IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation) }))
        {
            var dso = EmitDynamicSite(op, ds);
            if (!ds.Void) Drop(dso, D.ObjectType);
            return;
        }
        switch (op)
        {
            case ISimpleAssignmentOperation sa: if (!TryInstrumentedWrite(sa, () => EmitAssign(sa, needResult: false), out _)) EmitAssign(sa, needResult: false); return;
            case ICompoundAssignmentOperation ca: if (!TryInstrumentedWrite(ca, () => EmitCompound(ca, needResult: false), out _)) EmitCompound(ca, needResult: false); return;
            case IIncrementOrDecrementOperation inc: if (!TryInstrumentedWrite(inc, () => EmitIncDec(inc, needResult: false), out _)) EmitIncDec(inc, needResult: false); return;
            case ICoalesceAssignmentOperation cas: if (!TryInstrumentedWrite(cas, () => EmitCoalesceAssign(cas, needResult: false), out _)) EmitCoalesceAssign(cas, needResult: false); return;
            case IEventAssignmentOperation ea: EmitEventAssign(ea); return;
            case IDeconstructionAssignmentOperation da: EmitDeconstruction(da); return;
        }
        var t = op.Type;
        var o = EmitValue(op);
        if (t == null || Types.IsVoid(t)) return;
        Drop(o, t);
    }

    void EmitCondition(IOperation op)
    {
        var o = EmitValue(op);
        // (the graph's read of an interpolated string handler's `out bool` has no type: its slot's)
        var t = op is IFlowCaptureReferenceOperation { Type: null } r && _captureSlots.TryGetValue(r.Id, out var s) ? s.type : TypeOf(op);
        if (Types.PrimOf(t) != Prim.Bool) throw new CompileException("condition is not bool: " + op.Syntax, op.Syntax.GetLocation());
    }

    /// <summary>Emit a value converted to <paramref name="target"/> (implicit conversions the operation tree left out).</summary>
    Own EmitValueAs(IOperation op, ITypeSymbol target)
    {
        var o = EmitValue(op);
        if (op.Type == null) return o; // null literal etc.
        var from = TypeOf(op);
        ConvertTop(from, target, ref o);
        return o;
    }

    Own EmitValue(IOperation op)
    {
        if (D.ValueHooks.Count > 0 && !ReferenceEquals(op, _hookedValue) && op.Syntax != null && op.Type != null
            && D.ValueHooks.TryGetValue((Driver.SiteKey(op.Syntax), op.Kind), out var valueHooks))
            return EmitValueHooked(op, valueHooks);
        int save = E.CurrentPos;
        if (s_countRc && IsCountedPush(op)) E.PendingProbes++;
        // (a value of an immortal type is never counted: Types.IsImmortalType)
        try
        {
            var o = EmitValueCore(op);
            if (o != Own.None && op.Type != null && Types.IsImmortalType(op.Type)) return Own.None;
            if (o == Own.Unstable && D.ThreadMode)
            {
                // (a place no other thread stores: read as without threads, Sharing.PriorThreadLocal)
                if (UnsharedPlace(op)) { _plainLoadAt = E.Fn.Body.Count; return o; }
                return CountedLoad(o, op);
            }
            return o;
        }
        catch (CompileException ex) when (ex.Location == null) { throw new CompileException(ex.Message, op.Syntax.GetLocation()); }
        finally { E.CurrentPos = save; }
    }

    static readonly bool s_countRc = Environment.GetEnvironmentVariable("CS2WASM_COUNT_RC") == "1";

    /// <summary>Threads (Driver.ThreadMode): a reference just read from memory, not counted. Another thread may store
    /// over the place and release the object before this one counts or uses it, so the read becomes a read that
    /// counts in one step (rt.load_retain: under the lock the store takes too), and the value is this code's own.
    /// Not for a place that never changes once others can see it (_plainLoadAt: a readonly field).</summary>
    Own CountedLoad(Own o, IOperation? op = null)
    {
        var b = E.Fn.Body;
        if (o != Own.Unstable || b.Count == 0 || _plainLoadAt == b.Count || s_noCountedLoads) return o;
        var last = b[^1];
        if (last.Op != Op.I32Load || last.Ref != null) return o;
        b.RemoveAt(b.Count - 1);
        if (last.Imm != 0) { E.I32((int)last.Imm); E.Op(Op.I32Add); }
        // (a slot of an object no other thread can reach: no other thread stores there, TypedCounts.cs)
        ITypeSymbol? container = op switch
        {
            IFieldReferenceOperation { Field.IsStatic: false, Instance: { Type: not null } inst } when !TypeOf(inst).IsValueType => TypeOf(inst),
            IArrayElementReferenceOperation { ArrayReference.Type: not null } ae => TypeOf(ae.ArrayReference),
            _ => null,
        };
        E.Call(container != null && op!.Type != null ? Rt.LoadRetainOf(container, TypeOf(op)) : Rt.LoadRetain);
        return Own.Owned;
    }
    /// <summary>Where (body length) a load ended whose place does not change: CountedLoad leaves it.</summary>
    int _plainLoadAt = -1;
    /// <summary>CS2WASM_NO_COUNTED_LOADS=1 (to see tests/Conformance/081 fail: reads that count are what it tests).</summary>
    static readonly bool s_noCountedLoads = Environment.GetEnvironmentVariable("CS2WASM_NO_COUNTED_LOADS") == "1";

    /// <summary>The baseline of the count measurement (CS2WASM_COUNT_RC): a reference value an expression produces from a
    /// variable, a field, an element, a property, a call or an allocation, which a scheme without ownership analysis
    /// counts up when it is pushed and down when it is consumed (the same values PrintCountStats counts statically).</summary>
    static bool IsCountedPush(IOperation op)
    {
        if (op is not (ILocalReferenceOperation or IParameterReferenceOperation or IFieldReferenceOperation or IArrayElementReferenceOperation
            or IInvocationOperation or IObjectCreationOperation or IArrayCreationOperation or IPropertyReferenceOperation or IInstanceReferenceOperation)) return false;
        var t = op.Type;
        if (t == null || !(t.IsReferenceType || t.TypeKind == TypeKind.TypeParameter && !t.IsValueType)) return false;
        return !(op.Parent is IAssignmentOperation asg && ReferenceEquals(asg.Target, op));
    }

    Own EmitValueCore(IOperation op)
    {
        if ((_captureOp != null || _entryCaptures.Count > 0) && TryCaptured(op, out var cap)) return cap;
        if (_machine != null && TryPrecomputed(op, out var pre)) return pre;
        if (D.HasEntryHooks && TryInstrumentedEntry(op, () => EmitValueCore(op), out var entry)) return entry;
        if (op.ConstantValue.HasValue && op is not IAssignmentOperation)
            return EmitConstant(op.ConstantValue.Value, op.Type != null ? TypeOf(op) : null);
        if (op.Type is { SpecialType: SpecialType.System_Boolean } && Fold(op) is bool known) { E.I32(known ? 1 : 0); return Own.None; }
        if (op is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } ub && Utf8Concat(ub) is { } u8bytes) return EmitUtf8Span(u8bytes, TypeOf(ub));
        if (D.Dynamic.Any && TryEmitDynamic(op) is { } dyo) return dyo;
        switch (op)
        {
            // query syntax: Roslyn's translation (Where/Select/GroupBy... calls)
            case ITranslatedQueryOperation tq: return EmitValue(tq.Operation);
            // a lambda converted to Expression<T> (ExpressionTrees.cs)
            case IConversionOperation { Operand: var eop } ec when ec.Type != null && IsExpressionTree(TypeOf(ec))
                && (eop is IAnonymousFunctionOperation || eop is IDelegateCreationOperation { Target: IAnonymousFunctionOperation }):
                return EmitExpressionLambda(eop as IAnonymousFunctionOperation ?? (IAnonymousFunctionOperation)((IDelegateCreationOperation)eop).Target, (INamedTypeSymbol)TypeOf(ec));
            case IConversionOperation cspan when TryEmitConstantSpan(cspan) is { } cso: return cso;
            case IConversionOperation c: return EmitConversion(c);
            case ILocalReferenceOperation l:
                if (IsCaptured(l.Local)) return LoadCaptured(l.Local);
                if (TryEmitLocalMove(l, out var moved)) return moved;
                { var lo = LoadVar(LocalVar(l.Local)); return lo == Own.Stable && WrittenDuringUse(l.Local) ? Own.Unstable : lo; }
            case IParameterReferenceOperation p:
                if (IsPrimaryParamHere(p.Parameter)) { var ps = PrimarySlot(p.Parameter); PushPrimaryThis(); if (Types.IsStruct(ps.Type)) { E.I32(ps.Offset); E.Op(Op.I32Add); return Own.None; } E.Load(Types.LoadOp(ps.Type), ps.Offset); return Types.IsRef(ps.Type) ? Own.Unstable : Own.None; }
                if (IsCaptured(p.Parameter)) return LoadCaptured(p.Parameter);
                if (IsMovedParamUse(p))
                {
                    // an owned parameter's use that takes its count: the parameter is cleared (OwnedParams.cs)
                    var mv = _vars[p.Parameter];
                    E.Get(mv.Local); E.I32(0); E.Set(mv.Local);
                    return Own.Owned;
                }
                if (IsMovedStructParamUse(p)) { var sv = LoadVar(_vars[p.Parameter]); _freshParam = E.Body.Count; _structParamLoads++; return sv; }
                { var po = LoadVar(_vars[p.Parameter]); return po == Own.Stable && WrittenDuringUse(p.Parameter) ? Own.Unstable : po; }
            case IInstanceReferenceOperation ir:
                {
                    var to = EmitThis(ir);
                    // a primitive's own methods get its address: `this` as a value is what is stored there
                    if (ir.ReferenceKind == InstanceReferenceKind.ContainingTypeInstance && _closure == null && M.ContainingType != null
                        && T.Resolve(M.ContainingType, Map) is var pct && Types.PrimOf(pct) != Prim.None && pct.TypeKind != TypeKind.Enum)
                        E.Load(Types.LoadOp(pct));
                    return to;
                }
            case IFieldReferenceOperation f: return TryInstrumentedRead(f, () => EmitFieldLoad(f), out var r1) ? r1 : EmitFieldLoad(f);
            case IPropertyReferenceOperation pr: return TryInstrumentedRead(pr, () => EmitPropertyGet(pr), out var r2) ? r2 : EmitPropertyGet(pr);
            case IEventReferenceOperation evr when Driver.IsFieldLikeEvent(evr.Event):
                {
                    int off = EventStorage(evr.Event, () => { var o = EmitValue(evr.Instance!); ToTransient(o, TypeOf(evr.Instance!)); if (!IsKnownNonNull(evr.Instance!)) NullCheckKeep(); });
                    E.Load(Op.I32Load, off);
                    return Own.Unstable;
                }
            // a[range]: a new array of the slice, as C# defines it (RuntimeHelpers.GetSubArray)
            case IArrayElementReferenceOperation { Indices: [var rix] } ae when rix.Type is { Name: "Range" } rt && rt.ContainingNamespace?.ToDisplayString() == "System":
                {
                    var et = ((IArrayTypeSymbol)TypeOf(ae.ArrayReference)).ElementType;
                    var gsa = D.LibType("System.Runtime.CompilerServices.RuntimeHelpers").GetMembers("GetSubArray").OfType<IMethodSymbol>().First();
                    return EmitCallResolved(D.CanonicalMethod(gsa.Construct(T.Canonical(et))), null, [ae.ArrayReference, rix], false);
                }
            case IArrayElementReferenceOperation ae: return EmitArrayElementLoad(ae);
            case IBinaryOperation b: return EmitBinary(b);
            case IUnaryOperation u: return EmitUnary(u);
            case IRangeOperation { Method: { IsStatic: true } factory } ro:
                return EmitCall(factory, null, new[] { ro.LeftOperand, ro.RightOperand }.Where(o => o != null).ToArray()!, false, ro.Syntax);
            case IRangeOperation ro: return EmitRange(ro);
            case ISimpleAssignmentOperation sa: return TryInstrumentedWrite(sa, () => EmitAssign(sa, needResult: true), out var w1) ? w1 : EmitAssign(sa, needResult: true);
            case ICompoundAssignmentOperation ca: return TryInstrumentedWrite(ca, () => EmitCompound(ca, needResult: true), out var w2) ? w2 : EmitCompound(ca, needResult: true);
            case IIncrementOrDecrementOperation inc: return TryInstrumentedWrite(inc, () => EmitIncDec(inc, needResult: true), out var w3) ? w3 : EmitIncDec(inc, needResult: true);
            case ICoalesceAssignmentOperation cas: return TryInstrumentedWrite(cas, () => EmitCoalesceAssign(cas, needResult: true), out var w4) ? w4 : EmitCoalesceAssign(cas, needResult: true);
            case IInvocationOperation inv: return EmitInvocation(inv);
            case IObjectCreationOperation oc: return EmitObjectCreation(oc);
            case ITypeParameterObjectCreationOperation tpc: return EmitTypeParamCreation(tpc);
            case IArrayCreationOperation ac: return EmitArrayCreation(ac);
            case IConditionalOperation co: return EmitConditionalExpr(co);
            case ICoalesceOperation coal: return EmitCoalesce(coal);
            case IIsTypeOperation it: return EmitIsType(it);
            case IIsPatternOperation ip: return EmitIsPattern(ip);
            case ISwitchExpressionOperation se: return EmitSwitchExpression(se);
            case IInterpolatedStringOperation iso: return EmitInterpolatedString(iso);
            case IImplicitIndexerReferenceOperation ii: return EmitImplicitIndexer(ii, false);
            case IFunctionPointerInvocationOperation fpi: return EmitFunctionPointerInvocation(fpi);
            case IAddressOfOperation { Reference: IMethodReferenceOperation } mao: return TryEmitMethodAddress(mao)!.Value;
            case IInterpolatedStringHandlerCreationOperation hc: return EmitHandlerCreation(hc);
            case IInterpolatedStringHandlerArgumentPlaceholderOperation ph: return EmitHandlerPlaceholder(ph);
            case IInlineArrayAccessOperation ia when TypeOf(ia.Argument) is INamedTypeSymbol { Name: "Range" }:
                {
                    // buffer[a..b]: a span over the inline array, sliced
                    var spanT = TypeOf(ia);
                    var own = Own.None;
                    EmitAddress(ia.Instance);
                    ConvertValue(TypeOf(ia.Instance), spanT, false, ref own, false, null);
                    int sp = E.AddLocal(ValType.I32); E.Set(sp);
                    int len = E.AddLocal(ValType.I32); E.I32(Types.InlineArrayLength(TypeOf(ia.Instance))!.Value); E.Set(len);
                    int start = E.AddLocal(ValType.I32), end = E.AddLocal(ValType.I32);
                    if (ia.Argument is not IRangeOperation ro) throw new CompileException("inline array range", ia.Syntax.GetLocation());
                    EmitIndexOffset(ro.LeftOperand, len, true); E.Set(start);
                    EmitIndexOffset(ro.RightOperand, len, false); E.Set(end);
                    var slice = ((INamedTypeSymbol)T.Canonical(spanT)).GetMembers("Slice").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 2);
                    return CallWith(slice, false, () => E.Get(sp), () => { E.Get(start); E.Get(end); E.Get(start); E.Op(Op.I32Sub); }, null, ia.Syntax);
                }
            case IInlineArrayAccessOperation ia2:
                {
                    var et = TypeOf(ia2);
                    int off = EmitAddressBase(ia2);
                    if (Types.IsStruct(et)) { if (off != 0) { E.I32(off); E.Op(Op.I32Add); } return Own.None; }
                    E.Load(Types.LoadOp(et), off);
                    return Types.IsRef(et) ? Own.Unstable : Own.None;
                }
            case IUtf8StringOperation u8: return EmitUtf8Span(System.Text.Encoding.UTF8.GetBytes(u8.Value), TypeOf(u8));
            case IDefaultValueOperation dv: return EmitDefault(TypeOf(dv));
            case IConditionalAccessOperation ca2: return EmitConditionalAccess(ca2);
            case IConditionalAccessInstanceOperation: return EmitConditionalInstance();
            case IDelegateCreationOperation dc: return EmitDelegateCreation(dc);
            case IThrowOperation th: EmitThrow(th.Exception); E.Op(Op.Unreachable); return th.Type != null && Types.IsRef(TypeOf(th)) ? Own.Owned : Own.None;
            case IAddressOfOperation ao: EmitAddress(ao.Reference); return Own.None;
            case IDeclarationExpressionOperation de: return EmitValue(de.Expression);
            case ISizeOfOperation so: E.I32(T.StorageOf(T.Resolve(so.TypeOperand, Map)).size); return Own.None;
            case IAnonymousFunctionOperation af: throw new CompileException("lambda without delegate conversion", af.Syntax.GetLocation());
            case IParenthesizedOperation pa: return EmitValue(pa.Operand);
            case IObjectOrCollectionInitializerOperation: throw new CompileException("initializer outside creation");
            case INameOfOperation: break;
            case ITupleOperation tu: return EmitTuple(tu);
            case ITupleBinaryOperation tb: return EmitTupleBinary(tb);
            case IAwaitOperation: throw new CompileException(_machine == null ? "await outside an async method" : "internal: await result not computed", op.Syntax.GetLocation());
            case IFlowCaptureOperation fc: return EmitFlowCapture(fc);
            case IFlowCaptureReferenceOperation fcr: return EmitFlowCaptureReference(fcr);
            case ICaughtExceptionOperation: return EmitCaughtException();
            case IIsNullOperation isn: return EmitIsNull(isn);
            case IFlowAnonymousFunctionOperation fa: throw new CompileException("lambda without delegate conversion", fa.Syntax.GetLocation());
            case IAnonymousObjectCreationOperation aoc: return EmitAnonymousCreation(aoc);
            case ITypeOfOperation to:
                {
                    var tt = T.Resolve(to.TypeOperand, Map);
                    if (tt.TypeKind == TypeKind.Enum) D.MarkConstructed(tt); // Enum's API reads its table
                    D.TypeOfTargets.Add((tt, D.CurrentFn));
                    E.Tid(D.TypeId(tt));
                    var fromId = D.LibType("System.Type").GetMembers("FromId").OfType<IMethodSymbol>().First();
                    E.Call(D.Method(fromId).Fn);
                    return Own.Owned;
                }
            case ICollectionExpressionOperation cspan2 when TryEmitConstantSpan(cspan2) is { } cso2: return cso2;
            case ICollectionExpressionOperation ce: return EmitCollectionExpression(ce);
            case IWithOperation wo: return EmitWith(wo);
        }
        if (op.Kind == OperationKind.None) return EmitPointerOp(op);
        throw new CompileException($"unsupported expression '{op.Syntax}' ({op.Kind})", op.Syntax.GetLocation());
    }

    Own EmitConstant(object? value, ITypeSymbol? t)
    {
        if (value == null)
        {
            if (t != null && Types.IsStruct(t)) return EmitDefault(t);
            E.I32(0); return t != null && Types.WasmType(t) is { } vt && vt != ValType.I32 ? PushZero(vt) : Own.Stable;
        }
        if (value is string s) { E.I32Addr(D.StringLiteral(s)); return Own.Stable; }
        var p = t != null ? Types.PrimOf(t) : Prim.None;
        if (p == Prim.None)
            p = value switch { bool => Prim.Bool, char => Prim.Char, sbyte => Prim.I8, byte => Prim.U8, short => Prim.I16, ushort => Prim.U16, int => Prim.I32, uint => Prim.U32, long => Prim.I64, ulong => Prim.U64, float => Prim.F32, double => Prim.F64, _ => Prim.None };
        if (value is decimal dec) return EmitDecimalConstant(dec);
        switch (Types.WasmPrim(p))
        {
            case ValType.I64: E.I64(value switch { ulong u => unchecked((long)u), _ => Convert.ToInt64(value) }); break;
            case ValType.F32: E.F32(Convert.ToSingle(value)); break;
            case ValType.F64: E.F64(Convert.ToDouble(value)); break;
            default:
                E.I32(value switch
                {
                    bool b => b ? 1 : 0, char c => c, uint u => unchecked((int)u), ulong ul => unchecked((int)ul), long l => unchecked((int)l),
                    float f => (int)f, double d => (int)d,
                    _ => Convert.ToInt32(value),
                });
                break;
        }
        return Own.None;
    }

    Own PushZero(ValType vt)
    {
        E.Op(Op.Drop);
        switch (vt) { case ValType.I64: E.I64(0); break; case ValType.F32: E.F32(0); break; case ValType.F64: E.F64(0); break; default: E.I32(0); break; }
        return Own.None;
    }

    Own EmitDefault(ITypeSymbol t)
    {
        if (Types.IsStruct(t))
        {
            // (a pooled temporary may hold an earlier struct's bytes: in a loop, the last iteration's)
            int off = TempStruct(t);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.I32(0); E.I32(T.StorageOf(t).size); E.Op(Op.MemoryFill);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
            return Own.None;
        }
        var vt = Types.WasmType(t) ?? ValType.I32;
        switch (vt) { case ValType.I64: E.I64(0); break; case ValType.F32: E.F32(0); break; case ValType.F64: E.F64(0); break; default: E.I32(0); break; }
        return Types.IsRef(t) ? Own.Stable : Own.None;
    }

    // =====================================================================
    // variables
    // =====================================================================
    Own LoadVar(Var v)
    {
        if (!v.InMemory) { E.Get(v.Local); return Types.IsRef(v.Type) ? Own.Stable : Own.None; }
        E.Get(v.AddrLocal);
        if (Types.IsStruct(v.Type)) { if (v.Offset != 0) { E.I32(v.Offset); E.Op(Op.I32Add); } return Own.None; }
        E.Load(Types.LoadOp(v.Type), v.Offset);
        if (!Types.IsRef(v.Type)) return Own.None;
        return v.AddrLocal == _fb && !IsRefLocal(v) ? Own.Stable : Own.Unstable;
    }

    void PushVarAddress(Var v)
    {
        if (!v.InMemory) throw new CompileException("address of register variable");
        E.Get(v.AddrLocal);
        if (v.Offset != 0) { E.I32(v.Offset); E.Op(Op.I32Add); }
    }

    /// <summary>Value on the stack is stored into the variable, taking ownership as needed.</summary>
    void StoreVar(Var v, Own o)
    {
        var t = v.Type;
        if (!v.InMemory)
        {
            if (Types.IsRef(t) && !Types.IsImmortalType(t))
            {
                ToOwned(o, t);
                int nv = Scratch(ValType.I32); E.Set(nv);
                E.Get(v.Local); ReleaseOf(t);
                E.Get(nv); E.Set(v.Local);
                FreeScratch(ValType.I32, nv);
            }
            else E.Set(v.Local);
            return;
        }
        StoreToAddressFromStack(() => E.Get(v.AddrLocal), v.Offset, t, o, frame: v.AddrLocal == _fb && !IsRefLocal(v));
        if (v.AddrLocal != _fb && T.NeedsRc(t)) D.NoteFieldWrite(E, Driver.ElementsKey);
    }

    /// <summary>Stores the value on top of the stack at (pushAddr() + offset), handling counts and struct copies.</summary>
    void StoreToAddressFromStack(Action pushAddr, int offset, ITypeSymbol t, Own o, Arenas.Site? site = null, Action? pushContainer = null, WasmFunction? pin = null,
                                 Publish pub = Publish.Unknown, Action? pubContainer = null, string? initKey = null, bool frame = false, bool fresh = false, bool owned = false, bool iso = false, bool initNull = false,
                                 ITypeSymbol? structContainer = null, ITypeSymbol? valueType = null, bool viaPointer = false, int lastLocal = -1)
    {
        if (Types.IsStruct(t))
        {
            bool move = TakeFresh(t);
            int src = Scratch(ValType.I32); E.Set(src);
            pushAddr(); if (offset != 0) { E.I32(offset); E.Op(Op.I32Add); }
            // (threads: into an object of a class, or an array of a type, the last compilation found no other thread
            // reaches, the stores of its references take no runtime lock, as a reference field's: StoreLock below)
            bool locked = !(structContainer != null && D.ThreadMode && D.Sharing is { } shs0 && shs0.PriorThreadLocal(structContainer));
            // (a fresh temporary's counts are taken: the old value released, no new counts)
            // (the container's type, where known: a container that is never a group's member takes plain stores)
            E.Get(src); if (move) E.Call(D.Rt.MoveAssign(t, shared: !frame, locked, structContainer)); else CopyStructAssign(t, shared: !frame, locked, structContainer);
            FreeScratch(ValType.I32, src);
            return;
        }
        // (a store proven to close no cycle is linked as such, whoever holds the container: the owned local's link
        // would make the site general)
        if (iso && !Driver.NoChainStores) owned = false;
        var vt = Types.WasmType(t)!.Value;
        // (an immortal object: stored as it is, nothing counted, nothing to publish: its count word marks it shared)
        if (Types.IsImmortalType(t) && site == null && pin == null) { int iv = Scratch(vt); E.Set(iv); pushAddr(); E.Get(iv); E.Store(Op.I32Store, offset); FreeScratch(vt, iv); return; }
        int val = Scratch(vt);
        if (Types.IsRef(t)) ToOwned(o, t);
        E.Set(val);
        if (Types.IsRef(t))
        {
            // section 4.3: a site that can close a cycle links the value into the container's arena (resolved later)
            void PushSlot() { if (site!.IsArray) { E.I32(0); return; } pushAddr(); if (offset != 0) { E.I32(offset); E.Op(Op.I32Add); } }
            // A slot the code knows by its address alone (through a `ref`, a span, a struct's own method), not one in
            // this function's frame or a static: the runtime finds the object it is in (Arenas.LinkAt), so the store
            // is the one the object's own code would have made.
            bool byAddress = site == null && pin == null && !frame && initKey == null && !Types.IsImmortalType(t);
            // (a field an object initializer sets on a new object its constructors left null: no old value to
            // exchange or release. Its constructor may have made it a member of a group, by a store of its own: the
            // link of a store into a new object stays, which takes a plain object's way when it is one)
            if (initNull && pin == null)
            {
                if (site != null) { E.Get(val); pushContainer!(); PushSlot(); E.Call(site.LinkFresh); E.Set(val); }
                EmitPublish(val, pub, pubContainer, () => { pushAddr(); if (offset != 0) { E.I32(offset); E.Op(Op.I32Add); } });
                pushAddr(); E.Get(val); E.Store(Op.I32Store, offset);
                FreeScratch(vt, val);
                return;
            }
            // (threads: a general store into an object another thread may reach is one step, StoreLock; not one into
            // an object the last compilation found no other thread reaches: Api.cs compiles again should that change)
            bool storeLock = site != null && D.ThreadMode && !fresh && !owned && !(D.Sharing is { } shs && shs.PriorThreadLocal(site.Container));
            int took = storeLock || byAddress && D.ThreadMode ? Scratch(ValType.I32) : -1;
            if (storeLock) { pushContainer!(); pushAddr(); if (offset != 0) { E.I32(offset); E.Op(Op.I32Add); } E.Call(site!.StoreLock); E.Set(took); }
            // (the last use of the local that holds the container, LocalMoves.cs, IsLastContainer: the link may give
            // the local's count back, where it would make the container hold its owners until the scope's end; the
            // local is cleared then, before anything else runs)
            bool last = lastLocal >= 0 && site != null && owned && !fresh && !storeLock;
            if (last) { pushContainer!(); E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "ArenaLastBegin", 1).Fn); }
            if (site != null) { E.Get(val); pushContainer!(); PushSlot(); E.Call(fresh ? site.LinkFresh : owned ? site.LinkOwned : iso ? site.LinkIso : site.Link); E.Set(val); }
            else if (byAddress)
            {
                // (the type graph learns what this store can put where: RefStores.cs)
                D.Arenas.RefStore(t, valueType, viaPointer);
                if (D.ThreadMode) { pushAddr(); if (offset != 0) { E.I32(offset); E.Op(Op.I32Add); } E.Call(D.Arenas.StoreLockAt(t, true)); E.Set(took); }
                E.Get(val); pushAddr(); if (offset != 0) { E.I32(offset); E.Op(Op.I32Add); } E.Call(D.Arenas.LinkAt(t)); E.Set(val);
            }
            if (last) { pushContainer!(); E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "ArenaLastEnd", 1).Fn); E.Set(lastLocal); }
            if (pin != null) { E.Get(val); E.Call(pin); E.Set(val); }
            EmitPublish(val, pub, pubContainer, () => { pushAddr(); if (offset != 0) { E.I32(offset); E.Op(Op.I32Add); } });
            int a = Scratch(ValType.I32);
            int old = Scratch(ValType.I32);
            if (frame)
            {
                pushAddr(); E.Tee(a);
                E.Load(Op.I32Load, offset);
                E.Set(old);
                E.Get(a); E.Get(val); E.Store(Op.I32Store, offset);
            }
            else
            {
                // (an exchange: rt.store_ref)
                pushAddr(); if (offset != 0) { E.I32(offset); E.Op(Op.I32Add); }
                // (into an object no other thread can reach: a plain exchange, TypedCounts.cs)
                var storeRef = site != null && !site.IsArray ? Rt.StoreRefOf(site.Container) : site != null ? Rt.StoreRefOf(site.Container) : Rt.StoreRef;
                if (took >= 0)
                {
                    // (in a store's region, 1, the exchange is the region's, which holds the place: Rt.RegionExchange)
                    E.Set(a);
                    E.Get(took); E.I32(1); E.Op(Op.I32Eq);
                    E.If(ValType.I32); E.Get(a); E.Get(val); E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "RegionExchange", 2).Fn);
                    E.Else(); E.Get(a); E.Get(val); E.Call(storeRef); E.End();
                }
                else { E.Get(val); E.Call(storeRef); }
                E.Set(old);
            }
            E.Get(old);
            // (a constructor's first store to its object's field replaces null: InitStores.cs decides at the end)
            if (site != null)
            {
                pushContainer!(); PushSlot(); E.Call(fresh || owned ? site.UnlinkFresh : site.Unlink);
                // (the class of what the place held, by this code's own types: a List<T>'s or a Dictionary's store,
                // compiled for a T that is a sealed class whose objects hold only leaves, names that class, and
                // where the site turns out plain the release is the class's own: StoreHint, Arenas.Resolve)
                if (StoreHint(t) is int sh and not 0) E.Body[^1] = E.Body[^1] with { Imm2 = sh };
                if (storeLock) { E.Get(took); E.Call(site.StoreUnlock); }
            }
            else if (byAddress) { pushAddr(); if (offset != 0) { E.I32(offset); E.Op(Op.I32Add); } E.Call(D.Arenas.UnlinkAt(t)); if (StoreHint(t) is int ah and not 0) E.Body[^1] = E.Body[^1] with { Imm2 = ah }; if (D.ThreadMode) { E.Get(took); E.Call(D.Arenas.StoreLockAt(t, false)); } }
            else if (initKey != null) E.Call(D.InitStoreRelease(E.Fn, initKey));
            // (what the place held is of the place's class: named, as a local's release is)
            else ReleaseOf(t);
            if (took >= 0) FreeScratch(ValType.I32, took);
            FreeScratch(ValType.I32, old); FreeScratch(ValType.I32, a);
        }
        else { pushAddr(); E.Get(val); E.Store(Types.StoreOp(t), offset); }
        FreeScratch(vt, val);
    }

    void CopyStructInit(ITypeSymbol t)
    {
        // stack: dst src
        if (T.LayoutOf(t).HasRefs) { E.Call(Rt.CopyInit(t)); return; }
        E.I32(T.StorageOf(t).size); E.Op(Op.MemoryCopy);
    }

    void CopyStructAssign(ITypeSymbol t, bool shared = true, bool locked = true, ITypeSymbol? container = null)
    {
        if (T.LayoutOf(t).HasRefs) { E.Call(Rt.CopyAssign(t, shared, locked, container)); return; }
        E.I32(T.StorageOf(t).size); E.Op(Op.MemoryCopy);
    }

    void EmitStoreToVar(ILocalSymbol sym, IOperation value) => EmitStoreToVarFrom(sym, value.Type != null ? TypeOf(value) : T.Resolve(sym.Type, Map), () => EmitValue(value));

    void EmitStoreToVarFrom(ISymbol sym, ITypeSymbol valueType, Func<Own> produce)
    {
        if (IsCaptured(sym)) { StoreCaptured(sym, valueType, produce); return; }
        var v = sym is ILocalSymbol l ? LocalVar(l) : _vars[sym];
        var o = produce();
        ConvertTop(valueType, v.Type, ref o);
        StoreVar(v, o);
    }

    /// <summary>A primary-constructor parameter used outside that constructor lives in the object.</summary>
    bool IsPrimaryParamHere(IParameterSymbol p) =>
        p.ContainingSymbol is IMethodSymbol c && Driver.IsPrimaryCtor(c) && Driver.IsCapturedPrimaryParam(p) &&
        !(SymbolEqualityComparer.Default.Equals(c.OriginalDefinition, M.OriginalDefinition) && _closure == null && _machine == null);

    FieldSlot PrimarySlot(IParameterSymbol p)
    {
        var owner = (INamedTypeSymbol)T.Resolve(p.ContainingSymbol.ContainingType, Map);
        return T.LayoutOf(owner).Fields.First(f => SymbolEqualityComparer.Default.Equals(f.Symbol, p.OriginalDefinition));
    }

    void PushPrimaryThis()
    {
        if (_closure != null) { LoadCapturedThis(); return; }
        E.Get(_thisLocal);
    }

    Own EmitThis(IInstanceReferenceOperation ir)
    {
        switch (ir.ReferenceKind)
        {
            case InstanceReferenceKind.InterpolatedStringHandler: return EmitHandlerReference();
            case InstanceReferenceKind.ImplicitReceiver:
                {
                    var (local, type) = _implicitReceivers[^1];
                    E.Get(local);
                    return Types.IsRef(type) ? Own.Stable : Own.None;
                }
            case InstanceReferenceKind.PatternInput:
                {
                    var (local, type) = _patternInputs[^1];
                    E.Get(local);
                    return Types.IsRef(type) ? Own.Stable : Own.None;
                }
        }
        if (_closure != null) return LoadCapturedThis();
        if (_thisLocal < 0) throw new CompileException("'this' in static context");
        E.Get(_thisLocal);
        return M.ContainingType.IsValueType ? Own.None : Own.Stable;
    }

    readonly List<(int local, ITypeSymbol type)> _patternInputs = [];

    // =====================================================================
    // addresses (lvalues in memory)
    // =====================================================================
    bool IsAddressable(IOperation op)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        return op switch
        {
            ILocalReferenceOperation l => IsCaptured(l.Local) || LocalVar(l.Local).InMemory,
            IParameterReferenceOperation p => IsPrimaryParamHere(p.Parameter) || IsCaptured(p.Parameter) || _vars[p.Parameter].InMemory,
            IFieldReferenceOperation f => f.Field.IsStatic || f.Instance == null || !TypeOf(f.Instance).IsValueType || IsAddressable(f.Instance),
            IArrayElementReferenceOperation => true,
            IInstanceReferenceOperation iro => iro.ReferenceKind == InstanceReferenceKind.InterpolatedStringHandler ? Types.IsStruct(TypeOf(iro)) : M.ContainingType?.IsValueType == true || _closure != null,
            IPropertyReferenceOperation pr => pr.Property.ReturnsByRef || pr.Property.ReturnsByRefReadonly,
            IImplicitIndexerReferenceOperation { IndexerSymbol: IPropertySymbol ip } => ip.ReturnsByRef || ip.ReturnsByRefReadonly,
            IInlineArrayAccessOperation => true,
            IInvocationOperation inv => inv.TargetMethod.ReturnsByRef || inv.TargetMethod.ReturnsByRefReadonly,
            IDeclarationExpressionOperation => true,
            IConditionalOperation { IsRef: true } => true,
            IInterpolatedStringHandlerArgumentPlaceholderOperation { PlaceholderKind: InterpolatedStringArgumentPlaceholderKind.TrailingValidityArgument } => true,
            _ => op.Kind == OperationKind.None,
        };
    }

    /// <summary>Pushes the address of a storage location.</summary>
    void EmitAddress(IOperation op)
    {
        NoteAddressTaken(op);
        int off = EmitAddressBase(op);
        if (off != 0) { E.I32(off); E.Op(Op.I32Add); }
    }

    /// <summary>A reference slot whose address escapes is written without the arena protocol: its site stays plain.</summary>
    void NoteAddressTaken(IOperation op)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        // (Sharing.cs: stores through this address are recorded where they are made)
        bool precise = _preciseAddr;
        _preciseAddr = false;
        // (where the address goes: a `ref` parameter that keeps no ref of it, or anywhere: Sharing.RefUseOf)
        string? use = _addrUse;
        _addrUse = null;
        List<Sharing.Src>? none = precise ? [] : null;
        switch (op)
        {
            case IFieldReferenceOperation { Field.IsStatic: true } sf when Types.IsRef(TypeOf(sf)):
                D.FieldsAddressTaken.Add(Driver.FieldKey(Owner(sf.Field), StorageField(sf.Field)));
                Sh?.NoteNodeAddress(Sharing.StaticNode(Driver.StaticKey(T.Canonical(Owner(sf.Field)), StorageField(sf.Field))), use);
                if (!precise) Sh?.Static(Driver.StaticKey(T.Canonical(Owner(sf.Field)), StorageField(sf.Field)), TypeOf(sf), IsThreadStatic(StorageField(sf.Field)), [TopSrc(TypeOf(sf))]);
                break;
            case IFieldReferenceOperation { Field.IsStatic: false } f when f.Instance != null && !TypeOf(f.Instance).IsValueType && Types.IsRef(TypeOf(f)):
                D.FieldsAddressTaken.Add(Driver.FieldKey(Owner(f.Field), StorageField(f.Field)));
                if (D.Arenas.FieldSite(Owner(f.Field), T.FieldIn(Owner(f.Field), StorageField(f.Field))) is { } s)
                {
                    s.AddressTaken = true;
                    if (use == null) s.RefDirty = true; else if (use.Length > 0) s.RefUses.Add(use);
                    s.AddValue(T.Canonical(TypeOf(f)), none);
                }
                break;
            // (a struct's field, wherever the struct is: Sharing.StructFieldNode)
            case IFieldReferenceOperation { Field.IsStatic: false } sff when sff.Instance != null && TypeOf(sff.Instance).IsValueType && Types.IsRef(TypeOf(sff)):
                Sh?.NoteNodeAddress(Sharing.StructFieldNode(T.Canonical(Owner(sff.Field)), StorageField(sff.Field)), use);
                break;
            case IArrayElementReferenceOperation ae when TypeOf(ae.ArrayReference) is IArrayTypeSymbol at:
                if (D.Arenas.ArraySite(at) is { } a) { a.AddressTaken = true; a.RefDirty = true; a.AddValue(T.Canonical(at.ElementType), none); }
                break;
        }
    }

    /// <summary>Pushes a base address and returns a constant offset to add.</summary>
    int EmitAddressBase(IOperation op)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        switch (op)
        {
            case IDeclarationExpressionOperation de: return EmitAddressBase(de.Expression);
            case IConditionalOperation { IsRef: true } rc:
                {
                    // c ? ref a : ref b: the address of one of them
                    if (Fold(rc.Condition) is bool known) { EmitAddress(known ? rc.WhenTrue : rc.WhenFalse!); return 0; }
                    EmitCondition(rc.Condition);
                    E.If(ValType.I32); EmitAddress(rc.WhenTrue); E.Else(); EmitAddress(rc.WhenFalse!); E.End();
                    return 0;
                }
            case IInterpolatedStringHandlerArgumentPlaceholderOperation { PlaceholderKind: InterpolatedStringArgumentPlaceholderKind.TrailingValidityArgument }:
                E.Get(_fb); return _handlers[^1].successOffset;
            case IEventReferenceOperation evr when Driver.IsFieldLikeEvent(evr.Event):
                return EventStorage(evr.Event, () => { var o = EmitValue(evr.Instance!); ToTransient(o, TypeOf(evr.Instance!)); if (!IsKnownNonNull(evr.Instance!)) NullCheckKeep(); });
            case IDiscardOperation dis:
                {
                    var dt = TypeOf(dis);
                    int off = Types.IsStruct(dt) ? TempStruct(dt) : TempValue(dt);
                    E.Get(_fb); return off;
                }
            case ILocalReferenceOperation l:
                {
                    if (IsCaptured(l.Local)) return CapturedAddress(l.Local);
                    var v = LocalVar(l.Local);
                    if (!v.InMemory) throw new CompileException("internal: address of register local " + l.Local.Name);
                    E.Get(v.AddrLocal); return v.Offset;
                }
            case IParameterReferenceOperation p:
                {
                    if (IsPrimaryParamHere(p.Parameter)) { PushPrimaryThis(); return PrimarySlot(p.Parameter).Offset; }
                    if (IsCaptured(p.Parameter)) return CapturedAddress(p.Parameter);
                    var v = _vars[p.Parameter];
                    if (!v.InMemory) throw new CompileException("internal: address of register parameter " + p.Parameter.Name);
                    E.Get(v.AddrLocal); return v.Offset;
                }
            case IInstanceReferenceOperation ir:
                if (ir.ReferenceKind == InstanceReferenceKind.ImplicitReceiver) { E.Get(_implicitReceivers[^1].local); return 0; }
                if (ir.ReferenceKind == InstanceReferenceKind.InterpolatedStringHandler) { E.Get(_handlers[^1].addrLocal); return 0; }
                if (ir.ReferenceKind == InstanceReferenceKind.PatternInput) { E.Get(_patternInputs[^1].local); return 0; }
                if (_closure != null) { LoadCapturedThis(); return 0; }
                E.Get(_thisLocal); return 0;
            case IFieldReferenceOperation { Field.RefKind: not RefKind.None } rf:
                {
                    // a ref field holds the address of what it refers to (Types: a pointer slot)
                    int so = EmitFieldSlotBase(rf);
                    E.Load(Op.I32Load, so);
                    return 0;
                }
            case IFieldReferenceOperation f: return EmitFieldSlotBase(f);
            case IArrayElementReferenceOperation ae: return EmitArrayElementAddress(ae);
            case IInvocationOperation inv when inv.TargetMethod.ReturnsByRef || inv.TargetMethod.ReturnsByRefReadonly:
                { var save = _wantRefReturnAddress; _wantRefReturnAddress = true; try { EmitInvocation(inv); } finally { _wantRefReturnAddress = save; } return 0; }
            case IInlineArrayAccessOperation ia when TypeOf(ia.Argument) is not INamedTypeSymbol { Name: "Range" }:
                {
                    // element i of an inline array: bounds-checked, at i * size
                    int n = Types.InlineArrayLength(TypeOf(ia.Instance))!.Value;
                    var et = T.Canonical(TypeOf(ia));
                    int size = T.StorageOf(et).size;
                    int idx = E.AddLocal(ValType.I32);
                    int len = E.AddLocal(ValType.I32); E.I32(n); E.Set(len);
                    EmitIndexOffset(ia.Argument, len, true); E.Set(idx);
                    E.Get(idx); E.I32(n); E.Op(Op.I32GeU); E.If(); E.Call(Rt.ThrowIndex); E.End();
                    EmitAddress(ia.Instance);
                    E.Get(idx); if (size != 1) { E.I32(size); E.Op(Op.I32Mul); } E.Op(Op.I32Add);
                    return 0;
                }
            case IImplicitIndexerReferenceOperation ii when ii.IndexerSymbol is IPropertySymbol { ReturnsByRef: true } or IPropertySymbol { ReturnsByRefReadonly: true }:
                EmitImplicitIndexer(ii, true); return 0;
            case IPropertyReferenceOperation pr when pr.Property.ReturnsByRef || pr.Property.ReturnsByRefReadonly:
                { var save = _wantRefReturnAddress; _wantRefReturnAddress = true; try { EmitPropertyGetCall(pr); } finally { _wantRefReturnAddress = save; } return 0; }
            case IConversionOperation cv when cv.Operand is IAnonymousFunctionOperation == false && IsAddressable(cv.Operand) && T.StorageOf(TypeOf(cv)).size == T.StorageOf(TypeOf(cv.Operand)).size:
                return EmitAddressBase(cv.Operand);
        }
        if (op is IFlowCaptureReferenceOperation fcr && _captureSlots.TryGetValue(fcr.Id, out var cslot))
        {
            E.Get(0);
            if (_addrCaptures.Contains(fcr.Id)) { E.Load(Op.I32Load, cslot.off); return 0; }
            return cslot.off;
        }
        if (op.Kind == OperationKind.None && op.Syntax is not (StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax)) return EmitPointerAddress(op);
        // rvalue: materialize a temporary
        var t = TypeOf(op);
        if (Types.IsStruct(t)) { EmitValue(op); return 0; }
        int tmp = TempValue(t);
        var own = EmitValue(op);
        if (Types.IsRef(t)) ToOwned(own);
        int sv = Scratch(Types.WasmType(t)!.Value); E.Set(sv);
        E.Get(_fb); E.Get(sv); E.Store(Types.StoreOp(t), tmp);
        FreeScratch(Types.WasmType(t)!.Value, sv);
        E.Get(_fb);
        return tmp;
    }

    /// <summary>The base address and offset of a field's own slot (for a ref field: the slot holding the address).</summary>
    int EmitFieldSlotBase(IFieldReferenceOperation f)
    {
        var field = f.Field;
        var storage = StorageField(field);
        if (field.IsStatic)
        {
            EmitStaticInitGuardForField(Owner(field));
            D.StaticReads.Add(Driver.StaticKey(Owner(field), storage));
            if (IsThreadStatic(storage)) { E.GGet(D.TlsBase); return D.ThreadStaticOffset(Owner(field), storage, T.Resolve(field.Type, Map)); }
            E.I32Addr(D.StaticField(Owner(field), storage, T.Resolve(field.Type, Map)));
            return 0;
        }
        var slot = T.FieldIn(Owner(field), storage);
        var it = TypeOf(f.Instance!);
        if (it.IsValueType && (Types.PrimOf(it) == Prim.None || IsAddressable(f.Instance!)))
        {
            if (IsAddressable(f.Instance!)) return EmitAddressBase(f.Instance!) + slot.Offset;
            EmitValue(f.Instance!); return slot.Offset; // rvalue struct: its temp
        }
        var o = EmitValue(f.Instance!);
        ToTransient(o, it);
        if (!IsKnownNonNull(f.Instance!)) NullCheckKeep();
        return slot.Offset;
    }

    /// <summary>A frame slot holding a temporary non-struct value (for `in` arguments of rvalues).</summary>
    int TempValue(ITypeSymbol t)
    {
        int off = AllocFrame(t);
        if (Types.IsRef(t)) RegisterTemp(new Slot { FrameOffset = off, Type = t });
        return off;
    }

    static IFieldSymbol StorageField(IFieldSymbol f) => f.CorrespondingTupleField ?? f;

    bool IsKnownNonNull(IOperation op, int depth = 0)
    {
        while (op is IConversionOperation c && c.Conversion.IsReference) op = c.Operand;
        if (op is ILocalReferenceOperation lr && depth < 4) return LocalKnownNonNull(lr.Local, depth);
        return op is IInstanceReferenceOperation or IObjectCreationOperation or IArrayCreationOperation or IDelegateCreationOperation || (op.ConstantValue.HasValue && op.ConstantValue.Value != null);
    }

    /// <summary>A local of this method assigned only by its declaration, from a value that is never null.</summary>
    bool LocalKnownNonNull(ILocalSymbol l, int depth)
    {
        if (l.IsRef || l.Type.IsValueType || WrittenLocals().Contains(l)) return false;
        var decl = l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (decl == null || !M.OriginalDefinition.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == decl.SyntaxTree && r.Span.Contains(decl.Span))) return false;
        return D.ModelFor(decl.SyntaxTree).GetOperation(decl) is IVariableDeclaratorOperation { Initializer.Value: { } init } && IsKnownNonNull(init, depth + 1);
    }

    void NullCheck()
    {
        E.Call(Rt.NonNull); E.Op(Op.Drop);
    }

    /// <summary>Null-check the value on top of the stack, leaving it there.</summary>
    void NullCheckKeep() => E.Call(Rt.NonNull);

    // =====================================================================
    // fields, properties, arrays
    // =====================================================================
    Own EmitFieldLoad(IFieldReferenceOperation f)
    {
        var ft = T.Resolve(f.Field.Type, Map);
        if (f.Field.IsConst) return EmitConstant(f.Field.ConstantValue, ft);
        // string.Empty: the runtime's to set (upstream declares it without a value): the "" literal
        if (f.Field is { IsStatic: true, Name: "Empty" } && Types.IsString(f.Field.ContainingType)) { E.I32Addr(D.StringLiteral("")); return Own.Stable; }
        // a primitive's own field (int.m_value) is the value itself
        if (!f.Field.IsStatic && f.Instance != null && Types.PrimOf(TypeOf(f.Instance)) is not Prim.None && Types.PrimOf(ft) == Types.PrimOf(TypeOf(f.Instance)))
        {
            // (in the primitive's own methods `this` is its address)
            if (f.Instance is IInstanceReferenceOperation) { int po = EmitAddressBase(f.Instance); E.Load(Types.LoadOp(ft), po); return Own.None; }
            return EmitValue(f.Instance);
        }
        if (f.Field.IsFixedSizeBuffer) { EmitAddress(f); return Own.None; }
        int off = EmitAddressBase(f);
        if (Types.IsStruct(ft)) { if (off != 0) { E.I32(off); E.Op(Op.I32Add); } return Own.None; }
        E.Load(Types.LoadOp(ft), off);
        // (a static's reference, read with a plain load: a loop that cannot store it reads it once, StaticLoads.cs)
        if (f.Field.IsStatic && off == 0 && Types.IsRef(ft) && !D.ThreadMode && !f.Field.IsVolatile && !IsThreadStatic(StorageField(f.Field))
            && E.Fn.Body.Count >= 2 && E.Fn.Body[^1].Op == Op.I32Load && E.Fn.Body[^2] is { Op: Op.I32Const, Imm: 0, Ref: StaticSlot slot })
            D.NoteStaticRefRead(slot, Driver.FieldKey(Owner(f.Field), StorageField(f.Field)), D.StaticInit((INamedTypeSymbol)T.Resolve(T.Canonical(Owner(f.Field)), Map))?.flag);
        // (a readonly field is written by its object's constructors only, before the object is anyone else's)
        if (D.ThreadMode && f.Field.IsReadOnly && !f.Field.IsStatic && M.MethodKind != MethodKind.Constructor) _plainLoadAt = E.Fn.Body.Count;
        if ((!D.ThreadMode || f.Instance != null && UnsharedContainer(TypeOf(f.Instance))) && Types.IsRef(ft) && !f.Field.IsStatic && f.Instance != null && f.Field.ContainingType.TypeKind == TypeKind.Class && (IsStableBase(f.Instance) || QuietLocalBase(f.Instance)))
        {
            var lk = Driver.FieldKey(Owner(f.Field), StorageField(f.Field));
            D.NoteBorrowField(lk, StorageField(f.Field), ft);
            _lastFieldLoad = (E.Fn.Body.Count, lk);
        }
        // a static readonly field keeps its value from its static constructor on (section 4.1: Stable, no count to
        // borrow it); in a static constructor it may still change
        if (Types.IsRef(ft) && f.Field is { IsStatic: true, IsReadOnly: true } && M.MethodKind != MethodKind.StaticConstructor && !s_noStableStatics
            && !f.Field.GetAttributes().Any(a => a.AttributeClass?.Name == "ThreadStaticAttribute"))
            return Own.Stable;
        // a static's value held for a call (a receiver, an argument): as a field of a stable object, with the static's
        // own key, the one a local borrows it by (no threads; FieldBorrows.cs decides at the end: no count unless a
        // store of the static can run meanwhile)
        if (f.Field.IsStatic && !s_noStaticTemps && StaticArrayKey(f) is { } stk) _lastFieldLoad = (E.Fn.Body.Count, stk);
        return Types.IsRef(ft) ? Own.Unstable : Own.None;
    }
    /// <summary>CS2WASM_NO_STATIC_TEMPS=1: a static's value held for a call is counted, as before.</summary>
    static readonly bool s_noStaticTemps = Environment.GetEnvironmentVariable("CS2WASM_NO_STATIC_TEMPS") == "1";

    /// <summary>An object reference that stays the same, and alive, for the whole method: this, a parameter never
    /// assigned, a local never assigned after its declaration (none captured).</summary>
    bool IsStableBase(IOperation op)
    {
        while (op is IConversionOperation { OperatorMethod: null, Conversion.IsReference: true } c) op = c.Operand;
        if (_closure != null || _machine != null) return false;
        return op switch
        {
            IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } => M.ContainingType.TypeKind == TypeKind.Class,
            IParameterReferenceOperation { Parameter: { RefKind: RefKind.None } p } => !IsCaptured(p) && !_assignedParams.Contains(p) && !_assignedParams.Contains(p.OriginalDefinition) && !TypeOf(op).IsValueType,
            ILocalReferenceOperation { Local: { IsRef: false } l } => !IsCaptured(l) && !WrittenLocals().Contains(l) && !TypeOf(op).IsValueType,
            _ => false,
        };
    }

    void EmitStoreField(IFieldSymbol f, Action? recv, IOperation value, bool initializer = false)
    {
        var ft = T.Resolve(f.Type, Map);
        EmitStoreFieldFrom(f, recv, value.Type != null ? TypeOf(value) : ft, srcs: Sh != null ? FlowSrcs(value) : null, produce: () =>
        {
            // `f = default`: copied from zeros of the program's that nothing writes (the store reads its value
            // and copies it; a temporary would be the function's frame, cleared, then released at the statement's end)
            if (Types.IsStruct(ft) && !s_noZeroDefault && IsZeroStruct(value) && Types.Key(T.Canonical(TypeOf(value))) == Types.Key(T.Canonical(ft)))
            {
                int size = T.StorageOf(ft).size;
                E.I32Addr(D.StaticAddress("zeros:" + size, size, 8));
                return Own.None;
            }
            return EmitValue(value);
        }, initializer: initializer);
    }

    static readonly bool s_noZeroDefault = Environment.GetEnvironmentVariable("CS2WASM_NO_ZERO_DEFAULT") == "1";

    /// <summary>A struct value that is all zeros, made with no code of its own: `default`, `new S()` with no
    /// constructor or initializer that runs.</summary>
    static bool IsZeroStruct(IOperation v)
    {
        while (v is IConversionOperation { Conversion.IsIdentity: true } c) v = c.Operand;
        return v is IDefaultValueOperation
            || v is IObjectCreationOperation { Initializer: null, Constructor: { IsImplicitlyDeclared: true, Parameters.Length: 0 } ctor } && !HasInstanceFieldInitializers(ctor.ContainingType);
    }

    /// <summary>A [ThreadStatic] field; or, in a program with threads, a static field of the runtime marked [Intrinsic]
    /// (its scratch state that each thread keeps for itself once threads exist, a plain static without).</summary>
    bool IsThreadStatic(ISymbol f) => f.GetAttributes().Any(a => a.AttributeClass?.Name is "ThreadStaticAttribute" || D.ThreadMode && a.AttributeClass?.Name is "IntrinsicAttribute");

    void EmitStoreFieldFrom(IFieldSymbol f, Action? recv, ITypeSymbol valueType, Func<Own> produce, bool initializer = false, List<Sharing.Src>? srcs = null)
    {
        D.NoteFieldWrite(E, Driver.FieldKey(Owner(f), f));
        var ft = T.Resolve(f.Type, Map);
        int a = E.AddLocal(ValType.I32);
        int off;
        if (f.IsStatic && IsThreadStatic(f)) { EmitStaticInitGuardForField(Owner(f)); E.GGet(D.TlsBase); off = D.ThreadStaticOffset(Owner(f), f, ft); }
        else if (f.IsStatic) { EmitStaticInitGuardForField(Owner(f)); E.I32Addr(D.StaticField(Owner(f), f, ft)); off = 0; }
        else { recv!(); off = T.Field(f, Map).Offset; }
        E.Set(a);
        var o = produce();
        ConvertTop(valueType, ft, ref o);
        Arenas.Site? site = null; WasmFunction? pin = null;
        if (Types.IsRef(ft))
        {
            if (f.IsStatic) pin = D.Arenas.Pin(ft);
            else if ((site = D.Arenas.FieldSite(Owner(f), T.Field(f, Map))) != null) site.AddValue(valueType, srcs);
        }
        if (f.IsStatic) Sh?.Static(Driver.StaticKey(T.Canonical(Owner(f)), f), ft, IsThreadStatic(f), srcs ?? [TopSrc(valueType)]);
        else if (Owner(f).IsValueType && Types.IsRef(ft)) Sh?.Add(Sharing.StructFieldNode(T.Canonical(Owner(f)), f), srcs ?? [TopSrc(valueType)]);
        var pub = f.IsStatic ? (IsThreadStatic(f) ? Publish.None : Publish.Always) : f.ContainingType.IsValueType ? Publish.Unknown : Publish.Container;
        StoreToAddressFromStack(() => E.Get(a), off, ft, o, site, () => E.Get(a), pin, pub, () => E.Get(a),
            initializer && !f.IsStatic ? InitStoreKey(f, Owner(f), null) : null,
            // (a field initializer stores into the object its constructor is making: FreshStores.cs, ThisIsFresh)
            fresh: site != null && initializer && !f.IsStatic && ThisIsFresh(),
            structContainer: !f.IsStatic && !Owner(f).IsValueType && Types.IsStruct(ft) ? Owner(f) : null);
    }

    bool IsDirectAutoProperty(IPropertySymbol p, IOperation? instance)
    {
        if (p.Parameters.Length > 0) return false;
        if (p.IsVirtual || p.IsAbstract || p.IsOverride || p.ContainingType.TypeKind == TypeKind.Interface) return false;
        if (p.IsExtern) return false;
        var bf = Driver.AutoPropertyField(p.OriginalDefinition);
        if (bf == null) return false;
        // accessors must be compiler-generated (no `field` keyword bodies)
        var acc = p.OriginalDefinition.GetMethod ?? p.OriginalDefinition.SetMethod;
        if (p.OriginalDefinition.GetMethod is { } g && Driver.HasBody(g)) return false;
        if (p.OriginalDefinition.SetMethod is { } s && Driver.HasBody(s)) return false;
        return acc != null;
    }

    Own EmitPropertyGet(IPropertyReferenceOperation pr)
    {
        var p = pr.Property;
        if (IsDirectAutoProperty(p, pr.Instance))
        {
            var bf = Driver.AutoPropertyField(p.OriginalDefinition)!;
            var ft = T.Resolve(bf.Type, Map);
            int off;
            var owner = Owner(p);
            ft = T.Resolve(p.Type, Map);
            if (p.IsStatic) { D.StaticReads.Add(Driver.StaticKey(owner, bf)); EmitStaticInitGuardForField(owner); E.I32Addr(D.StaticField(owner, bf, ft)); off = 0; }
            else off = EmitReceiverAddressForField(pr.Instance!) + T.FieldIn(owner, bf).Offset;
            if (Types.IsStruct(ft)) { if (off != 0) { E.I32(off); E.Op(Op.I32Add); } return Own.None; }
            E.Load(Types.LoadOp(ft), off);
            return Types.IsRef(ft) ? Own.Unstable : Own.None;
        }
        // array length is a plain load
        if (p.Name is "Length" && pr.Instance != null && TypeOf(pr.Instance) is IArrayTypeSymbol)
        {
            var o = EmitValue(pr.Instance);
            ToTransient(o, TypeOf(pr.Instance));
            NullCheckKeep();
            E.Emit(Op.I32Load, 12, 2, Loops.Length);
            return Own.None;
        }
        return EmitPropertyGetCall(pr);
    }

    IFieldSymbol SubstField(IFieldSymbol bf, IOperation instance) => bf;

    int EmitReceiverAddressForField(IOperation instance)
    {
        var it = TypeOf(instance);
        if (it.IsValueType && Types.PrimOf(it) == Prim.None)
        {
            if (IsAddressable(instance)) return EmitAddressBase(instance);
            EmitValue(instance); return 0;
        }
        var o = EmitValue(instance);
        ToTransient(o, it);
        if (!IsKnownNonNull(instance)) NullCheckKeep();
        return 0;
    }

    Own EmitPropertyGetCall(IPropertyReferenceOperation pr)
    {
        var getter = pr.Property.GetMethod ?? throw new CompileException("property has no getter: " + pr.Property.Name);
        if (getter.IsStatic && getter.ContainingType.TypeKind == TypeKind.Interface && pr.ConstrainedToType is { } ctt)
        {
            var impl = D.Implementation(T.Resolve(ctt, Map), SubstMethod(getter)) ?? SubstMethod(getter);
            return EmitCallOps(impl, null, pr.Arguments.Cast<IOperation>().ToList(), false, pr.Syntax, alreadyResolved: true);
        }
        return EmitCall(getter, pr.Instance, pr.Arguments.Cast<IOperation>().ToList(), IsVirtualCall(pr.Instance, getter), pr.Syntax);
    }

    bool IsVirtualCall(IOperation? instance, IMethodSymbol m)
    {
        if (m.IsStatic) return false;
        if (instance is IInstanceReferenceOperation { Syntax: BaseExpressionSyntax }) return false;
        return m.IsVirtual || m.IsAbstract || m.IsOverride || m.ContainingType.TypeKind == TypeKind.Interface;
    }

    Own EmitArrayElementLoad(IArrayElementReferenceOperation ae)
    {
        var et = TypeOf(ae);
        int off = EmitArrayElementAddress(ae);
        if (Types.IsStruct(et)) { E.I32(off); E.Op(Op.I32Add); return Own.None; }
        E.Load(Types.LoadOp(et), off);
        if (!Types.IsRef(et)) return Own.None;
        // an element of an array that stays put (a stable reference, or a field of one): held for a call, it needs no
        // count unless an element store can run meanwhile (FieldBorrows.cs decides at the end)
        if (ae.Indices.Length == 1 && (!D.ThreadMode || UnsharedPlace(ae)))
        {
            var ar = ae.ArrayReference;
            while (ar is IConversionOperation { OperatorMethod: null, Conversion.IsReference: true } ac) ar = ac.Operand;
            if (D.ThreadMode && ar is IFieldReferenceOperation { Instance: { } fi } && !UnsharedContainer(TypeOf(fi))) return Own.Unstable;
            if (IsStableBase(ar)) _lastFieldLoad = (E.Fn.Body.Count, Driver.ElementsKey);
            else if (StaticArrayKey(ar) is { } sak) _lastFieldLoad = (E.Fn.Body.Count, sak + "\n" + Driver.ElementsKey);
            else if (ar is IFieldReferenceOperation { Field.IsStatic: false, Instance: { } ai } afr && IsStableBase(ai) && afr.Field.ContainingType.TypeKind == TypeKind.Class
                && !(ai is IParameterReferenceOperation && TypeOf(ai).IsValueType))
            {
                var ak = Driver.FieldKey(Owner(afr.Field), StorageField(afr.Field));
                D.NoteBorrowField(ak, StorageField(afr.Field), T.Resolve(afr.Field.Type, Map));
                _lastFieldLoad = (E.Fn.Body.Count, ak + "\n" + Driver.ElementsKey);
            }
        }
        return Own.Unstable;
    }

    int EmitArrayElementAddress(IArrayElementReferenceOperation ae, int keepArray = -1)
    {
        var at = (IArrayTypeSymbol)TypeOf(ae.ArrayReference);
        var et = T.Canonical(at.ElementType);
        if (at.Rank != 1)
        {
            // row-major: ((i0 * d1 + i1) * d2 + i2) ..., each index checked against its dimension
            var o0 = EmitValue(ae.ArrayReference);
            bool calls = ae.Indices.Any(ix => ix.DescendantsAndSelf().Any(x => x is IInvocationOperation or IObjectCreationOperation));
            o0 = calls ? ToCallSafe(o0, at) : ToTransient(o0, at);
            int a = E.AddLocal(ValType.I32);
            E.Tee(a);
            if (!IsKnownNonNull(ae.ArrayReference)) NullCheck(); else E.Op(Op.Drop);
            var idxs = new int[at.Rank];
            for (int i = 0; i < at.Rank; i++) { idxs[i] = E.AddLocal(ValType.I32); EmitIndex(ae.Indices[i]); E.Set(idxs[i]); }
            int lin = E.AddLocal(ValType.I32);
            E.I32(0); E.Set(lin);
            for (int i = 0; i < at.Rank; i++)
            {
                E.Get(idxs[i]); E.Get(a); E.Load(Op.I32Load, Types.ArrayData + 4 * i); E.Op(Op.I32GeU);
                E.If(); E.Call(Rt.ThrowIndex); E.End();
                E.Get(lin); E.Get(a); E.Load(Op.I32Load, Types.ArrayData + 4 * i); E.Op(Op.I32Mul); E.Get(idxs[i]); E.Op(Op.I32Add); E.Set(lin);
            }
            E.Get(a); E.Get(lin);
            int sz = T.StorageOf(et).size;
            if (sz != 1) { E.I32(sz); E.Op(Op.I32Mul); }
            E.Op(Op.I32Add);
            return Types.ArrayDataOffset(at.Rank);
        }
        var o = EmitValue(ae.ArrayReference);
        bool indexCalls = ae.Indices[0].DescendantsAndSelf().Any(x => x is IInvocationOperation or IObjectCreationOperation);
        o = indexCalls ? ToCallSafe(o, at) : ToTransient(o, at);
        int arr = Scratch(ValType.I32);
        E.Tee(arr);
        if (keepArray >= 0) { E.Tee(keepArray); }
        if (!IsKnownNonNull(ae.ArrayReference)) NullCheck(); else E.Op(Op.Drop);
        int idx = Scratch(ValType.I32);
        if (TypeOf(ae.Indices[0]) is INamedTypeSymbol { Name: "Index" } && Types.PrimOf(TypeOf(ae.Indices[0])) == Prim.None)
        {
            // a[^i]: the offset from the end, against the array's length
            int alen = E.AddLocal(ValType.I32);
            E.Get(arr); E.Load(Op.I32Load, 12); E.Set(alen);
            EmitIndexOffset(ae.Indices[0], alen, fromStartDefault: true);
        }
        else EmitIndex(ae.Indices[0]);
        E.Set(idx);
        E.Get(idx); E.Get(arr); E.Emit(Op.I32Load, 12, 2, Loops.Length); E.Op(Op.I32GeU);
        E.If(); E.Call(Rt.ThrowIndex); E.End();
        E.Get(arr); E.Get(idx);
        int size = T.StorageOf(et).size;
        if (size != 1) { E.I32(size); E.Op(Op.I32Mul); }
        E.Op(Op.I32Add);
        FreeScratch(ValType.I32, idx); FreeScratch(ValType.I32, arr);
        return Types.ArrayData;
    }

    /// <summary>Pushes an i32 index; 64-bit and unsigned indices out of range become an out-of-range index.</summary>
    void EmitIndex(IOperation index)
    {
        var it = TypeOf(index);
        var p = Types.PrimOf(it);
        EmitValue(index);
        if (Types.Is64(p))
        {
            int t = Scratch(ValType.I64);
            E.Tee(t); E.I64(0x7FFFFFFF); E.Op(Op.I64GtU);
            E.If(ValType.I32); E.I32(-1); E.Else(); E.Get(t); E.Op(Op.I32WrapI64); E.End();
            FreeScratch(ValType.I64, t);
        }
        else if (p is Prim.IntPtr or Prim.UIntPtr or Prim.I32 or Prim.U32 or Prim.I16 or Prim.U16 or Prim.I8 or Prim.U8 or Prim.Char) { }
        else throw new CompileException("unsupported index type " + it);
    }

    // =====================================================================
    // conversions
    // =====================================================================
    Own EmitConversion(IConversionOperation c)
    {
        // An interpolated string taken as a FormattableString or IFormattable (FormattableString.Invariant($"..")):
        // the composite format and the holes' values, as the C# compiler builds it.
        if (c.Operand is IInterpolatedStringOperation fiso && c.Type is { } ft && ft.Name is "FormattableString" or "IFormattable" && ft.ContainingNamespace?.ToDisplayString() == "System")
            return EmitFormattableString(fiso);
        var to = TypeOf(c);
        if (c.Operand is IAnonymousFunctionOperation af) return EmitLambdaDelegate(af, to);
        if (c.Operand is IDelegateCreationOperation or IMethodReferenceOperation && to.TypeKind == TypeKind.Delegate && c.Operand is IMethodReferenceOperation mr)
            return EmitMethodGroupDelegate(mr, to);
        var conv = c.Conversion;
        if (conv.IsUserDefined && conv.MethodSymbol != null)
        {
            var m = conv.MethodSymbol;
            var argT = T.Resolve(m.Parameters[0].Type, Map);
            var r = CallWith(SubstMethod(m), false, null, () =>
            {
                var o = EmitValueAs(c.Operand, argT);
                if (Types.IsRef(argT)) ToCallSafe(o, argT);
            }, null, c.Syntax);
            var rt = T.Resolve(m.ReturnType, Map);
            ConvertTop(rt, to, ref r);
            return r;
        }
        if (c.Operand.Type == null)
        {
            // null literal or default literal
            // (`c ? value : throw e` has no type either: the throw is what runs)
            if (Types.IsStruct(to) && c.Operand is not IThrowOperation) return EmitDefault(to);
            var o = EmitValue(c.Operand);
            return o;
        }
        var from = TypeOf(c.Operand);
        // (T)(object)x where x is a T: generic code picks a type's branch with `typeof(T) == typeof(int)` and converts
        // through object, which costs nothing under .NET. No box to make and take apart: the value itself.
        if (!c.IsTryCast && to.IsValueType && !from.IsValueType && to.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
        {
            var inner = c.Operand;
            while (inner is IParenthesizedOperation po) inner = po.Operand;
            if (inner is IConversionOperation { OperatorMethod: null, IsTryCast: false, Operand.Type: not null } bc
                && TypeOf(bc.Operand) is { IsValueType: true } vt && Types.Key(vt) == Types.Key(to))
                return EmitValue(bc.Operand);
        }
        if (c.IsTryCast)
        {
            var o = EmitValue(c.Operand);
            o = ToTransient(o, from);
            if (IsNullable(to))
            {
                int ov = Scratch(ValType.I32); E.Set(ov);
                int res = TempStruct(to);
                E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.I32(0); E.I32(T.StorageOf(to).size); E.Op(Op.MemoryFill);
                E.Get(ov); E.Call(Rt.IsExactBox(Underlying(to)));
                E.If();
                E.Get(ov); EmitUnbox(Underlying(to)); WrapNullable(to, Own.None);
                int w = Scratch(ValType.I32); E.Set(w);
                E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.Get(w); CopyStructInit(to);
                FreeScratch(ValType.I32, w);
                E.End();
                FreeScratch(ValType.I32, ov);
                E.Get(_fb); E.I32(res); E.Op(Op.I32Add);
                return Own.None;
            }
            int v = Scratch(ValType.I32);
            E.Set(v);
            E.Get(v); E.Call(Rt.IsInst(to));
            E.If(ValType.I32); E.Get(v); E.Else(); E.I32(0); E.End();
            FreeScratch(ValType.I32, v);
            return o == Own.None ? Own.None : Own.Unstable;
        }
        var own = EmitValue(c.Operand);
        ConvertValue(from, to, conv.IsNumeric || from.TypeKind == TypeKind.Enum || to.TypeKind == TypeKind.Enum ? c.IsChecked : false, ref own, explicitConv: !conv.IsImplicit, conv);
        return own;
    }

    void ConvertTop(ITypeSymbol from, ITypeSymbol to, ref Own o) => ConvertValue(from, to, false, ref o, false, null);

    void ConvertValue(ITypeSymbol from, ITypeSymbol to, bool isChecked, ref Own o, bool explicitConv, CommonConversion? conv)
    {
        from = T.Canonical(from); to = T.Canonical(to);
        if (Types.Key(from) == Types.Key(to)) return;
        var pf = Types.PrimOf(from); var pt = Types.PrimOf(to);
        if (ConvertNullable(from, to, isChecked, ref o)) return;
        if (IsSpanType(to) && !IsSpanType(from) || IsSpanType(to) && IsSpanType(from) && Types.Key(from.OriginalDefinition) != Types.Key(to.OriginalDefinition))
        {
            if (Types.InlineArrayLength(from) is int ilen && to is INamedTypeSymbol { Arity: 1 } spanT)
            {
                // an inline array as a span over its elements: MemoryMarshal.Create(ReadOnly)Span(ref element 0, n)
                var mm = D.LibType("System.Runtime.InteropServices.MemoryMarshal");
                var create = mm.GetMembers(spanT.Name == "Span" ? "CreateSpan" : "CreateReadOnlySpan").OfType<IMethodSymbol>().First().Construct(T.Canonical(spanT.TypeArguments[0]));
                int ia = E.AddLocal(ValType.I32); E.Set(ia);
                o = CallWith(create, false, null, () => { E.Get(ia); E.I32(ilen); }, null, null);
                return;
            }
            // first-class span conversions (C# 14): array/string/Span -> (ReadOnly)Span, through the library's operators
            var op = FindImplicit(to, from, to) ?? FindImplicit(from, from, to);
            // array covariance: a Task<int>[] read as ReadOnlySpan<Task> (same references)
            if (op == null && from is IArrayTypeSymbol { Rank: 1 } fa && !fa.ElementType.IsValueType && to is INamedTypeSymbol { Arity: 1 } tn && !tn.TypeArguments[0].IsValueType)
                op = FindImplicit(to, D.LibCompilation.CreateArrayTypeSymbol(T.Canonical(tn.TypeArguments[0])), to);
            if (op == null) throw new CompileException($"no span conversion from {from} to {to}");
            int v = Scratch(Types.WasmType(from) ?? ValType.I32); E.Set(v);
            o = CallWith(op, false, null, () => E.Get(v), null, null);
            FreeScratch(Types.WasmType(from) ?? ValType.I32, v);
            return;
        }
        if (IsDecimal(from) || IsDecimal(to))
        {
            var dop = FindConversion(IsDecimal(to) ? to : from, from, to);
            if (dop != null)
            {
                int dv = Scratch(Types.WasmType(from) ?? ValType.I32); E.Set(dv);
                o = CallWith(dop, false, null, () => E.Get(dv), null, null);
                FreeScratch(Types.WasmType(from) ?? ValType.I32, dv);
                return;
            }
        }
        if (pf != Prim.None && pt != Prim.None)
        {
            if (from.TypeKind == TypeKind.Pointer || to.TypeKind == TypeKind.Pointer) { NumericConvert(pf == Prim.U32 && from.TypeKind == TypeKind.Pointer ? Prim.U32 : pf, pt, isChecked); return; }
            NumericConvert(pf, pt, isChecked);
            return;
        }
        bool fromVal = from.IsValueType, toVal = to.IsValueType;
        if (fromVal && !toVal)
        {
            // boxing
            EmitBox(from, o);
            o = Own.Owned;
            return;
        }
        if (!fromVal && toVal)
        {
            // unboxing
            o = ToTransient(o, from);
            EmitUnbox(to);
            o = Own.None;
            return;
        }
        if (!fromVal && !toVal)
        {
            if (T.IsSubtype(from, to)) return; // implicit reference conversion
            if (!explicitConv && conv != null) return; // C# says it is an implicit conversion: statically safe
            if (from.TypeKind == TypeKind.Interface && to.TypeKind == TypeKind.Interface && !explicitConv) return;
            // explicit reference conversion: checked cast
            int v = Scratch(ValType.I32);
            E.Tee(v); E.Get(v);
            E.If(); E.Get(v); E.Call(Rt.IsInst(to)); E.Op(Op.I32Eqz); E.If(); E.Get(v); E.Call(Rt.ThrowInvalidCast(to)); E.End(); E.End();
            FreeScratch(ValType.I32, v);
            return;
        }
        if (IsValueTuple(from) && IsValueTuple(to)) { ConvertTuple(from, to); return; }
        if (Types.Key(from.OriginalDefinition) == Types.Key(to.OriginalDefinition)) return;
        // a user-defined conversion the operation tree left implicit (UInt128's from ulong, at a return)
        if ((FindConversion(to, from, to) ?? FindConversion(from, from, to)) is { } udc)
        {
            var wt = Types.WasmType(from) ?? ValType.I32;
            int uv = E.AddLocal(wt); E.Set(uv);
            o = CallWith(udc, false, null, () => E.Get(uv), null, null);
            return;
        }
        throw new CompileException($"unsupported conversion from {from} to {to}");
    }

    static bool IsSpanType(ITypeSymbol t) => t is INamedTypeSymbol { Arity: 1 } n && Types.FullName(n) is "System.Span`1" or "System.ReadOnlySpan`1";

    static bool IsDecimal(ITypeSymbol? t) => t?.SpecialType == SpecialType.System_Decimal || t is INamedTypeSymbol { Name: "Decimal", ContainingNamespace.Name: "System" } n && n.ContainingNamespace.ContainingNamespace?.IsGlobalNamespace == true;

    IMethodSymbol DecimalOp(string name, int arity) =>
        D.CanonicalMethod(D.LibType("System.Decimal").GetMembers(name).OfType<IMethodSymbol>().First(m => m.Parameters.Length == arity));

    IMethodSymbol? DecimalOpFor(BinaryOperatorKind k) => k switch
    {
        BinaryOperatorKind.Add => DecimalOp("op_Addition", 2),
        BinaryOperatorKind.Subtract => DecimalOp("op_Subtraction", 2),
        BinaryOperatorKind.Multiply => DecimalOp("op_Multiply", 2),
        BinaryOperatorKind.Divide => DecimalOp("op_Division", 2),
        BinaryOperatorKind.Remainder => DecimalOp("op_Modulus", 2),
        BinaryOperatorKind.Equals => DecimalOp("op_Equality", 2),
        BinaryOperatorKind.NotEquals => DecimalOp("op_Inequality", 2),
        BinaryOperatorKind.LessThan => DecimalOp("op_LessThan", 2),
        BinaryOperatorKind.LessThanOrEqual => DecimalOp("op_LessThanOrEqual", 2),
        BinaryOperatorKind.GreaterThan => DecimalOp("op_GreaterThan", 2),
        BinaryOperatorKind.GreaterThanOrEqual => DecimalOp("op_GreaterThanOrEqual", 2),
        _ => null,
    };

    /// <summary>decimal's built-in operators are the library Decimal's operator methods.</summary>
    IMethodSymbol? DecimalBinary(BinaryOperatorKind k, IOperation l, IOperation r) =>
        l.Type != null && r.Type != null && IsDecimal(TypeOf(l)) && IsDecimal(TypeOf(r)) ? DecimalOpFor(k) : null;

    IMethodSymbol? FindConversion(ITypeSymbol owner, ITypeSymbol from, ITypeSymbol to)
    {
        if (T.Canonical(owner) is not INamedTypeSymbol n) return null;
        foreach (var name in new[] { "op_Implicit", "op_Explicit" })
            foreach (var m in n.GetMembers(name).OfType<IMethodSymbol>())
                if (Types.Key(T.Canonical(m.Parameters[0].Type)) == Types.Key(T.Canonical(from)) && Types.Key(T.Canonical(m.ReturnType)) == Types.Key(T.Canonical(to)))
                    return D.CanonicalMethod(m);
        return null;
    }

    /// <summary>A decimal constant: the struct's fields (as .NET's layout) in a temporary.</summary>
    Own EmitDecimalConstant(decimal d)
    {
        var t = T.Canonical(D.LibType("System.Decimal"));
        var bits = decimal.GetBits(d);
        int off = TempStruct(t);
        var l = T.LayoutOf(t);
        void Field(string name, int v) { var f = l.Fields.First(x => x.Symbol.Name == name); E.Get(_fb); E.I32(v); E.Store(Op.I32Store, off + f.Offset); }
        if (l.Fields.Any(x => x.Symbol.Name == "_lo64"))
        {
            // upstream's decimal: _flags, _hi32, _lo64
            Field("_flags", bits[3]); Field("_hi32", bits[2]);
            var lo = l.Fields.First(x => x.Symbol.Name == "_lo64");
            E.Get(_fb); E.I64((long)(((ulong)(uint)bits[1] << 32) | (uint)bits[0])); E.Store(Op.I64Store, off + lo.Offset);
        }
        else { Field("_lo", bits[0]); Field("_mid", bits[1]); Field("_hi", bits[2]); Field("_flags", bits[3]); }
        E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
        return Own.None;
    }

    IMethodSymbol? FindImplicit(ITypeSymbol owner, ITypeSymbol from, ITypeSymbol to)
    {
        if (T.Canonical(owner) is not INamedTypeSymbol n) return null;
        foreach (var m in n.GetMembers("op_Implicit").OfType<IMethodSymbol>())
            if (Types.Key(T.Canonical(m.Parameters[0].Type)) == Types.Key(T.Canonical(from)) && Types.Key(T.Canonical(m.ReturnType)) == Types.Key(T.Canonical(to)))
                return D.CanonicalMethod(m);
        return null;
    }

    static bool IsNullable(ITypeSymbol t) => t is INamedTypeSymbol { Arity: 1 } n && Types.FullName(n) == "System.Nullable`1";

    void EmitBox(ITypeSymbol vt, Own o)
    {
        int tid = D.MarkConstructed(vt);
        var (size, align) = T.StorageOf(vt);
        int dataOff = BoxDataOffset(T, vt);
        if (Types.IsStruct(vt))
        {
            int src = Scratch(ValType.I32); E.Set(src);
            E.I32(dataOff + size); E.Tid(tid); E.Call(Rt.AllocObject);
            int obj = Scratch(ValType.I32); E.Tee(obj);
            E.I32(dataOff); E.Op(Op.I32Add); E.Get(src); CopyStructInit(vt);
            E.Get(obj);
            FreeScratch(ValType.I32, obj); FreeScratch(ValType.I32, src);
            return;
        }
        var wt = Types.WasmType(vt)!.Value;
        int val = Scratch(wt); E.Set(val);
        E.I32(dataOff + size); E.Tid(tid); E.Call(Rt.AllocObject);
        int o2 = Scratch(ValType.I32); E.Tee(o2);
        E.Get(val); E.Store(Types.StoreOp(vt), dataOff);
        E.Get(o2);
        FreeScratch(ValType.I32, o2); FreeScratch(wt, val);
    }

    public static int BoxDataOffset(Types t, ITypeSymbol vt) => FunctionCompilerHelpers.BoxDataOffset(t, vt);

    /// <summary>Stack: object → value (or struct address inside the box).</summary>
    void EmitUnbox(ITypeSymbol vt)
    {
        int obj = Scratch(ValType.I32);
        E.Tee(obj);
        NullCheck();
        E.Get(obj); E.Call(Rt.UnboxCheck(vt));
        E.Get(obj);
        int off = BoxDataOffset(T, vt);
        if (Types.IsStruct(vt)) { E.I32(off); E.Op(Op.I32Add); }
        else E.Load(Types.LoadOp(vt), off);
        FreeScratch(ValType.I32, obj);
    }

    void NumericConvert(Prim from, Prim to, bool isChecked)
    {
        if (from == to) return;
        var wf = Types.WasmPrim(from); var wt = Types.WasmPrim(to);
        if (Types.IsFloat(from))
        {
            if (Types.IsFloat(to)) { E.Op(to == Prim.F64 ? Op.F64PromoteF32 : Op.F32DemoteF64); return; }
            if (isChecked) { E.Call(Rt.CheckedFloatToInt(from, to)); return; }
            // .NET saturates (since .NET 9) to the 32/64-bit range, then narrows
            bool to64 = Types.Is64(to);
            bool unsigned = to is Prim.U32 or Prim.U64 or Prim.UIntPtr;
            Op op = (from, to64, unsigned) switch
            {
                (Prim.F32, false, false) => Op.I32TruncSatF32S, (Prim.F32, false, true) => Op.I32TruncSatF32U,
                (Prim.F32, true, false) => Op.I64TruncSatF32S, (Prim.F32, true, true) => Op.I64TruncSatF32U,
                (_, false, false) => Op.I32TruncSatF64S, (_, false, true) => Op.I32TruncSatF64U,
                (_, true, false) => Op.I64TruncSatF64S, _ => Op.I64TruncSatF64U,
            };
            E.Op(op);
            if (Types.PrimSize(to) < 4)
            {
                var (mn, mx) = Range(to);
                E.I32((int)mn); E.I32((int)mx); E.Call(Rt.ClampI32);
            }
            return;
        }
        if (Types.IsFloat(to))
        {
            bool srcUnsigned = !Types.IsSigned(from) && from != Prim.Bool;
            Op op = (to, wf, srcUnsigned) switch
            {
                (Prim.F32, ValType.I64, false) => Op.F32ConvertI64S, (Prim.F32, ValType.I64, true) => Op.F32ConvertI64U,
                (Prim.F32, _, false) => Op.F32ConvertI32S, (Prim.F32, _, true) => Op.F32ConvertI32U,
                (_, ValType.I64, false) => Op.F64ConvertI64S, (_, ValType.I64, true) => Op.F64ConvertI64U,
                (_, _, false) => Op.F64ConvertI32S, _ => Op.F64ConvertI32U,
            };
            E.Op(op);
            return;
        }
        // integer to integer
        if (isChecked) CheckIntRange(from, to);
        if (wf == ValType.I32 && wt == ValType.I64) { E.Op(Types.IsSigned(from) ? Op.I64ExtendI32S : Op.I64ExtendI32U); return; }
        if (wf == ValType.I64 && wt == ValType.I32) { E.Op(Op.I32WrapI64); Narrow(to); return; }
        if (wf == ValType.I32 && wt == ValType.I32) { if (Types.PrimSize(to) < 4 && (Types.PrimSize(to) < Types.PrimSize(from) || Types.IsSigned(from) != Types.IsSigned(to))) Narrow(to); }
    }

    void Narrow(Prim to)
    {
        switch (to)
        {
            case Prim.U8: E.I32(0xFF); E.Op(Op.I32And); break;
            case Prim.I8: E.Op(Op.I32Extend8S); break;
            case Prim.U16: case Prim.Char: E.I32(0xFFFF); E.Op(Op.I32And); break;
            case Prim.I16: E.Op(Op.I32Extend16S); break;
            case Prim.Bool: E.I32(0); E.Op(Op.I32Ne); break;
        }
    }

    static (long min, ulong max) Range(Prim p) => p switch
    {
        Prim.I8 => (sbyte.MinValue, (ulong)sbyte.MaxValue), Prim.U8 => (0, byte.MaxValue),
        Prim.I16 => (short.MinValue, (ulong)short.MaxValue), Prim.U16 or Prim.Char => (0, ushort.MaxValue),
        Prim.I32 or Prim.IntPtr => (int.MinValue, int.MaxValue), Prim.U32 or Prim.UIntPtr => (0, uint.MaxValue),
        Prim.I64 => (long.MinValue, long.MaxValue), _ => (0, ulong.MaxValue),
    };

    /// <summary>Checked integer conversion: throw OverflowException when the value does not fit.</summary>
    void CheckIntRange(Prim from, Prim to)
    {
        var (fmin, fmax) = Range(from); var (tmin, tmax) = Range(to);
        bool needLow = fmin < tmin, needHigh = fmax > tmax;
        if (!needLow && !needHigh) return;
        bool f64 = Types.Is64(from);
        int t = Scratch(f64 ? ValType.I64 : ValType.I32);
        E.Tee(t);
        // compare in the source's signedness
        bool signed = Types.IsSigned(from);
        if (f64)
        {
            if (needLow && needHigh) { E.I64(tmin); E.Op(Op.I64LtS); E.Get(t); E.I64(unchecked((long)tmax)); E.Op(signed ? Op.I64GtS : Op.I64GtU); E.Op(Op.I32Or); }
            else if (needLow) { E.I64(tmin); E.Op(Op.I64LtS); }
            else { E.I64(unchecked((long)tmax)); E.Op(signed ? Op.I64GtS : Op.I64GtU); }
        }
        else
        {
            if (needLow && needHigh) { E.I32((int)tmin); E.Op(Op.I32LtS); E.Get(t); E.I32(unchecked((int)tmax)); E.Op(signed ? Op.I32GtS : Op.I32GtU); E.Op(Op.I32Or); }
            else if (needLow) { E.I32((int)tmin); E.Op(Op.I32LtS); }
            else { E.I32(unchecked((int)(uint)tmax)); E.Op(signed ? Op.I32GtS : Op.I32GtU); }
        }
        E.If(); E.Call(Rt.ThrowOverflow); E.End();
        E.Get(t);
        FreeScratch(f64 ? ValType.I64 : ValType.I32, t);
    }

    // =====================================================================
    // binary / unary
    // =====================================================================
    Own EmitBinary(IBinaryOperation b)
    {
        if (b.IsLifted) return EmitLiftedBinary(b);
        if (b.OperatorMethod is { IsStatic: true, ContainingType.TypeKind: TypeKind.Interface } om && b.ConstrainedToType is { } bct)
        {
            var impl = D.Implementation(T.Resolve(bct, Map), SubstMethod(om)) ?? SubstMethod(om);
            return EmitCallOps(impl, null, [b.LeftOperand, b.RightOperand], false, b.Syntax, alreadyResolved: true);
        }
        if (b.OperatorMethod == null && DecimalBinary(b.OperatorKind, b.LeftOperand, b.RightOperand) is { } dop)
            return EmitCall(dop, null, [b.LeftOperand, b.RightOperand], false, b.Syntax);
        if (b.OperatorMethod != null && !IsBuiltinOperator(b.OperatorMethod))
        {
            var m = SubstMethod(b.OperatorMethod);
            return EmitCall(b.OperatorMethod, null, [b.LeftOperand, b.RightOperand], false, b.Syntax);
        }
        var ltN = b.LeftOperand.Type != null ? TypeOf(b.LeftOperand) : null;
        var rtN = b.RightOperand.Type != null ? TypeOf(b.RightOperand) : null;
        // `x == null` where x is a value that cannot be null, seen as an object (generic code over T = int:
        // `if (array[i] == null)`): never equal. The value is evaluated, not boxed.
        if (b.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals && b.OperatorMethod == null)
        {
            var nullSide = IsNullConstant(b.RightOperand) ? b.LeftOperand : IsNullConstant(b.LeftOperand) ? b.RightOperand : null;
            if (nullSide != null && BoxedValue(nullSide) is { } boxed)
            {
                var bt = TypeOf(boxed);
                Drop(EmitValue(boxed), bt);
                E.I32(b.OperatorKind == BinaryOperatorKind.NotEquals ? 1 : 0);
                return Own.None;
            }
        }
        if ((ltN != null && IsNullable(ltN)) || (rtN != null && IsNullable(rtN))) return EmitLiftedBinary(b);
        if (ltN == null || rtN == null)
        {
            // comparison with an untyped null
            var other = ltN == null ? b.RightOperand : b.LeftOperand;
            var ot = ltN ?? rtN;
            if (ot != null && ot.IsValueType)
            {
                // a non-nullable value is never null
                Drop(EmitValue(other), ot);
                E.I32(b.OperatorKind == BinaryOperatorKind.NotEquals ? 1 : 0);
                return Own.None;
            }
            var oo = EmitValue(other); ToTransient(oo, ot ?? D.ObjectType);
            E.Op(Op.I32Eqz);
            if (b.OperatorKind == BinaryOperatorKind.NotEquals) E.Op(Op.I32Eqz);
            return Own.None;
        }
        var lt = ltN;
        var rt = rtN;
        var kind = b.OperatorKind;
        // Two delegates are equal when they are of one type and call the same methods on the same targets (C#
        // 12.12.9): not only when they are one object.
        if (kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals && b.OperatorMethod == null && IsDelegateLike(lt) && IsDelegateLike(rt))
        {
            var dl = EmitValue(b.LeftOperand); ToCallSafe(dl, lt);
            var dr = EmitValue(b.RightOperand); ToCallSafe(dr, rt);
            E.Call(D.LibMethod("System.Delegate", "op_Equality", 2).Fn);
            if (kind == BinaryOperatorKind.NotEquals) E.Op(Op.I32Eqz);
            return Own.None;
        }
        if (kind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
        {
            EmitCondition(b.LeftOperand);
            E.If(ValType.I32);
            // (the right operand's temporaries are released where it ran, not at the statement's end whether it ran or not)
            if (kind == BinaryOperatorKind.ConditionalAnd) { BeginBranchTemps(); EmitCondition(b.RightOperand); EndBranchTempsValue(ValType.I32); E.Else(); E.I32(0); }
            else { E.I32(1); E.Else(); BeginBranchTemps(); EmitCondition(b.RightOperand); EndBranchTempsValue(ValType.I32); }
            E.End();
            return Own.None;
        }
        var resT = TypeOf(b);
        if (kind == BinaryOperatorKind.Add && (Types.IsString(lt) || Types.IsString(rt)) && Types.IsString(resT))
            return EmitStringConcat(b.LeftOperand, b.RightOperand);
        if (kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals && Types.IsString(lt) && Types.IsString(rt)
            && b.LeftOperand.ConstantValue is not { HasValue: true, Value: null } && b.RightOperand.ConstantValue is not { HasValue: true, Value: null })
        {
            // string == string is value equality (the predefined operator), not reference equality
            var o1 = EmitValue(b.LeftOperand); ToCallSafe(o1, lt);
            var o2 = EmitValue(b.RightOperand); ToCallSafe(o2, rt);
            E.Call(D.StringEquals.Fn);
            if (kind == BinaryOperatorKind.NotEquals) E.Op(Op.I32Eqz);
            return Own.None;
        }
        if (kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals && (Types.IsRef(lt) || Types.IsRef(rt) || lt.TypeKind == TypeKind.Pointer))
        {
            var o1 = EmitValue(b.LeftOperand); o1 = ToTransient(o1, lt);
            var o2 = EmitValue(b.RightOperand); ToTransient(o2, rt);
            E.Op(kind == BinaryOperatorKind.Equals ? Op.I32Eq : Op.I32Ne);
            return Own.None;
        }
        // delegate + delegate, delegate - delegate: Delegate.Combine / Delegate.Remove
        if (kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract && TypeOf(b).TypeKind == TypeKind.Delegate)
        {
            var o1 = EmitValue(b.LeftOperand); ToCallSafe(o1, lt);
            var o2 = EmitValue(b.RightOperand); ToCallSafe(o2, rt);
            E.Call(D.LibMethod("System.Delegate", kind == BinaryOperatorKind.Add ? "Combine" : "Remove", 2).Fn);
            return Own.Owned;
        }
        // pointer arithmetic
        if (lt.TypeKind == TypeKind.Pointer && kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract && rt.TypeKind != TypeKind.Pointer)
        {
            EmitValue(b.LeftOperand);
            EmitValue(b.RightOperand); var prt = Types.PrimOf(rt); if (Types.Is64(prt)) E.Op(Op.I32WrapI64);
            int es = T.StorageOf(T.Resolve(((IPointerTypeSymbol)lt).PointedAtType, Map)).size;
            if (es != 1) { E.I32(es); E.Op(Op.I32Mul); }
            E.Op(kind == BinaryOperatorKind.Add ? Op.I32Add : Op.I32Sub);
            return Own.None;
        }
        if (lt.TypeKind == TypeKind.Pointer && rt.TypeKind == TypeKind.Pointer && kind == BinaryOperatorKind.Subtract)
        {
            EmitValue(b.LeftOperand); EmitValue(b.RightOperand); E.Op(Op.I32Sub);
            int es = T.StorageOf(T.Resolve(((IPointerTypeSymbol)lt).PointedAtType, Map)).size;
            if (es != 1) { E.I32(es); E.Op(Op.I32DivS); }
            E.Op(Op.I64ExtendI32S);
            return Own.None;
        }
        // operand type: shifts keep the left type; everything else operates in the left type (Roslyn converted operands)
        var opT = lt;
        var p = Types.PrimOf(opT);
        if (p == Prim.None) throw new CompileException($"unsupported operator {kind} on {opT}", b.Syntax.GetLocation());
        EmitValue(b.LeftOperand);
        EmitValue(b.RightOperand);
        if (kind is BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift or BinaryOperatorKind.UnsignedRightShift)
        {
            if (Types.Is64(p) && !Types.Is64(Types.PrimOf(rt))) E.Op(Op.I64ExtendI32U);
            else if (!Types.Is64(p) && Types.Is64(Types.PrimOf(rt))) E.Op(Op.I32WrapI64);
        }
        else if (Types.PrimOf(rt) != p)
        {
            NumericConvert(Types.PrimOf(rt), p, false);
        }
        EmitArith(kind, p, b.IsChecked, Types.PrimOf(resT));
        return Own.None;
    }

    static bool IsBuiltinOperator(IMethodSymbol m) => false;

    static bool IsNullConstant(IOperation op)
    {
        while (op is IConversionOperation { Conversion.IsImplicit: true } c) op = c.Operand;
        return op.ConstantValue is { HasValue: true, Value: null } && op.Type == null || op is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } };
    }

    /// <summary>The value under a conversion to object or an interface, when it is of a value type that has no null.</summary>
    IOperation? BoxedValue(IOperation op)
    {
        while (op is IConversionOperation { OperatorMethod: null } c && c.Operand.Type != null)
        {
            var ot = TypeOf(c.Operand);
            if (ot.IsValueType) return !IsNullable(ot) && ot.TypeKind != TypeKind.TypeParameter && !TypeOf(c).IsValueType ? c.Operand : null;
            op = c.Operand;
        }
        return null;
    }

    readonly Dictionary<ILocalSymbol, bool> _ownedLocals = new(SymbolEqualityComparer.Default);

    /// <summary>The expression is a local variable that owns the reference it holds: a plain local of this function
    /// (not captured, not a state machine's, not a ref, its address not taken), declared in a declaration statement
    /// or a `for`, written by plain assignments only, and one that takes a count for every value it is given: not
    /// one that may borrow (a local assigned again whose every value something else keeps, LocalBorrows.cs; one
    /// never assigned again that reads a field or a stable reference, FieldBorrows.cs), nor a `foreach` or pattern
    /// variable. One of the counts of the object it holds is then this function's own, whatever the final pass makes
    /// of the counts between this function's variables (a count handed from one of them to the other is still this
    /// function's). So while that object's count is 1, no object anywhere refers to it (Rt.ArenaLinkOwned): a list
    /// reversed in place, `n.Next = prev`, is plain stores.</summary>
    bool OwnedLocal(IOperation? instance)
    {
        if (_machine != null || _closure != null || instance is not ILocalReferenceOperation lr) return false;
        var local = lr.Local;
        if (_ownedLocals.TryGetValue(local, out bool ok)) return ok;
        ok = false;
        if (local.RefKind == RefKind.None && !IsCaptured(local) && !_addrTaken.Contains(local) && Types.IsRef(T.Resolve(local.Type, Map))
            && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: var holder } } vd
            && (holder is ForStatementSyntax || holder is LocalDeclarationStatementSyntax ld && ld.UsingKeyword.RawKind == 0)
            && D.ModelFor(vd.SyntaxTree).GetOperation(vd) is IVariableDeclaratorOperation vdo
            && (!LocalWrites().TryGetValue(local, out var writes) || writes.All(w => w != null))
            && WrittenBorrow(local) == null)
        {
            var init = vdo.Initializer?.Value ?? (vdo.Parent as IVariableDeclarationOperation)?.Initializer?.Value;
            ok = init == null || FieldBorrowCandidate(local, init) == null;
        }
        return _ownedLocals[local] = ok;
    }

    static bool IsDelegateLike(ITypeSymbol t) => t.TypeKind == TypeKind.Delegate || t is INamedTypeSymbol { Name: "Delegate" or "MulticastDelegate", ContainingNamespace.Name: "System" };

    /// <summary>Stack: a b (in prim p) → result.</summary>
    void EmitArith(BinaryOperatorKind kind, Prim p, bool isChecked, Prim result)
    {
        var w = Types.WasmPrim(p);
        bool signed = Types.IsSigned(p) || p == Prim.Bool;
        bool f = Types.IsFloat(p);
        switch (kind)
        {
            case BinaryOperatorKind.Equals: case BinaryOperatorKind.NotEquals:
            case BinaryOperatorKind.LessThan: case BinaryOperatorKind.LessThanOrEqual:
            case BinaryOperatorKind.GreaterThan: case BinaryOperatorKind.GreaterThanOrEqual:
                EmitCompare(kind, p); return;
        }
        if (isChecked && !f && kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply)
        {
            E.Call(Rt.CheckedArith(kind, p));
            return;
        }
        Op op = (kind, w) switch
        {
            (BinaryOperatorKind.Add, ValType.I32) => Op.I32Add, (BinaryOperatorKind.Add, ValType.I64) => Op.I64Add,
            (BinaryOperatorKind.Add, ValType.F32) => Op.F32Add, (BinaryOperatorKind.Add, ValType.F64) => Op.F64Add,
            (BinaryOperatorKind.Subtract, ValType.I32) => Op.I32Sub, (BinaryOperatorKind.Subtract, ValType.I64) => Op.I64Sub,
            (BinaryOperatorKind.Subtract, ValType.F32) => Op.F32Sub, (BinaryOperatorKind.Subtract, ValType.F64) => Op.F64Sub,
            (BinaryOperatorKind.Multiply, ValType.I32) => Op.I32Mul, (BinaryOperatorKind.Multiply, ValType.I64) => Op.I64Mul,
            (BinaryOperatorKind.Multiply, ValType.F32) => Op.F32Mul, (BinaryOperatorKind.Multiply, ValType.F64) => Op.F64Mul,
            (BinaryOperatorKind.Divide, ValType.F32) => Op.F32Div, (BinaryOperatorKind.Divide, ValType.F64) => Op.F64Div,
            (BinaryOperatorKind.And, ValType.I32) => Op.I32And, (BinaryOperatorKind.And, ValType.I64) => Op.I64And,
            (BinaryOperatorKind.Or, ValType.I32) => Op.I32Or, (BinaryOperatorKind.Or, ValType.I64) => Op.I64Or,
            (BinaryOperatorKind.ExclusiveOr, ValType.I32) => Op.I32Xor, (BinaryOperatorKind.ExclusiveOr, ValType.I64) => Op.I64Xor,
            (BinaryOperatorKind.LeftShift, ValType.I32) => Op.I32Shl, (BinaryOperatorKind.LeftShift, ValType.I64) => Op.I64Shl,
            (BinaryOperatorKind.RightShift, ValType.I32) => signed ? Op.I32ShrS : Op.I32ShrU,
            (BinaryOperatorKind.RightShift, ValType.I64) => signed ? Op.I64ShrS : Op.I64ShrU,
            (BinaryOperatorKind.UnsignedRightShift, ValType.I32) => Op.I32ShrU,
            (BinaryOperatorKind.UnsignedRightShift, ValType.I64) => Op.I64ShrU,
            (BinaryOperatorKind.Divide, _) or (BinaryOperatorKind.Remainder, _) => Op.Nop,
            _ => throw new CompileException($"unsupported operator {kind} on {p}"),
        };
        if (op == Op.Nop)
        {
            if (f) { E.Call(Rt.FloatRem(w)); return; }
            // A constant divisor that is neither 0 nor -1 can fail in no way: the instruction itself, no checks.
            if (E.Fn.Body.Count > 0 && E.Fn.Body[^1] is { Ref: null } last && last.Op == (w == ValType.I64 ? Op.I64Const : Op.I32Const)
                && last.Imm != 0 && (w == ValType.I64 ? last.Imm : (int)last.Imm) != -1)
            {
                bool dv = kind == BinaryOperatorKind.Divide, l64 = w == ValType.I64;
                E.Op((dv, l64, signed) switch
                {
                    (true, false, true) => Op.I32DivS, (true, false, false) => Op.I32DivU,
                    (true, true, true) => Op.I64DivS, (true, true, false) => Op.I64DivU,
                    (false, false, true) => Op.I32RemS, (false, false, false) => Op.I32RemU,
                    (false, true, true) => Op.I64RemS, _ => Op.I64RemU,
                });
                return;
            }
            E.Call(Rt.IntDiv(kind == BinaryOperatorKind.Divide, w, signed));
            return;
        }
        E.Op(op);
        // results of narrow types are int in C#, so no narrowing here (Roslyn converts back where needed)
        if (p is Prim.I8 or Prim.U8 or Prim.I16 or Prim.U16 or Prim.Char && result == p) Narrow(p);
    }

    /// <summary>A constant pattern's test, the value (of type `t`; a decimal's address) on the stack: whether it is the
    /// constant. A NaN constant matches a NaN (a pattern asks as Equals does, not as == does); a decimal is compared
    /// by its operator.</summary>
    void EmitEqualsConstant(IOperation cv, ITypeSymbol t)
    {
        if (cv.ConstantValue is { HasValue: true, Value: double d } && double.IsNaN(d) || cv.ConstantValue is { HasValue: true, Value: float f } && float.IsNaN(f))
        {
            var w = Types.WasmType(t) ?? ValType.F64;
            if (w is ValType.F64 or ValType.F32)
            {
                int x = Scratch(w);
                E.Tee(x); E.Get(x); E.Op(w == ValType.F64 ? Op.F64Ne : Op.F32Ne);
                FreeScratch(w, x);
                return;
            }
        }
        if (IsDecimal(t))
        {
            var dec = D.LibType("System.Decimal");
            int la = Scratch(ValType.I32); E.Set(la);
            CallWith(DecimalOpFor(BinaryOperatorKind.Equals)!, false, null, () =>
            {
                E.Get(la); CopyToTemp(dec);
                var co = EmitValue(cv);
                if (cv.Type != null) ConvertTop(TypeOf(cv), dec, ref co);
                CopyToTemp(dec);
            }, null, cv.Syntax);
            FreeScratch(ValType.I32, la);
            return;
        }
        var o = EmitValue(cv);
        if (cv.Type != null) ConvertTop(TypeOf(cv), t, ref o);
        EmitCompare(BinaryOperatorKind.Equals, t);
    }

    /// <summary>`> c`, `<= c` and the like on a decimal (its address pushed by `pushLeft`): decimal's own operator.</summary>
    void EmitDecimalRelation(IRelationalPatternOperation rp, Action pushLeft)
    {
        var dec = D.LibType("System.Decimal");
        var op = DecimalOpFor(rp.OperatorKind) ?? throw new CompileException("relational pattern on decimal: " + rp.OperatorKind, rp.Syntax.GetLocation());
        CallWith(op, false, null, () =>
        {
            pushLeft(); CopyToTemp(dec);
            var o = EmitValue(rp.Value);
            ConvertTop(TypeOf(rp.Value), dec, ref o);
            CopyToTemp(dec);
        }, null, rp.Syntax);
    }

    void EmitCompare(BinaryOperatorKind kind, ITypeSymbol t) => EmitCompare(kind, Types.PrimOf(t) is var p && p != Prim.None ? p : Prim.U32);

    void EmitCompare(BinaryOperatorKind kind, Prim p)
    {
        var w = Types.WasmPrim(p);
        bool signed = Types.IsSigned(p);
        Op op = (kind, w) switch
        {
            (BinaryOperatorKind.Equals, ValType.I32) => Op.I32Eq, (BinaryOperatorKind.Equals, ValType.I64) => Op.I64Eq,
            (BinaryOperatorKind.Equals, ValType.F32) => Op.F32Eq, (BinaryOperatorKind.Equals, ValType.F64) => Op.F64Eq,
            (BinaryOperatorKind.NotEquals, ValType.I32) => Op.I32Ne, (BinaryOperatorKind.NotEquals, ValType.I64) => Op.I64Ne,
            (BinaryOperatorKind.NotEquals, ValType.F32) => Op.F32Ne, (BinaryOperatorKind.NotEquals, ValType.F64) => Op.F64Ne,
            (BinaryOperatorKind.LessThan, ValType.I32) => signed ? Op.I32LtS : Op.I32LtU,
            (BinaryOperatorKind.LessThan, ValType.I64) => signed ? Op.I64LtS : Op.I64LtU,
            (BinaryOperatorKind.LessThan, ValType.F32) => Op.F32Lt, (BinaryOperatorKind.LessThan, ValType.F64) => Op.F64Lt,
            (BinaryOperatorKind.LessThanOrEqual, ValType.I32) => signed ? Op.I32LeS : Op.I32LeU,
            (BinaryOperatorKind.LessThanOrEqual, ValType.I64) => signed ? Op.I64LeS : Op.I64LeU,
            (BinaryOperatorKind.LessThanOrEqual, ValType.F32) => Op.F32Le, (BinaryOperatorKind.LessThanOrEqual, ValType.F64) => Op.F64Le,
            (BinaryOperatorKind.GreaterThan, ValType.I32) => signed ? Op.I32GtS : Op.I32GtU,
            (BinaryOperatorKind.GreaterThan, ValType.I64) => signed ? Op.I64GtS : Op.I64GtU,
            (BinaryOperatorKind.GreaterThan, ValType.F32) => Op.F32Gt, (BinaryOperatorKind.GreaterThan, ValType.F64) => Op.F64Gt,
            (BinaryOperatorKind.GreaterThanOrEqual, ValType.I32) => signed ? Op.I32GeS : Op.I32GeU,
            (BinaryOperatorKind.GreaterThanOrEqual, ValType.I64) => signed ? Op.I64GeS : Op.I64GeU,
            (BinaryOperatorKind.GreaterThanOrEqual, ValType.F32) => Op.F32Ge, (BinaryOperatorKind.GreaterThanOrEqual, ValType.F64) => Op.F64Ge,
            _ => throw new CompileException("bad comparison " + kind),
        };
        E.Op(op);
    }

    /// <summary>`a..b` as a value: new Range(a, b), an omitted end being Index.Start or Index.End.</summary>
    Own EmitRange(IRangeOperation ro)
    {
        var rangeT = (INamedTypeSymbol)TypeOf(ro);
        var ctor = rangeT.InstanceConstructors.First(c => c.Parameters.Length == 2);
        var idxT = (INamedTypeSymbol)ctor.Parameters[0].Type;
        var idxCtor = idxT.InstanceConstructors.First(c => c.Parameters.Length == 2 && Types.PrimOf(c.Parameters[1].Type) == Prim.Bool);
        int Index(IOperation? op, bool fromEnd)
        {
            int at = TempStruct(idxT);
            if (op == null)
                CallWith(D.CanonicalMethod(idxCtor), false, () => { E.Get(_fb); E.I32(at); E.Op(Op.I32Add); }, () => { E.I32(0); E.I32(fromEnd ? 1 : 0); }, null, ro.Syntax);
            else
            {
                E.Get(_fb); E.I32(at); E.Op(Op.I32Add);
                EmitValueAs(op, idxT);
                E.I32(T.StorageOf(idxT).size); E.Op(Op.MemoryCopy);
            }
            return at;
        }
        int a = Index(ro.LeftOperand, false), b = Index(ro.RightOperand, true);
        int off = TempStruct(rangeT);
        CallWith(D.CanonicalMethod(ctor), false, () => { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); },
            () => { E.Get(_fb); E.I32(a); E.Op(Op.I32Add); E.Get(_fb); E.I32(b); E.Op(Op.I32Add); }, null, ro.Syntax);
        E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
        return Own.None;
    }

    Own EmitUnary(IUnaryOperation u)
    {
        if (u.IsLifted) return EmitLiftedUnary(u);
        if (u.OperatorMethod != null) return EmitCall(u.OperatorMethod, null, [u.Operand], false, u.Syntax);
        if (u.OperatorKind == UnaryOperatorKind.Hat)
        {
            // ^n: new Index(n, fromEnd: true)
            var idxT = (INamedTypeSymbol)TypeOf(u);
            var ctor = idxT.InstanceConstructors.First(c => c.Parameters.Length == 2 && Types.PrimOf(c.Parameters[1].Type) == Prim.Bool);
            int off = TempStruct(idxT);
            CallWith(D.CanonicalMethod(ctor), false, () => { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); }, () => { EmitValueAs(u.Operand, D.LibType("System.Int32")); E.I32(1); }, null, u.Syntax);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
            return Own.None;
        }
        if (IsDecimal(TypeOf(u.Operand)) && u.OperatorKind is UnaryOperatorKind.Minus or UnaryOperatorKind.Plus)
            return EmitCall(DecimalOp(u.OperatorKind == UnaryOperatorKind.Minus ? "op_UnaryNegation" : "op_UnaryPlus", 1), null, [u.Operand], false, u.Syntax);
        var t = TypeOf(u.Operand);
        var p = Types.PrimOf(t);
        var w = Types.WasmPrim(p);
        switch (u.OperatorKind)
        {
            case UnaryOperatorKind.Plus: EmitValue(u.Operand); return Own.None;
            case UnaryOperatorKind.Not: EmitValue(u.Operand); E.Op(Op.I32Eqz); return Own.None;
            case UnaryOperatorKind.BitwiseNegation:
                EmitValue(u.Operand);
                if (w == ValType.I64) { E.I64(-1); E.Op(Op.I64Xor); } else { E.I32(-1); E.Op(Op.I32Xor); if (Types.PrimSize(p) < 4 && TypeOf(u) is var rt && Types.PrimOf(rt) == p) Narrow(p); }
                return Own.None;
            case UnaryOperatorKind.Minus:
                if (w == ValType.F32 || w == ValType.F64) { EmitValue(u.Operand); E.Op(w == ValType.F32 ? Op.F32Neg : Op.F64Neg); return Own.None; }
                if (w == ValType.I64) { E.I64(0); EmitValue(u.Operand); EmitArith(BinaryOperatorKind.Subtract, p, u.IsChecked, p); }
                else { E.I32(0); EmitValue(u.Operand); EmitArith(BinaryOperatorKind.Subtract, p, u.IsChecked, p); }
                return Own.None;
        }
        throw new CompileException("unsupported unary operator " + u.OperatorKind, u.Syntax.GetLocation());
    }

    // =====================================================================
    // strings
    // =====================================================================
    Own EmitStringConcat(IOperation a, IOperation b)
    {
        // flatten a + b + c ... into String.Concat calls of two strings
        var parts = new List<IOperation>();
        void Flatten(IOperation x)
        {
            if (x is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, OperatorMethod: null } bb && bb.Type != null && Types.IsString(TypeOf(bb)))
            { Flatten(bb.LeftOperand); Flatten(bb.RightOperand); }
            else parts.Add(x);
        }
        Flatten(a); Flatten(b);
        return ConcatParts(parts.Select(p => (Func<Own>)(() => EmitToString(p))).ToList());
    }

    /// <summary>A store that goes down a structure (ChainStores.cs): of a field of its class's own type, proven for
    /// this method's text; what the proof asks of the whole program is noted on the site.</summary>
    bool ChainStore(Arenas.Site site, IFieldReferenceOperation fr, ISimpleAssignmentOperation asg)
    {
        if (_closure != null || _machine != null) return false;
        if (!SymbolEqualityComparer.Default.Equals(fr.Field.Type.OriginalDefinition, fr.Field.ContainingType.OriginalDefinition)) return false;
        if (D.ChainSafeStore(M, asg) is not { } proof) return false;
        foreach (var t in proof.RefParamTypes) site.ChainRefTypes.Add(Types.Key(T.Canonical(T.Resolve(t, Map))));
        return true;
    }
    static readonly bool s_noConcatOnce = Environment.GetEnvironmentVariable("CS2WASM_NO_CONCAT_ONCE") == "1";
    Own ConcatParts(List<Func<Own>> parts)
    {
        // up to four strings in one call, one new string (as the C# compiler does); more: four, then the result
        // with the next three, and so on. Every part is evaluated before the call that takes it.
        var first = parts[0]();
        first = ToCallSafe(first, D.StringType);
        Own acc = first;
        // (five parts and more: eight in one call, one new string, as .NET's Concat of a span of strings makes one;
        // then the result with the next seven. In fours each call made a string and copied what the calls before it
        // had: three strings and two copies of the first part for a line of eight parts. CS2WASM_NO_CONCAT_ONCE=1)
        bool once = parts.Count > 4 && !s_noConcatOnce;
        for (int i = 1; i < parts.Count;)
        {
            int n = Math.Min(once ? 7 : 3, parts.Count - i);
            for (int k = 0; k < n; k++) { var o = parts[i + k](); ToCallSafe(o, D.StringType); }
            if (once)
            {
                for (int k = n; k < 7; k++) E.I32(0);
                E.Call(D.LibMethod("System.String", "Concat8", 8).Fn);
            }
            else
            E.Call(D.LibMethod("System.String", n == 1 ? "Concat2" : n == 2 ? "Concat3" : "Concat4", n + 1).Fn);
            i += n;
            acc = Own.Owned;
            if (i < parts.Count) acc = ToCallSafe(acc, D.StringType);
        }
        if (parts.Count == 1)
        {
            // a single part still becomes a non-null string
            E.Call(D.LibMethod("System.String", "OrEmpty", 1).Fn);
            acc = Own.Unstable;
        }
        return acc;
    }

    /// <summary>Pushes a string for any value: strings as-is (null stays null; Concat treats it as empty), others via ToString().</summary>
    Own EmitToString(IOperation op, string? format = null)
    {
        while (!_precomputed.ContainsKey(op) && op is IConversionOperation { Conversion.IsImplicit: true } cv && cv.Operand.Type != null && cv.Type != null && (cv.Conversion.IsReference || (cv.Operand.Type.IsValueType && !cv.Type.IsValueType))) op = cv.Operand;
        var t = TypeOf(op);
        if (Types.IsString(t) && format == null) return EmitValue(op);
        return EmitToStringOfValue(() => EmitValue(op), t, format, op);
    }

    Own EmitToStringOfValue(Func<Own> produce, ITypeSymbol t, string? format, IOperation? origin)
    {
        var obj = D.ObjectType;
        if (IsNullable(t))
        {
            // T?: the value's string, or "" without one (as interpolation and ToString give)
            var nt0 = T.Canonical(t);
            produce();
            int na = E.AddLocal(ValType.I32); E.Set(na);
            int res = E.AddLocal(ValType.I32);
            E.Get(na); NullableHas(nt0);
            E.If();
            var so = EmitToStringOfValue(() => { E.Get(na); NullableVal(nt0); return Own.None; }, Underlying(nt0), format, origin);
            ToOwned(so); E.Set(res);
            E.Else();
            E.I32Addr(D.StringLiteral("")); E.Set(res);
            E.End();
            E.Get(res);
            int keep = TempRef(D.StringType); E.Tee(keep); E.Op(Op.Drop);
            E.Get(res);
            return Own.Unstable;
        }
        if (t.IsValueType)
        {
            // call T.ToString() / T.ToString(string) directly on the value
            var nt = (INamedTypeSymbol)T.Canonical(t);
            IMethodSymbol? m = format != null
                ? nt.GetMembers("ToString").OfType<IMethodSymbol>().FirstOrDefault(x => x.Parameters.Length == 1 && Types.IsString(x.Parameters[0].Type))
                  ?? nt.GetMembers("ToString").OfType<IMethodSymbol>().FirstOrDefault(x => x.Parameters.Length == 2 && Types.IsString(x.Parameters[0].Type))
                : nt.GetMembers("ToString").OfType<IMethodSymbol>().FirstOrDefault(x => x.Parameters.Length == 0 && !x.IsStatic);
            if (t.TypeKind == TypeKind.Enum)
            {
                // enums format through their boxed form
                var o0 = produce();
                EmitBox(t, o0);
                int b = TempRef(obj); E.Set(b);
                E.Get(b);
                if (format != null)
                {
                    // Enum.ToString(string): "D", "X", "F", "G"
                    var tsF = D.LibType("System.Enum").GetMembers("ToString").OfType<IMethodSymbol>()
                        .First(x => x.Parameters.Length == 1 && Types.IsString(x.Parameters[0].Type));
                    E.I32Addr(D.StringLiteral(format));
                    return EmitCallCore(tsF, null, [], false, preArgs: 2, forceInstance: true);
                }
                var tsE = obj.GetMembers("ToString").OfType<IMethodSymbol>().First(x => x.Parameters.Length == 0);
                return EmitCallCore(tsE, null, [], true, preArgs: 1, forceInstance: true);
            }
            if (m == null) throw new CompileException($"{t} has no ToString{(format != null ? "(string)" : "")} in cs2wasm's library yet");
            var own = produce();
            int recv;
            if (Types.IsStruct(nt)) { recv = Scratch(ValType.I32); E.Set(recv); }
            else
            {
                // primitives: method takes the address of the value
                int off = TempValue(nt);
                int sv = Scratch(Types.WasmType(nt)!.Value); E.Set(sv);
                E.Get(_fb); E.Get(sv); E.Store(Types.StoreOp(nt), off);
                FreeScratch(Types.WasmType(nt)!.Value, sv);
                recv = Scratch(ValType.I32); E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.Set(recv);
            }
            E.Get(recv);
            FreeScratch(ValType.I32, recv);
            if (format != null)
            {
                E.I32Addr(D.StringLiteral(format));
                if (m.Parameters.Length == 2) E.I32(0);
                return EmitCallCore(m, null, [], false, preArgs: m.Parameters.Length + 1, forceInstance: true);
            }
            return EmitCallCore(m, null, [], false, preArgs: 1, forceInstance: true);
        }
        // reference: null → "" ; else virtual ToString()
        var o = produce();
        o = ToCallSafe(o, t);
        int v = Scratch(ValType.I32); E.Set(v);
        E.Get(v);
        E.If(ValType.I32);
        E.Get(v);
        if (format != null && T.IsSubtype(t, D.Types.Canonical(D.Main.GetTypeByMetadataName("System.IFormattable")!)))
        {
            var fm = D.Main.GetTypeByMetadataName("System.IFormattable")!.GetMembers("ToString").OfType<IMethodSymbol>().First();
            E.I32Addr(D.StringLiteral(format)); E.I32(0);
            EmitCallCore(fm, null, [], true, preArgs: 3, forceInstance: true);
        }
        else
        {
            var ts = obj.GetMembers("ToString").OfType<IMethodSymbol>().First(x => x.Parameters.Length == 0);
            EmitCallCore(ts, null, [], true, preArgs: 1, forceInstance: true);
        }
        E.Else();
        E.I32Addr(D.StringLiteral(""));
        E.End();
        FreeScratch(ValType.I32, v);
        // both branches are owned (+1) or immortal; treat as owned (release of an immortal literal is a no-op)
        return Own.Owned;
    }

    Own EmitFormattableString(IInterpolatedStringOperation iso)
    {
        var format = new System.Text.StringBuilder();
        var holes = new List<IInterpolationOperation>();
        foreach (var part in iso.Parts)
        {
            if (part is IInterpolatedStringTextOperation text) format.Append(((string?)text.Text.ConstantValue.Value ?? "").Replace("{", "{{").Replace("}", "}}"));
            else if (part is IInterpolationOperation ip)
            {
                format.Append('{').Append(holes.Count);
                if (ip.Alignment?.ConstantValue.Value is int al) format.Append(',').Append(al);
                if (ip.FormatString?.ConstantValue.Value is string fs) format.Append(':').Append(fs);
                format.Append('}');
                holes.Add(ip);
            }
            else throw new CompileException("unsupported interpolation part", part.Syntax.GetLocation());
        }
        var objT = D.ObjectType;
        var arrT = T.Canonical(D.LibCompilation.CreateArrayTypeSymbol(objT));
        int arr = E.AddLocal(ValType.I32);
        E.I32(holes.Count); E.I32(4); E.Tid(D.MarkConstructed(arrT)); E.Call(Rt.AllocArray); E.Set(arr);
        var slot = new Slot { Local = arr, Type = arrT };
        RegisterTemp(slot);
        for (int i = 0; i < holes.Count; i++)
        {
            BeginTemps();
            var o = EmitValueAs(holes[i].Expression, objT);
            StoreToAddressFromStack(() => E.Get(arr), Types.ArrayDataOffset(1) + 4 * i, objT, o, pub: Publish.None);
            EndTemps();
        }
        var create = D.LibMethod("System.Runtime.CompilerServices.FormattableStringFactory", "Create", 2);
        E.I32Addr(D.StringLiteral(format.ToString())); E.Get(arr);
        E.Call(create.Fn);
        return Own.Owned;
    }

    Own EmitInterpolatedString(IInterpolatedStringOperation iso)
    {
        var parts = new List<Func<Own>>();
        foreach (var part in iso.Parts)
        {
            switch (part)
            {
                case IInterpolatedStringTextOperation text:
                    {
                        var s = (string?)text.Text.ConstantValue.Value ?? "";
                        parts.Add(() => { E.I32Addr(D.StringLiteral(s)); return Own.Stable; });
                        break;
                    }
                case IInterpolationOperation ip:
                    {
                        var fmt = ip.FormatString?.ConstantValue.Value as string;
                        var align = ip.Alignment?.ConstantValue.Value is int a ? a : (int?)null;
                        parts.Add(() =>
                        {
                            var o = EmitToString(ip.Expression, fmt);
                            if (align is int al && al != 0)
                            {
                                o = ToCallSafe(o, D.StringType);
                                E.I32(al);
                                E.Call(D.LibMethod("System.String", "Align", 2).Fn);
                                return Own.Owned;
                            }
                            return o;
                        });
                        break;
                    }
                default: throw new CompileException("unsupported interpolation part", part.Syntax.GetLocation());
            }
        }
        if (parts.Count == 0) { E.I32Addr(D.StringLiteral("")); return Own.Stable; }
        return ConcatParts(parts);
    }

    /// <summary>`"..."u8`: a ReadOnlySpan&lt;byte&gt; over the bytes (null-terminated, as C# emits them) in the data section.</summary>
    Own EmitUtf8Span(byte[] bytes, ITypeSymbol spanType)
    {
        var addr = D.DataBlob([.. bytes, 0], 1);
        var l = T.LayoutOf(spanType);
        int off = TempStruct(spanType);
        var refSlot = l.Fields.First(f => f.Symbol.Name == "_reference");
        var lenSlot = l.Fields.First(f => f.Symbol.Name == "_length");
        E.Get(_fb); E.I32Addr(addr); E.Store(Op.I32Store, off + refSlot.Offset);
        E.Get(_fb); E.I32(bytes.Length); E.Store(Op.I32Store, off + lenSlot.Offset);
        E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
        return Own.None;
    }

    /// <summary>`"a"u8 + "b"u8`: concatenated at compile time.</summary>
    static byte[]? Utf8Concat(IOperation op) => op switch
    {
        IUtf8StringOperation u => System.Text.Encoding.UTF8.GetBytes(u.Value),
        IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } b when Utf8Concat(b.LeftOperand) is { } l && Utf8Concat(b.RightOperand) is { } r => [.. l, .. r],
        IParenthesizedOperation p => Utf8Concat(p.Operand),
        _ => null,
    };

    // =====================================================================
    // assignment
    // =====================================================================
    /// <summary>The value of `r = ref x` used as an expression: what r now refers to.</summary>
    Own RefAssignResult(int addrLocal, ITypeSymbol t)
    {
        E.Get(addrLocal);
        if (Types.IsStruct(t)) return Own.None;
        E.Load(Types.LoadOp(t));
        return Types.IsRef(t) ? Own.Unstable : Own.None;
    }

    Own EmitAssign(ISimpleAssignmentOperation sa, bool needResult)
    {
        if (!needResult && TryEmitBorrowedOutStore(sa)) return Own.None;
        if (sa.IsRef)
        {
            // ref reassignment: ref local = ref expr
            if (sa.Target is ILocalReferenceOperation rl)
            {
                var v = LocalVar(rl.Local);
                EmitAddress(sa.Value); E.Set(v.AddrLocal);
                return needResult ? RefAssignResult(v.AddrLocal, TypeOf(sa)) : Own.None;
            }
            if (sa.Target is IParameterReferenceOperation { Parameter.RefKind: not RefKind.None } rp && !IsCaptured(rp.Parameter))
            {
                // ref parameter = ref expr
                var v = _vars[rp.Parameter];
                EmitAddress(sa.Value); E.Set(v.AddrLocal);
                return needResult ? RefAssignResult(v.AddrLocal, TypeOf(sa)) : Own.None;
            }
            if (sa.Target is IFieldReferenceOperation { Field.RefKind: not RefKind.None } rf)
            {
                // ref field = ref expr: the slot takes the address
                int so = EmitFieldSlotBase(rf);
                if (so != 0) { E.I32(so); E.Op(Op.I32Add); }
                int ra = E.AddLocal(ValType.I32);
                EmitAddress(sa.Value); E.Tee(ra);
                E.Store(Op.I32Store, 0);
                return needResult ? RefAssignResult(ra, TypeOf(sa)) : Own.None;
            }
            throw new CompileException("ref assignment target not supported", sa.Syntax.GetLocation());
        }
        var target = sa.Target;
        if (target is IDiscardOperation)
        {
            var o0 = EmitValue(sa.Value);
            if (needResult) return o0;
            Drop(o0, sa.Value.Type);
            return Own.None;
        }
        if (target is ILocalReferenceOperation { Local: { IsRef: false } bl } && WrittenBorrow(bl) is { } bwb && LocalVar(bl) is { InMemory: false } bv)
            return EmitBorrowAssign(bv, sa.Value, bwb, needResult);
        var tt = TypeOf(target);
        var lv = LValueOf(target);
        lv.Note(StaticValueType(sa.Value), sa.Value);
        lv.Prepare();
        // (`x = default`: from zeros, as EmitStoreField)
        if (Types.IsStruct(tt) && !s_noZeroDefault && IsZeroStruct(sa.Value) && sa.Value.Type != null && Types.Key(T.Canonical(TypeOf(sa.Value))) == Types.Key(T.Canonical(tt)))
        {
            int size = T.StorageOf(tt).size;
            E.I32Addr(D.StaticAddress("zeros:" + size, size, 8));
            return lv.Store(Own.None, needResult);
        }
        Own o = Types.IsStruct(tt) ? EmitValue(sa.Value) : EmitValueAs(sa.Value, tt);
        if (Types.IsStruct(tt) && sa.Value.Type != null) ConvertTop(TypeOf(sa.Value), tt, ref o);
        // (the value is a local of this method, read as it is: an element store of it cannot let go of the object the
        // local names, FieldBorrows.cs)
        _storedLocal = StoredLocalOf(sa.Value, out _storedParam);
        try { return lv.Store(o, needResult); } finally { _storedLocal = -1; _storedParam = -1; }
    }

    /// <summary>The register local an assignment stores, when its value is that local read as it is (through
    /// reference conversions only), else -1.</summary>
    int StoredLocalOf(IOperation v, out int param)
    {
        param = -1;
        while (v is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference && c.Conversion.IsImplicit)) v = c.Operand;
        if (_closure != null || _machine != null || v.Type == null || TypeOf(v).IsValueType) return -1;
        if (v is IParameterReferenceOperation { Parameter: { RefKind: RefKind.None } p } pref && !IsPrimaryParamHere(p) && !IsCaptured(p) && !IsMovedParamUse(pref)
            && _vars.TryGetValue(p, out var pv) && !pv.InMemory)
        { param = p.Ordinal; return pv.Local; }
        if (v is not ILocalReferenceOperation { Local: { IsRef: false } l } || IsCaptured(l)) return -1;
        return LocalVar(l) is { InMemory: false } lv ? lv.Local : -1;
    }
    int _storedLocal = -1, _storedParam = -1;

    Own EmitCompound(ICompoundAssignmentOperation ca, bool needResult)
    {
        var tt = TypeOf(ca.Target);
        var lv = LValueOf(ca.Target);
        lv.Prepare();
        var vt = ca.Value.Type != null ? TypeOf(ca.Value) : tt;
        var caOp = ca.OperatorMethod ?? DynamicOperatorSite(ca) ?? (IsDecimal(tt) ? DecimalOpFor(ca.OperatorKind) : null);
        // a compound assignment operator declared as an instance member (C# 14: `public void operator +=(int n)`):
        // a call on the target, whose value is the expression's
        if (caOp is { IsStatic: false }) return EmitInstanceOperator(caOp, ca.Target, [ca.Value], needResult, ca.Syntax);
        // (lifted: the operator is the underlying type's, `x op= y` on a T? is null unless both have a value: below)
        bool liftedOp = IsNullable(tt) && !(caOp != null && IsNullable(T.Resolve(caOp.Parameters[0].Type, Map)));
        if (caOp != null && !liftedOp)
        {
            var m = caOp;
            // a static abstract operator through a constrained type parameter (`s += x` on a T : IAdditionOperators):
            // the type argument's own operator (the primitives' are written out where they are called)
            if (IsStaticInterfaceMember(m) && ca.ConstrainedToType is { } cct && T.Resolve(cct, Map) is var concrete
                && Types.PrimOf(concrete) == Prim.None && ImplementationOf(concrete, SubstMethod(m)) is { } impl)
                m = impl;
            var p0 = T.Resolve(m.Parameters[0].Type, Map);
            var p1 = T.Resolve(m.Parameters[1].Type, Map);
            var r = CallWith(SubstMethod(m), false, null, () =>
            {
                var cur = lv.Load();
                ConvertTop(tt, p0, ref cur);
                if (Types.IsStruct(p0)) CopyToTemp(p0); else ToCallSafe(cur, p0);
                var ov = EmitValueAs(ca.Value, p1);
                if (Types.IsRef(p1)) ToCallSafe(ov, p1);
            }, null, ca.Syntax);
            // (a dynamic operation on a variable of a type of its own: the site that converts the result back)
            if (ca.OperatorMethod == null && D.Dynamic.Any && D.Dynamic.Find(ca, "conv") is { } convSite)
            {
                ToOwned(r);
                int dyn = TempRef(D.ObjectType); E.Set(dyn);
                var cm = SiteMethod(convSite);
                r = CallWith(cm, false, null, () => E.Get(dyn), null, ca.Syntax);
                ConvertTop(T.Resolve(cm.ReturnType, Map), tt, ref r);
                return lv.Store(r, needResult);
            }
            ConvertTop(T.Resolve(m.ReturnType, Map), tt, ref r);
            return lv.Store(r, needResult);
        }
        if (ca.OperatorKind == BinaryOperatorKind.Add && Types.IsString(tt))
        {
            var cur = lv.Load();
            var curL = ToCallSafe(cur, tt);
            int c0 = Scratch(ValType.I32); E.Set(c0);
            var r = ConcatParts([() => { E.Get(c0); return Own.Stable; }, () => EmitToString(ca.Value)]);
            FreeScratch(ValType.I32, c0);
            return lv.Store(r, needResult);
        }
        if (ca.OperatorKind == BinaryOperatorKind.Add && tt.SpecialType == SpecialType.System_Object)
        {
            // o += x on an object: the text of what it holds (none for null) and x's, as a string
            var cur = lv.Load();
            ToCallSafe(cur, tt);
            E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "TextOf", 1).Fn);
            int c0 = Scratch(ValType.I32); E.Set(c0);
            var r = ConcatParts([() => { E.Get(c0); return Own.Owned; }, () => EmitToString(ca.Value)]);
            FreeScratch(ValType.I32, c0);
            return lv.Store(r, needResult);
        }
        if (ca.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract && tt.TypeKind == TypeKind.Delegate)
        {
            var cur = lv.Load(); ToCallSafe(cur, tt);
            var ov = EmitValue(ca.Value); ToCallSafe(ov, tt);
            E.Call(D.LibMethod("System.Delegate", ca.OperatorKind == BinaryOperatorKind.Add ? "Combine" : "Remove", 2).Fn);
            return lv.Store(Own.Owned, needResult);
        }
        if (IsNullable(tt))
        {
            // lifted: x op= y is x = (x.HasValue && y.HasValue) ? x.Value op y.Value : null
            var cur = lv.Load();
            int la = E.AddLocal(ValType.I32);
            int ct = TempStruct(tt);
            { int src = E.AddLocal(ValType.I32); E.Set(src); E.Get(_fb); E.I32(ct); E.Op(Op.I32Add); E.Get(src); CopyStructInit(tt); }
            E.Get(_fb); E.I32(ct); E.Op(Op.I32Add); E.Set(la);
            int ra = LiftedOperand(ca.Value, IsNullable(vt) ? vt : tt);
            var ntR = IsNullable(vt) ? vt : tt;
            var u = Underlying(tt);
            int res = TempStruct(tt);
            E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.I32(0); E.I32(T.StorageOf(tt).size); E.Op(Op.MemoryFill);
            E.Get(la); NullableHas(tt); E.Get(ra); NullableHas(ntR); E.Op(Op.I32And);
            E.If();
            var up = Types.PrimOf(u);
            // an operator that is a method (the type's own, decimal's): called on the two values
            if ((caOp ?? (IsDecimal(u) ? DecimalOpFor(ca.OperatorKind) : null)) is { } liftOp)
            {
                var lm = SubstMethod(liftOp);
                var q0 = T.Resolve(lm.Parameters[0].Type, Map); var q1 = T.Resolve(lm.Parameters[1].Type, Map);
                var rr = CallWith(lm, false, null, () =>
                {
                    E.Get(la); NullableVal(tt);
                    var a0 = Own.None; ConvertTop(u, q0, ref a0);
                    if (Types.IsStruct(q0)) CopyToTemp(q0);
                    E.Get(ra); NullableVal(ntR);
                    var a1 = Own.None; ConvertTop(Underlying(ntR), q1, ref a1);
                    if (Types.IsStruct(q1)) CopyToTemp(q1);
                }, null, ca.Syntax);
                ConvertTop(T.Resolve(lm.ReturnType, Map), u, ref rr);
                WrapNullable(tt, rr);
                { int w = E.AddLocal(ValType.I32); E.Set(w); E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.Get(w); CopyStructInit(tt); }
                E.End();
                E.Get(_fb); E.I32(res); E.Op(Op.I32Add);
                return lv.Store(Own.None, needResult);
            }
            if (up == Prim.None) throw new CompileException($"lifted compound assignment on {tt} is not supported", ca.Syntax.GetLocation());
            E.Get(la); NullableVal(tt);
            E.Get(ra); NullableVal(ntR);
            var ur = Underlying(ntR);
            if (Types.Key(ur) != Types.Key(u)) { var tmpO = Own.None; ConvertValue(ur, u, false, ref tmpO, false, null); }
            EmitArith(ca.OperatorKind, up, ca.IsChecked, up);
            WrapNullable(tt, Own.None);
            { int w = E.AddLocal(ValType.I32); E.Set(w); E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.Get(w); CopyStructInit(tt); }
            E.End();
            E.Get(_fb); E.I32(res); E.Op(Op.I32Add);
            return lv.Store(Own.None, needResult);
        }
        if (tt is IPointerTypeSymbol ptrT && ca.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)
        {
            // p += n: n elements
            lv.Load();
            EmitValue(ca.Value); if (Types.Is64(Types.PrimOf(vt))) E.Op(Op.I32WrapI64);
            int es = T.StorageOf(T.Resolve(ptrT.PointedAtType, Map)).size;
            if (es != 1) { E.I32(es); E.Op(Op.I32Mul); }
            E.Op(ca.OperatorKind == BinaryOperatorKind.Add ? Op.I32Add : Op.I32Sub);
            return lv.Store(Own.None, needResult);
        }
        var pt = Types.PrimOf(tt);
        if (pt == Prim.None) throw new CompileException($"compound assignment on {tt} not supported", ca.Syntax.GetLocation());
        var opP = ca.OperatorKind is BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift or BinaryOperatorKind.UnsignedRightShift
            ? Promote(pt, pt) : Promote(pt, Types.PrimOf(vt));
        if (tt.TypeKind == TypeKind.Enum || pt == Prim.Bool) opP = pt;
        lv.Load();
        NumericConvert(pt, opP, false);
        EmitValue(ca.Value);
        var vp = Types.PrimOf(vt);
        if (ca.OperatorKind is BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift or BinaryOperatorKind.UnsignedRightShift)
        { if (Types.Is64(opP) && !Types.Is64(vp)) E.Op(Op.I64ExtendI32U); else if (!Types.Is64(opP) && Types.Is64(vp)) E.Op(Op.I32WrapI64); }
        else NumericConvert(vp, opP, false);
        EmitArith(ca.OperatorKind, opP, ca.IsChecked, opP);
        NumericConvert(opP, pt, ca.IsChecked && opP != pt);
        return lv.Store(Own.None, needResult);
    }

    /// <summary>`target op= value` and `++target` where the operator is an instance member of the target's type: the
    /// member is called on the target (a variable, for a struct), and the target is the value of the expression.</summary>
    Own EmitInstanceOperator(IMethodSymbol op, IOperation target, IReadOnlyList<IOperation> args, bool needResult, SyntaxNode syntax)
    {
        var tt = TypeOf(target);
        bool virt = op.IsVirtual || op.IsAbstract || op.IsOverride || op.ContainingType.TypeKind == TypeKind.Interface;
        if (!needResult || tt.IsValueType || tt.TypeKind == TypeKind.TypeParameter)
        {
            var r = EmitCall(op, target, args, virt, syntax);
            if (!op.ReturnsVoid) Drop(r, T.Resolve(op.ReturnType, Map));
            return needResult ? EmitValue(target) : Own.None;
        }
        // a reference: evaluated once, held for the statement
        int recv = E.AddLocal(ValType.I32);
        Own held = Own.None;
        var m = SubstMethod(op);
        if (virt && tt is INamedTypeSymbol { IsSealed: true, TypeKind: TypeKind.Class } st && D.Implementation(st, m) is { } simpl) { m = simpl; virt = false; }
        var cr = CallWith(m, virt, () =>
        {
            var o = EmitValue(target);
            held = ToCallSafe(o, tt);
            NullCheckKeep();
            E.Tee(recv);
        }, () =>
        {
            for (int i = 0; i < args.Count; i++)
            {
                var pt = T.Resolve(m.Parameters[i].Type, Map);
                var ov = Types.IsStruct(pt) ? EmitValue(args[i]) : EmitValueAs(args[i], pt);
                if (Types.IsRef(pt)) ToCallSafe(ov, pt);
            }
        }, null, syntax);
        if (!op.ReturnsVoid) Drop(cr, T.Resolve(op.ReturnType, Map));
        E.Get(recv);
        return held;
    }

    /// <summary>Stack: struct address → address of a temporary copy (a by-value struct argument).</summary>
    void CopyToTemp(ITypeSymbol t)
    {
        int src = Scratch(ValType.I32); E.Set(src);
        int off = TempStruct(t);
        E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.Get(src); CopyStructInit(t);
        FreeScratch(ValType.I32, src);
        E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
    }

    static Prim Promote(Prim a, Prim b)
    {
        if (a == Prim.F64 || b == Prim.F64) return Prim.F64;
        if (a == Prim.F32 || b == Prim.F32) return Prim.F32;
        if (a == Prim.U64 || b == Prim.U64) return Prim.U64;
        if (a == Prim.I64 || b == Prim.I64) return Prim.I64;
        if (a == Prim.U32 || b == Prim.U32)
        {
            var o = a == Prim.U32 ? b : a;
            return o is Prim.I8 or Prim.I16 or Prim.I32 ? Prim.I64 : Prim.U32;
        }
        if (a == Prim.IntPtr || b == Prim.IntPtr) return Prim.IntPtr;
        if (a == Prim.UIntPtr || b == Prim.UIntPtr) return Prim.UIntPtr;
        return Prim.I32;
    }

    Own EmitIncDec(IIncrementOrDecrementOperation inc, bool needResult)
    {
        var tt = TypeOf(inc.Target);
        var lv = LValueOf(inc.Target);
        lv.Prepare();
        bool isInc = inc.Kind == OperationKind.Increment;
        var incOp = inc.OperatorMethod ?? DynamicOperatorSite(inc) ?? (IsDecimal(tt) ? DecimalOp(isInc ? "op_Increment" : "op_Decrement", 1) : null);
        if (incOp is { IsStatic: false }) return EmitInstanceOperator(incOp, inc.Target, [], needResult, inc.Syntax);
        if (incOp != null && IsStaticInterfaceMember(incOp) && inc.ConstrainedToType is { } ict && T.Resolve(ict, Map) is var iconcrete
            && Types.PrimOf(iconcrete) == Prim.None && ImplementationOf(iconcrete, SubstMethod(incOp)) is { } iimpl)
            incOp = iimpl;
        // (lifted: the operator is the underlying type's, `x++` on a T? stays null when x is: below)
        bool liftedInc = IsNullable(tt) && !(incOp != null && IsNullable(T.Resolve(incOp.Parameters[0].Type, Map)));
        if (incOp != null && !liftedInc)
        {
            Own cur = Own.None;
            int old = -1;
            var r = CallWith(SubstMethod(incOp), false, null, () =>
            {
                cur = lv.Load();
                if (Types.IsStruct(tt)) CopyToTemp(tt); else cur = ToCallSafe(cur, tt);
                if (needResult && inc.IsPostfix) { old = E.AddLocal(Types.WasmType(tt) ?? ValType.I32); E.Tee(old); }
            }, null, inc.Syntax);
            var res = lv.Store(r, needResult && !inc.IsPostfix);
            if (old >= 0) { E.Get(old); return cur; }
            return res;
        }
        if (IsNullable(tt))
        {
            // lifted: has ? value ± 1 : null
            var u = Underlying(tt);
            var up = Types.PrimOf(u);
            var uw = Types.WasmPrim(up);
            var cur = lv.Load();
            int ca = E.AddLocal(ValType.I32); E.Set(ca);
            int old = -1;
            if (needResult && inc.IsPostfix) { old = TempStruct(tt); E.Get(_fb); E.I32(old); E.Op(Op.I32Add); E.Get(ca); CopyStructInit(tt); }
            int res = TempStruct(tt);
            E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.I32(0); E.I32(T.StorageOf(tt).size); E.Op(Op.MemoryFill);
            E.Get(ca); NullableHas(tt);
            E.If();
            var wrapped = Own.None;
            // an operator that is a method (the type's own, decimal's): called on the value
            if ((incOp ?? (IsDecimal(u) ? DecimalOp(isInc ? "op_Increment" : "op_Decrement", 1) : null)) is { } liftOp)
            {
                var lm = SubstMethod(liftOp);
                var q0 = T.Resolve(lm.Parameters[0].Type, Map);
                wrapped = CallWith(lm, false, null, () =>
                {
                    E.Get(ca); NullableVal(tt);
                    var a0 = Own.None; ConvertTop(u, q0, ref a0);
                    if (Types.IsStruct(q0)) CopyToTemp(q0);
                }, null, inc.Syntax);
                ConvertTop(T.Resolve(lm.ReturnType, Map), u, ref wrapped);
            }
            else
            {
                if (up == Prim.None) throw new CompileException("++/-- on " + tt, inc.Syntax.GetLocation());
                E.Get(ca); NullableVal(tt);
                switch (uw) { case ValType.I64: E.I64(1); break; case ValType.F32: E.F32(1); break; case ValType.F64: E.F64(1); break; default: E.I32(1); break; }
                var uop = Types.PrimSize(up) < 4 ? (Types.IsSigned(up) ? Prim.I32 : Prim.U32) : up;
                EmitArith(isInc ? BinaryOperatorKind.Add : BinaryOperatorKind.Subtract, uop, inc.IsChecked && uop == up, uop);
                if (uop != up) Narrow(up);
            }
            WrapNullable(tt, wrapped);
            int wn = Scratch(ValType.I32); E.Set(wn);
            E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.Get(wn); CopyStructInit(tt);
            FreeScratch(ValType.I32, wn);
            E.End();
            E.Get(_fb); E.I32(res); E.Op(Op.I32Add);
            var stored = lv.Store(Own.None, needResult && !inc.IsPostfix);
            if (old >= 0) { E.Get(_fb); E.I32(old); E.Op(Op.I32Add); return Own.None; }
            return stored;
        }
        var p = Types.PrimOf(tt);
        if (p == Prim.None) throw new CompileException("++/-- on " + tt);
        var w = Types.WasmPrim(p);
        lv.Load();
        int oldV = -1;
        if (needResult && inc.IsPostfix) { oldV = Scratch(w); E.Tee(oldV); }
        var opP = Types.PrimSize(p) < 4 ? (Types.IsSigned(p) ? Prim.I32 : Prim.U32) : p;
        if (tt.TypeKind == TypeKind.Pointer)
        {
            E.I32(T.StorageOf(T.Resolve(((IPointerTypeSymbol)tt).PointedAtType, Map)).size);
            E.Op(isInc ? Op.I32Add : Op.I32Sub);
        }
        else
        {
            switch (w)
            {
                case ValType.I64: E.I64(1); break;
                case ValType.F32: E.F32(1); break;
                case ValType.F64: E.F64(1); break;
                default: E.I32(1); break;
            }
            EmitArith(isInc ? BinaryOperatorKind.Add : BinaryOperatorKind.Subtract, opP, inc.IsChecked && opP == p, opP);
            if (opP != p) { if (inc.IsChecked) CheckIntRange(opP == Prim.I32 ? Prim.I32 : Prim.U32, p); Narrow(p); }
        }
        if (oldV >= 0)
        {
            lv.Store(Own.None, false);
            E.Get(oldV); FreeScratch(w, oldV);
            return Own.None;
        }
        return lv.Store(Own.None, needResult);
    }

    Own EmitCoalesceAssign(ICoalesceAssignmentOperation cas, bool needResult)
    {
        var tt = TypeOf(cas.Target);
        if (!Types.IsRef(tt) && !IsNullable(tt)) throw new CompileException("??= on this type is not supported", cas.Syntax.GetLocation());
        // (the store runs only where the place holds null: Driver.BorrowWrites; the write is noted with the place)
        var savedNullOnly = Driver.NullOnlyStoreIn;
        if (Types.IsRef(tt) && cas.Target is IFieldReferenceOperation { Field.IsVolatile: false }) Driver.NullOnlyStoreIn = E.Fn;
        LValue lv;
        try { lv = LValueOf(cas.Target); } finally { Driver.NullOnlyStoreIn = savedNullOnly; }
        lv.Note(StaticValueType(cas.Value), cas.Value);
        lv.Prepare();
        lv.Load();
        if (IsNullable(tt)) NullableHas(tt);
        E.Op(Op.I32Eqz);
        E.If();
        var o = EmitValueAs(cas.Value, tt);
        lv.Store(o, false);
        E.End();
        if (!needResult) return Own.None;
        var r = lv.Load();
        // `x ??= v` on a T? has type T when v does: the value, not the T?
        if (IsNullable(tt) && !IsNullable(TypeOf(cas))) { NullableVal(tt); var u = Underlying(tt); return Types.IsRef(u) ? Own.Unstable : Own.None; }
        return r;
    }

    void EmitEventAssign(IEventAssignmentOperation ea)
    {
        var er = (IEventReferenceOperation)ea.EventReference;
        var ev = er.Event;
        var acc = ea.Adds ? ev.AddMethod! : ev.RemoveMethod!;
        // field-like events inside their own type assign the field directly through the accessor anyway
        EmitDiscardCall(acc, er.Instance, [ea.HandlerValue], IsVirtualCall(er.Instance, acc), ea.Syntax);
    }

    void EmitDiscardCall(IMethodSymbol m, IOperation? inst, IReadOnlyList<IOperation> args, bool virt, SyntaxNode syn)
    {
        var o = EmitCall(m, inst, args, virt, syn);
        var rt = T.Resolve(m.ReturnType, Map);
        if (!Types.IsVoid(rt)) Drop(o, rt);
    }

    void EmitDeconstruction(IDeconstructionAssignmentOperation da) => EmitDeconstructionCore(da);

    // ---------------- lvalues ----------------
    abstract class LValue
    {
        protected readonly FunctionCompiler F;
        public readonly ITypeSymbol Type;
        protected LValue(FunctionCompiler f, ITypeSymbol t) { F = f; Type = t; }
        public abstract void Prepare();
        public abstract Own Load();
        /// <summary>Stores the value on the stack; if needResult, pushes the stored value (borrowed from the target).</summary>
        public abstract Own Store(Own o, bool needResult);
        protected bool _noted;
        /// <summary>The static type of the value about to be stored (before conversion), for the cycle analysis.</summary>
        public void Note(ITypeSymbol? vt, IOperation? value = null) { _noted = true; NoteValue(vt, value); }
        public virtual void NoteValue(ITypeSymbol? vt, IOperation? value) { }
    }

    sealed class VarLV(FunctionCompiler f, Var v) : LValue(f, v.Type)
    {
        public override void Prepare() { }
        public override Own Load() => F.LoadVar(v);
        public override Own Store(Own o, bool needResult)
        {
            F.StoreVar(v, o);
            return needResult ? F.LoadVar(v) : Own.None;
        }
    }

    sealed class MemLV(FunctionCompiler f, ITypeSymbol t, Func<int> pushBase, Arenas.Site? site = null, int container = -1, WasmFunction? pin = null, Publish pub = Publish.Unknown, bool elements = false, string? initKey = null, bool fresh = false, IArrayTypeSymbol? covariant = null, bool owned = false, string? staticKey = null, bool threadStatic = false, string? structField = null, string? outParam = null, bool viaPointer = false, bool iso = false, bool initNull = false, ITypeSymbol? structContainer = null, IOperation? attachPlace = null, int lastLocal = -1) : LValue(f, t)
    {
        /// <summary>Sharing.cs: where the value stored here comes from.</summary>
        void Record(List<Sharing.Src>? srcs)
        {
            if (F.Sh is not { } sh) return;
            srcs ??= [Sharing.Debug ? new Sharing.Src(Sharing.K.Top, "a store with no value seen in " + F.M.ToDisplayString(), Type: Type) : TopSrc(Type)];
            if (site != null) return;
            if (staticKey != null) sh.Static(staticKey, Type, threadStatic, srcs);
            else if (structField != null) sh.Add(structField, srcs);
            else if (outParam != null) sh.Add(outParam, srcs);
            else if (viaPointer) { if (Types.IsRef(Type)) sh.Add(Sharing.PtrNode(F.T.Canonical(Type)), srcs); }
            // (Unsafe.Add(ref x, n) = v: into the place x names)
            else if (attachPlace != null) F.AttachTo(attachPlace, Type, srcs);
            else if (!F.InRefWriter) sh.RefStore(Type, srcs, F.M.ToDisplayString());
        }
        int _addr = -1, _off;
        /// <summary>The static type of the value stored (NoteValue), for a store by address (RefStores.cs).</summary>
        ITypeSymbol? _valueType;
        public override void Prepare() { _off = pushBase(); _addr = F.E.AddLocal(ValType.I32); F.E.Set(_addr); }
        public override void NoteValue(ITypeSymbol? vt, IOperation? value)
        {
            _valueType = vt;
            var srcs = F.Sh != null && value != null ? F.FlowSrcs(value) : null;
            site?.AddValue(vt, srcs);
            Record(srcs);
        }
        public override Own Load()
        {
            F.E.Get(_addr);
            if (Types.IsStruct(Type)) { if (_off != 0) { F.E.I32(_off); F.E.Op(Op.I32Add); } return Own.None; }
            F.E.Load(Types.LoadOp(Type), _off);
            return Types.IsRef(Type) ? Own.Unstable : Own.None;
        }
        public override Own Store(Own o, bool needResult)
        {
            if (site != null && !_noted) site.AddValue(Type);
            if (!_noted) Record(null);
            if (covariant != null && container >= 0)
            {
                // An array of a derived element type can stand where this array type does (string[] as object[]):
                // what is stored must be an element of the array that is there (ArrayTypeMismatchException). The
                // array is of the very type the code names, nearly always: one comparison.
                int sv = F.Scratch(ValType.I32);
                F.E.Tee(sv);
                F.E.Get(container); F.E.Load(Op.I32Load, 4); F.E.Tid(F.D.TypeId(covariant)); F.E.Op(Op.I32Ne);
                F.E.If();
                F.E.Get(container); F.E.Get(sv); F.E.Call(F.D.LibMethod("Cs2Wasm.Runtime.Rt", "StoreCheck", 2).Fn);
                F.E.End();
                F.FreeScratch(ValType.I32, sv);
            }
            // (an element, or memory behind an address: a borrowed element may be the one overwritten, FieldBorrows.cs)
            F.StoreToAddressFromStack(() => F.E.Get(_addr), _off, Type, o, site, site == null ? null : () => F.E.Get(container >= 0 ? container : _addr), pin,
                pub, () => F.E.Get(container >= 0 ? container : _addr), initKey, fresh: fresh, owned: owned, iso: iso, initNull: initNull, structContainer: structContainer,
                valueType: _valueType, viaPointer: viaPointer, lastLocal: lastLocal);
            if (elements && F.T.NeedsRc(Type))
            {
                int before = F.E.Body.Count;
                F.D.NoteFieldWrite(F.E, Driver.ElementsKey);
                // (the mark of a plain element store of a local's own value names the local: the store lets go of the
                // slot's old value after it has counted the new one, so the object the local names stays held)
                if (F._storedLocal >= 0 && F.E.Body.Count == before + 1) F.E.Body[^1] = F.E.Body[^1] with { Imm = F._storedLocal + 1, Ref = new Driver.SelfStoreMark(Driver.ElementsKey, site, F._storedParam) };
            }
            return needResult ? Load() : Own.None;
        }
    }

    sealed class PropLV(FunctionCompiler f, IPropertyReferenceOperation pr, ITypeSymbol t) : LValue(f, t)
    {
        int _recv = -1; bool _recvIsStruct;
        List<Sharing.Src>? _valueSrcs;
        public override void NoteValue(ITypeSymbol? vt, IOperation? value) { if (F.Sh != null && value != null) _valueSrcs = F.FlowSrcs(value); }
        void RecordIndexArgs(IMethodSymbol acc)
        {
            if (F.Sh == null) return;
            for (int k = 0; k < pr.Arguments.Length && k < acc.Parameters.Length; k++) F.RecordParam(acc, acc.Parameters[k], F.FlowSrcs(pr.Arguments[k].Value));
        }
        readonly List<(int local, ITypeSymbol type)> _args = [];
        public override void Prepare()
        {
            var p = pr.Property;
            if (!p.IsStatic)
            {
                var it = F.TypeOf(pr.Instance!);
                _recvIsStruct = it.IsValueType;
                if (_recvIsStruct)
                {
                    if (F.IsAddressable(pr.Instance!)) F.EmitAddress(pr.Instance!); else F.EmitValue(pr.Instance!);
                }
                else
                {
                    var o = F.EmitValue(pr.Instance!);
                    F.ToCallSafe(o, it);
                    if (!F.IsKnownNonNull(pr.Instance!)) F.NullCheckKeep();
                }
                _recv = F.E.AddLocal(ValType.I32); F.E.Set(_recv);
            }
            foreach (var a in pr.Arguments)
            {
                var at = F.T.Resolve(a.Parameter!.Type, F.Map);
                var o = F.EmitValueAs(a.Value, at);
                if (Types.IsRef(at)) F.ToCallSafe(o, at);
                int l = F.E.AddLocal(Types.WasmType(at) ?? ValType.I32); F.E.Set(l);
                _args.Add((l, at));
            }
        }
        /// <summary>On a struct receiver (a type parameter instantiated with a struct, say), the struct's own
        /// implementation of an interface's accessor.</summary>
        IMethodSymbol Accessor(IMethodSymbol acc)
        {
            if (!_recvIsStruct || acc.IsStatic) return acc;
            var rt = F.T.Canonical(F.TypeOf(pr.Instance!));
            if (Types.Key(F.T.Canonical(acc.ContainingType)) == Types.Key(rt)) return acc;
            if (F.D.Implementation(rt, acc) is { } impl && Types.Key(F.T.Canonical(impl.ContainingType)) == Types.Key(rt)) return impl;
            return acc;
        }

        void PushRecvAndArgs()
        {
            if (_recv >= 0) F.E.Get(_recv);
            foreach (var (l, _) in _args) F.E.Get(l);
        }
        public override Own Load()
        {
            var g = pr.Property.GetMethod ?? throw new CompileException("property has no getter");
            var gm = Accessor(F.SubstMethod(g));
            return F.CallWith(gm, !_recvIsStruct && F.IsVirtualCall(pr.Instance, g), _recv >= 0 ? () => F.E.Get(_recv) : null, () => { foreach (var (l, _) in _args) F.E.Get(l); RecordIndexArgs(gm); }, null, pr.Syntax);
        }
        public override Own Store(Own o, bool needResult)
        {
            var s = pr.Property.SetMethod ?? throw new CompileException("property has no setter");
            var vt = Type;
            int v;
            var sm = Accessor(F.SubstMethod(s));
            // (a fresh value for Dictionary's indexer, whose setter hands it on to TryInsert, which consumes it:
            // the temporary that holds its count until the call is noted, and the count goes to the callee as a
            // fresh argument's does, OwnedParams.cs IsMapInsert; `map[key] = new Item()`)
            int handOver = -1;
            if (Types.IsRef(vt))
            {
                if (o == Own.Owned && !needResult && !F.D.ThreadMode && sm.Parameters.Length > 0 && Driver.IsMapInsert(sm.OriginalDefinition))
                {
                    handOver = F.TempRef(vt);
                    F.E.Tee(handOver);
                    o = Own.Stable;
                }
                else o = F.ToCallSafe(o, vt);
                v = F.Scratch(ValType.I32); F.E.Set(v);
            }
            else { v = F.Scratch(Types.WasmType(vt)!.Value); F.E.Set(v); }
            F.CallWith(sm, !_recvIsStruct && F.IsVirtualCall(pr.Instance, s), _recv >= 0 ? () => F.E.Get(_recv) : null, () =>
            {
                foreach (var (l, _) in _args) F.E.Get(l);
                F.E.Get(v);
                if (handOver >= 0 && F._consume is { } cc && (cc.Mask & (1 << sm.Parameters[^1].Ordinal)) != 0 && SymbolEqualityComparer.Default.Equals(sm.Parameters[^1].ContainingSymbol, cc.M))
                {
                    cc.Temps.Add(handOver);
                    cc.Moved |= 1 << sm.Parameters[^1].Ordinal;
                }
                RecordIndexArgs(sm);
                if (sm.Parameters.Length > 0) F.RecordParam(sm, sm.Parameters[^1], _valueSrcs ?? [TopSrc(vt)]);
            }, null, pr.Syntax);
            // (the value was a local of this method read as it is: FieldBorrows.cs, ArgLocalMark)
            if (F._storedLocal >= 0 && Types.IsRef(vt) && sm.Parameters.Length > 0 && F.E.Body.Count > 0 && F.E.Body[^1].Op == Op.Call)
            {
                F.E.Emit(Op.Nop, 0, 0, new Driver.ArgLocalMark(sm.Parameters.Length - 1, F._storedLocal));
                F.E.Body[^1] = F.E.Body[^1] with { Pos = -1 };
            }
            if (needResult) { F.E.Get(v); F.FreeScratch(Types.WasmType(vt) ?? ValType.I32, v); return Types.IsRef(vt) ? Own.Stable : Own.None; }
            F.FreeScratch(Types.WasmType(vt) ?? ValType.I32, v);
            return Own.None;
        }
    }

    /// <summary>`list[^i] = v` (and `list[^i] += v`): the receiver and the offset from the end once, then the
    /// indexer's getter and setter, as C# lowers it.</summary>
    sealed class ImplicitIndexerLV(FunctionCompiler f, IImplicitIndexerReferenceOperation ii, ITypeSymbol t) : LValue(f, t)
    {
        int _recv = -1, _idx = -1; bool _recvIsStruct;
        IPropertySymbol Indexer => (IPropertySymbol)ii.IndexerSymbol!;
        public override void Prepare()
        {
            var rt = F.TypeOf(ii.Instance);
            _recvIsStruct = rt.IsValueType;
            if (_recvIsStruct) { if (F.IsAddressable(ii.Instance)) F.EmitAddress(ii.Instance); else F.EmitValue(ii.Instance); }
            else
            {
                var o = F.EmitValue(ii.Instance);
                F.ToCallSafe(o, rt);
                if (!F.IsKnownNonNull(ii.Instance)) F.NullCheckKeep();
            }
            _recv = F.E.AddLocal(ValType.I32); F.E.Set(_recv);
            var lengthGetter = ii.LengthSymbol switch { IPropertySymbol p => p.GetMethod!, IMethodSymbol m => m, _ => throw new CompileException("implicit indexer without a length", ii.Syntax.GetLocation()) };
            int len = F.E.AddLocal(ValType.I32);
            F.CallWith(F.SubstMethod(lengthGetter), !_recvIsStruct, () => F.E.Get(_recv), () => { }, null, ii.Syntax);
            F.E.Set(len);
            _idx = F.E.AddLocal(ValType.I32);
            F.EmitIndexOffset(ii.Argument, len, fromStartDefault: true); F.E.Set(_idx);
        }
        public override Own Load()
        {
            var g = F.SubstMethod(Indexer.GetMethod ?? throw new CompileException("indexer has no getter"));
            return F.CallWith(g, !_recvIsStruct && IsVirtualDispatch(g), () => F.E.Get(_recv), () => F.E.Get(_idx), null, ii.Syntax);
        }
        public override Own Store(Own o, bool needResult)
        {
            var sm = F.SubstMethod(Indexer.SetMethod ?? throw new CompileException("indexer has no setter"));
            var vt = Type;
            int v;
            if (Types.IsRef(vt)) { o = F.ToCallSafe(o, vt); v = F.Scratch(ValType.I32); F.E.Set(v); }
            else { v = F.Scratch(Types.WasmType(vt)!.Value); F.E.Set(v); }
            F.CallWith(sm, !_recvIsStruct && IsVirtualDispatch(sm), () => F.E.Get(_recv), () => { F.E.Get(_idx); F.E.Get(v); }, null, ii.Syntax);
            if (needResult) { F.E.Get(v); F.FreeScratch(Types.WasmType(vt) ?? ValType.I32, v); return Types.IsRef(vt) ? Own.Stable : Own.None; }
            F.FreeScratch(Types.WasmType(vt) ?? ValType.I32, v);
            return Own.None;
        }
    }

    sealed class CapturedLV(FunctionCompiler f, ISymbol sym, ITypeSymbol t) : LValue(f, t)
    {
        public override void Prepare() { }
        public override Own Load() => F.LoadCaptured(sym);
        public override Own Store(Own o, bool needResult)
        {
            int off = 0;
            int a = F.Scratch(ValType.I32);
            // value is on the stack; compute the address after it
            var vt = Types.WasmType(Type) ?? ValType.I32;
            int v = F.Scratch(vt);
            if (Types.IsRef(Type)) F.ToOwned(o, Type);
            F.E.Set(v);
            off = F.CapturedAddress(sym); F.E.Set(a);
            F.E.Get(v);
            F.StoreToAddressFromStack(() => F.E.Get(a), off, Type, Types.IsRef(Type) ? Own.Owned : Own.None);
            F.FreeScratch(vt, v); F.FreeScratch(ValType.I32, a);
            return needResult ? F.LoadCaptured(sym) : Own.None;
        }
    }

    /// <summary>The static type of a value before implicit conversions; null for the null literal.</summary>
    ITypeSymbol? StaticValueType(IOperation v)
    {
        while (v is IConversionOperation { IsImplicit: true } c) v = c.Operand;
        if (v.ConstantValue is { HasValue: true, Value: null }) return null;
        if (v is IDefaultValueOperation) return null;
        return v.Type == null ? null : TypeOf(v);
    }

    LValue LValueOf(IOperation target)
    {
        while (target is IConversionOperation { Conversion.IsIdentity: true } c) target = c.Operand;
        var t = TypeOf(target);
        switch (target)
        {
            case ILocalReferenceOperation l:
                if (IsCaptured(l.Local)) return new CapturedLV(this, l.Local, t);
                { var v = LocalVar(l.Local); if (!v.InMemory) return new VarLV(this, v); }
                break;
            case IParameterReferenceOperation p:
                if (IsPrimaryParamHere(p.Parameter)) break;
                if (IsCaptured(p.Parameter)) return new CapturedLV(this, p.Parameter, t);
                { var v = _vars[p.Parameter]; if (!v.InMemory) return new VarLV(this, v); }
                break;
            case IPropertyReferenceOperation pr:
                // (a get-only auto-property, virtual or an override too, is assigned in its constructors: its field)
                if (IsDirectAutoProperty(pr.Property, pr.Instance)
                    || pr.Property.SetMethod == null && pr.Property.Parameters.Length == 0 && Driver.AutoPropertyField(pr.Property.OriginalDefinition) != null)
                {
                    var bf = Driver.AutoPropertyField(pr.Property.OriginalDefinition)!;
                    var ft = T.Resolve(pr.Property.Type, Map);
                    var owner = Owner(pr.Property);
                    D.NoteFieldWrite(E, Driver.FieldKey(owner, bf));
                    Arenas.Site? psite = null; WasmFunction? ppin = null;
                    if (Types.IsRef(ft))
                    {
                        if (bf.IsStatic) ppin = D.Arenas.Pin(ft);
                        else if (pr.Instance != null && !TypeOf(pr.Instance).IsValueType) psite = D.Arenas.FieldSite(owner, T.FieldIn(owner, bf));
                    }
                    return new MemLV(this, ft, () =>
                    {
                        if (bf.IsStatic) { D.StaticReads.Add(Driver.StaticKey(owner, bf)); EmitStaticInitGuardForField(owner); E.I32Addr(D.StaticField(owner, bf, ft)); return 0; }
                        return EmitReceiverAddressForField(pr.Instance!) + T.FieldIn(owner, bf).Offset;
                    }, psite, -1, ppin, bf.IsStatic ? Publish.Always : pr.Instance != null && !TypeOf(pr.Instance).IsValueType ? Publish.Container : Publish.Unknown,
                    initKey: InitStoreKey(bf, owner, pr.Instance), staticKey: bf.IsStatic ? Driver.StaticKey(T.Canonical(owner), bf) : null, threadStatic: bf.IsStatic && IsThreadStatic(bf),
                    structField: !bf.IsStatic && owner.IsValueType && Sh != null ? Sharing.StructFieldNode(T.Canonical(owner), bf) : null);
                }
                if (pr.Property.ReturnsByRef) break;
                return new PropLV(this, pr, t);
            case IImplicitIndexerReferenceOperation ii when ii.IndexerSymbol is IPropertySymbol { ReturnsByRef: false, ReturnsByRefReadonly: false }:
                return new ImplicitIndexerLV(this, ii, t);
            case IDeclarationExpressionOperation de: return LValueOf(de.Expression);
            case IFieldReferenceOperation fr when Types.IsRef(t):
                {
                    var field = fr.Field;
                    D.NoteFieldWrite(E, Driver.FieldKey(Owner(field), StorageField(field)));
                    if (field.IsStatic) return new MemLV(this, t, () => EmitAddressBase(target), pin: field.IsReadOnly ? D.Arenas.Pin(t) : null, pub: IsThreadStatic(StorageField(field)) ? Publish.None : Publish.Always,
                        staticKey: Driver.StaticKey(T.Canonical(Owner(field)), StorageField(field)), threadStatic: IsThreadStatic(StorageField(field)));
                    if (fr.Instance != null && !TypeOf(fr.Instance).IsValueType)
                    {
                        var site = D.Arenas.FieldSite(Owner(field), T.FieldIn(Owner(field), StorageField(field)));
                        return new MemLV(this, t, () => EmitAddressBase(target), site, pub: Publish.Container,
                            initKey: StorageField(field) is IFieldSymbol sf ? InitStoreKey(sf, Owner(field), fr.Instance) : null,
                            fresh: site != null && target.Parent != null && StoresIntoFresh(fr.Instance, target.Parent),
                            owned: site != null && OwnedLocal(fr.Instance),
                            iso: site != null && target.Parent is ISimpleAssignmentOperation isa && ReferenceEquals(isa.Target, target) && (D.IsolatedValue(isa.Value) || ChainStore(site, fr, isa)),
                            initNull: InitializerStoreIntoNull(fr, target),
                            lastLocal: site != null ? LastContainerLocal(fr.Instance) : -1);
                    }
                    // a struct's field, wherever the struct is (Sharing.cs: one node for the field)
                    if (fr.Instance != null && Sh != null)
                        return new MemLV(this, t, () => EmitAddressBase(target), elements: true, structField: Sharing.StructFieldNode(T.Canonical(Owner(field)), StorageField(field)));
                    break;
                }
            case IArrayElementReferenceOperation ae when Types.IsRef(t) && TypeOf(ae.ArrayReference) is IArrayTypeSymbol at:
                {
                    int keep = E.AddLocal(ValType.I32);
                    // (a sealed element type has no derived one: the array can only be of this type)
                    var et = T.Canonical(at.ElementType);
                    // (shared generic code: an element type that is a type parameter was taken as any reference type,
                    // as in every instantiation of the family, so that their bodies are one, and the check made. It
                    // is decided on the exact type now: the instantiations for sealed classes are one body without
                    // the check, those for the others one with it. List<T>'s setter is such a store, and the check
                    // (the array's type word read and compared) was 3 us of the churn scene's 86 a frame: 19,000
                    // survivors moved down a list of a sealed class. CS2WASM_NO_SEALED_STORE=1: as before.)
                    bool exact = (et is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Delegate, IsSealed: true } || Types.IsString(et))
                        && (!s_noSealedStore || !OfTypeParameter((ae.ArrayReference.Type as IArrayTypeSymbol)?.ElementType));
                    return new MemLV(this, t, () => EmitArrayElementAddress(ae, keep), D.Arenas.ArraySite(at), keep, pub: Publish.Container, elements: true,
                        covariant: exact || at.Rank != 1 ? null : (IArrayTypeSymbol)T.Canonical(at), owned: OwnedLocal(ae.ArrayReference));
                }
        }
        // a store through an `out` or `ref` parameter: into what the caller passed (Sharing.cs: the parameter's node)
        if (Sh != null && target is IParameterReferenceOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } rp && SharingKey is { } opk
            && SymbolEqualityComparer.Default.Equals(rp.Parameter.ContainingSymbol?.OriginalDefinition, M.OriginalDefinition))
            return new MemLV(this, t, () => EmitAddressBase(target), elements: true, outParam: Sharing.OutNode(opk, rp.Parameter.Ordinal));
        // (a ref made from a pointer: Unsafe.AsRef(void*))
        bool viaPointer = target is IInvocationOperation { TargetMethod: { Name: "AsRef", Parameters: [{ Type: IPointerTypeSymbol }] } };
        // (a struct into a class's field or an array's element: the object it goes into, by type, StoreToAddressFromStack)
        ITypeSymbol? structContainer = !Types.IsStruct(t) ? null : target switch
        {
            IFieldReferenceOperation { Field.IsStatic: false, Instance: { } fi } fr2 when !TypeOf(fi).IsValueType => Owner(fr2.Field),
            IArrayElementReferenceOperation ae2 when TypeOf(ae2.ArrayReference) is IArrayTypeSymbol at2 => at2,
            _ => null,
        };
        IOperation? samePlace = null;
        for (var u = target; u is IInvocationOperation ui && SamePlaceRef(ui) is { } inner; u = inner) samePlace = inner;
        return new MemLV(this, t, () => EmitAddressBase(target), elements: true, viaPointer: viaPointer, structContainer: structContainer, attachPlace: Sh != null ? samePlace : null);
    }

    // =====================================================================
    // conditional / coalesce / type tests
    // =====================================================================
    Own EmitConditionalExpr(IConditionalOperation co)
    {
        var rt = TypeOf(co);
        if (Fold(co.Condition) is bool known)
        {
            var o = EmitValueAs(known ? co.WhenTrue : co.WhenFalse!, rt);
            if (Types.IsRef(rt)) { ToOwned(o, rt); return Own.Owned; }
            return o;
        }
        var vt = Types.WasmType(rt) ?? ValType.I32;
        EmitCondition(co.Condition);
        E.If(vt);
        BeginBranchTemps();
        var a = EmitValueAs(co.WhenTrue, rt);
        int aEnd = E.Fn.Body.Count;
        EndBranchTemps(a, rt);
        E.Else();
        BeginBranchTemps();
        var b = EmitValueAs(co.WhenFalse!, rt);
        if (!Types.IsRef(rt)) { EndBranchTemps(b, rt); E.End(); return Own.None; }
        // section 4.1: the value is borrowed when both branches' are (no count); owned when one is (the other's is counted)
        var joined = JoinOwn(a, b);
        if (joined == Own.Owned)
        {
            ToOwned(b);
            if (a is Own.Stable or Own.Unstable) E.Fn.Body.Insert(aEnd, new Instr { Op = Op.Call, Ref = Rt.Retain, Pos = E.CurrentPos });
            b = Own.Owned;
        }
        EndBranchTemps(b, rt);
        E.End();
        _lastFieldLoad = null;
        return joined;
    }

    /// <summary>The ownership of a value that is one of two: owned if either is (the caller counts the other), not counted
    /// if neither needs counting, else borrowed, from memory that may change if either is.</summary>
    static Own JoinOwn(Own a, Own b)
    {
        if (a == Own.Owned || b == Own.Owned) return Own.Owned;
        if (a == Own.None && b == Own.None) return Own.None;
        // (None beside a borrow: null, a literal, an immortal object: as stable as the other)
        if (a == Own.Unstable || b == Own.Unstable) return Own.Unstable;
        return Own.Stable;
    }

    // A branch's temporaries: released at the branch's end when its value does not depend on them (an owned
    // reference, a number), so a branch that does not run releases nothing; else they wait for the statement's end.
    void BeginBranchTemps() { if (_machine == null) BeginTemps(); }

    void EndBranchTempsValue(ValType keep) { if (_machine == null) EndTemps(keep); }

    void EndBranchTemps(Own o, ITypeSymbol t)
    {
        if (_machine != null) return;
        bool independent = o == Own.Owned || o == Own.None && (Types.IsRef(t) || Types.PrimOf(t) != Prim.None);
        if (independent) { EndTemps(Types.WasmType(t) ?? ValType.I32); return; }
        var list = _temps[^1];
        _temps.RemoveAt(_temps.Count - 1);
        if (list.Count == 0) return;
        if (_temps.Count == 0) BeginTemps();
        _temps[^1].AddRange(list);
    }

    Own EmitCoalesce(ICoalesceOperation c)
    {
        var lt = TypeOf(c.Value);
        var rt = TypeOf(c);
        if (IsNullable(lt)) return EmitNullableCoalesce(c);
        if (!Types.IsRef(lt)) throw new CompileException("?? on this type is not supported", c.Syntax.GetLocation());
        var o = EmitValue(c.Value);
        int t = Scratch(ValType.I32);
        E.Tee(t);
        E.If(ValType.I32);
        E.Get(t);
        int aEnd = E.Fn.Body.Count;
        E.Else();
        BeginBranchTemps();
        var o2 = EmitValueAs(c.WhenNull, rt);
        // section 4.1: borrowed when both sides are; owned when one is (the other is counted then)
        var joined = Types.IsRef(rt) ? JoinOwn(o, o2) : Own.Owned;
        if (joined == Own.Owned)
        {
            if (Types.IsRef(rt)) ToOwned(o2);
            if (o is Own.Stable or Own.Unstable) E.Fn.Body.Insert(aEnd, new Instr { Op = Op.Call, Ref = Rt.Retain, Pos = E.CurrentPos });
            o2 = Own.Owned;
        }
        EndBranchTemps(o2, rt);
        E.End();
        FreeScratch(ValType.I32, t);
        _lastFieldLoad = null;
        return joined;
    }

    Own EmitIsType(IIsTypeOperation it)
    {
        var vt = TypeOf(it.ValueOperand);
        var target = T.Resolve(it.TypeOperand, Map);
        var o = EmitValue(it.ValueOperand);
        if (IsNullable(vt))
        {
            // a nullable is what its value is, when it has one
            var u = Underlying(vt);
            var tu = IsNullable(target) ? Underlying(target) : target;
            if (Types.Key(u) == Types.Key(T.Canonical(tu)) || T.IsSubtype(u, tu)) NullableHas(vt);
            else { Drop(o, vt); E.I32(0); }
        }
        else if (vt.IsValueType)
        {
            Drop(o, vt);
            E.I32(T.IsSubtype(vt, target) || Types.Key(vt) == Types.Key(target) || IsNullable(target) && Types.Key(vt) == Types.Key(T.Canonical(Underlying(target))) ? 1 : 0);
        }
        else
        {
            ToTransient(o, vt);
            EmitTypeTest(target);
        }
        if (it.IsNegated) E.Op(Op.I32Eqz);
        return Own.None;
    }

    /// <summary>Stack: object pointer → i32 (non-null and an instance of target).</summary>
    void EmitTypeTest(ITypeSymbol target)
    {
        if (target.IsValueType)
        {
            int v = Scratch(ValType.I32);
            E.Tee(v);
            E.If(ValType.I32); E.Get(v); E.Call(Rt.IsExactBox(target)); E.Else(); E.I32(0); E.End();
            FreeScratch(ValType.I32, v);
            return;
        }
        E.Call(Rt.IsInst(target));
    }

    Own EmitConditionalAccess(IConditionalAccessOperation ca)
    {
        var it = TypeOf(ca.Operation);
        var rt = ca.Type != null ? TypeOf(ca) : null;
        bool voidResult = rt == null || Types.IsVoid(rt);
        if (!voidResult && rt!.IsValueType && !IsNullable(rt)) throw new CompileException("?. producing a value type is not supported", ca.Syntax.GetLocation());
        var o = EmitValue(ca.Operation);
        int v;
        ITypeSymbol instType = it;
        if (it.IsValueType)
        {
            // a nullable: test HasValue, the instance is the value; a plain value type is never null
            v = E.AddLocal(Types.WasmType(it) ?? ValType.I32);
            E.Set(v);
            if (IsNullable(it))
            {
                instType = Underlying(it);
                int inner = E.AddLocal(Types.WasmType(instType) ?? ValType.I32);
                E.Get(v); NullableHas(it);
                E.If(); E.Get(v); NullableVal(it); E.Set(inner); E.End();
                E.Get(v); NullableHas(it);
                v = inner;
            }
            else E.I32(1);
        }
        else
        {
            o = ToCallSafe(o, it);
            v = E.AddLocal(ValType.I32);
            E.Tee(v);
        }
        _condAccess.Add((v, instType));
        if (voidResult)
        {
            E.If();
            EmitDiscard(ca.WhenNotNull);
            E.End();
            _condAccess.RemoveAt(_condAccess.Count - 1);
            return Own.None;
        }
        E.If(ValType.I32);
        var r = EmitValueAs(ca.WhenNotNull, rt!);
        if (Types.IsRef(rt!)) ToOwned(r, rt);
        E.Else();
        if (IsNullable(rt!)) EmitDefault(rt!); else E.I32(0);
        E.End();
        _condAccess.RemoveAt(_condAccess.Count - 1);
        return Types.IsRef(rt!) ? Own.Owned : Own.None;
    }

    readonly List<(int local, ITypeSymbol type)> _condAccess = [];

    Own EmitConditionalInstance()
    {
        var (l, t) = _condAccess[^1];
        E.Get(l);
        return Types.IsRef(t) ? Own.Stable : Own.None;
    }

    // =====================================================================
    // patterns
    // =====================================================================
    Own EmitIsPattern(IIsPatternOperation ip)
    {
        var vt = TypeOf(ip.Value);
        int v = BindValue(ip.Value, vt);
        EmitPattern(ip.Pattern, v, vt);
        return Own.None;
    }

    /// <summary>Evaluates a value into a local held until the end of the statement.</summary>
    int BindValue(IOperation op, ITypeSymbol vt)
    {
        var o = EmitValue(op);
        if (Types.IsRef(vt)) { ToCallSafe(o, vt); }
        int v = E.AddLocal(Types.WasmType(vt) ?? ValType.I32);
        E.Set(v);
        return v;
    }

    /// <summary>Pushes an i32 telling whether the value in local <paramref name="v"/> (of type vt) matches.</summary>
    void EmitPattern(IPatternOperation pat, int v, ITypeSymbol vt)
    {
        switch (pat)
        {
            case IDiscardPatternOperation: E.I32(1); return;
            case IConstantPatternOperation cp:
                {
                    var cv = cp.Value;
                    if (cv.ConstantValue.HasValue && cv.ConstantValue.Value == null)
                    {
                        // a non-nullable value type (an instantiation's T) is never null
                        if (vt.IsValueType && !IsNullable(vt)) { E.I32(0); return; }
                        E.Get(v);
                        if (IsNullable(vt)) NullableHas(vt);
                        E.Op(Op.I32Eqz); return;
                    }
                    if (IsNullable(vt))
                    {
                        var u0 = Underlying(vt);
                        E.Get(v); NullableHas(vt);
                        E.If(ValType.I32);
                        E.Get(v); NullableVal(vt);
                        EmitEqualsConstant(cv, u0);
                        E.Else(); E.I32(0); E.End();
                        return;
                    }
                    if (Types.IsString(vt))
                    {
                        E.Get(v); EmitValue(cv); E.Call(D.StringEquals.Fn); return;
                    }
                    // a span of chars against a string constant: its characters (the span's address, as held)
                    if (vt is INamedTypeSymbol { Arity: 1 } sv && Types.FullName(sv) is "System.Span`1" or "System.ReadOnlySpan`1"
                        && sv.TypeArguments[0].SpecialType == SpecialType.System_Char && cv.ConstantValue.Value is string)
                    {
                        E.Get(v); EmitValue(cv); E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "SpanEqualsString", 2).Fn); return;
                    }
                    if (!vt.IsValueType)
                    {
                        // object o is 5 / is "x"
                        var ct = TypeOf(cv);
                        if (Types.IsString(ct)) { E.Get(v); E.Call(Rt.IsInst(ct)); E.If(ValType.I32); E.Get(v); EmitValue(cv); E.Call(D.StringEquals.Fn); E.Else(); E.I32(0); E.End(); return; }
                        E.Get(v); EmitTypeTest(ct);
                        E.If(ValType.I32);
                        E.Get(v);
                        if (Types.IsStruct(ct)) { E.I32(BoxDataOffset(T, ct)); E.Op(Op.I32Add); } else E.Load(Types.LoadOp(ct), BoxDataOffset(T, ct));
                        EmitEqualsConstant(cv, ct);
                        E.Else(); E.I32(0); E.End();
                        return;
                    }
                    E.Get(v);
                    EmitEqualsConstant(cv, vt);
                    return;
                }
            case IRelationalPatternOperation rp when IsNullable(vt):
                {
                    // T?: has a value and the value compares
                    var u = Underlying(vt);
                    E.Get(v); NullableHas(vt);
                    E.If(ValType.I32);
                    if (IsDecimal(u)) EmitDecimalRelation(rp, () => { E.Get(v); NullableVal(vt); });
                    else
                    {
                        E.Get(v); NullableVal(vt);
                        var ro = EmitValue(rp.Value);
                        ConvertTop(TypeOf(rp.Value), u, ref ro);
                        EmitCompare(rp.OperatorKind, u);
                    }
                    E.Else(); E.I32(0); E.End();
                    return;
                }
            case IRelationalPatternOperation rp:
                {
                    if (!vt.IsValueType)
                    {
                        var ct = TypeOf(rp.Value);
                        E.Get(v); EmitTypeTest(ct);
                        E.If(ValType.I32);
                        E.Get(v); E.Load(Types.LoadOp(ct), BoxDataOffset(T, ct));
                        EmitValue(rp.Value); EmitCompare(rp.OperatorKind, ct);
                        E.Else(); E.I32(0); E.End();
                        return;
                    }
                    // (a decimal is compared by its operators: it is no number of the machine's)
                    if (IsDecimal(vt)) { EmitDecimalRelation(rp, () => E.Get(v)); return; }
                    E.Get(v);
                    var o = EmitValue(rp.Value);
                    ConvertTop(TypeOf(rp.Value), vt, ref o);
                    EmitCompare(rp.OperatorKind, vt);
                    return;
                }
            case INegatedPatternOperation np: EmitPattern(np.Pattern, v, vt); E.Op(Op.I32Eqz); return;
            case IBinaryPatternOperation bp:
                EmitPattern(bp.LeftPattern, v, vt);
                E.If(ValType.I32);
                if (bp.OperatorKind == BinaryOperatorKind.And)
                {
                    // (the left side may have told the type: `int[] and [var first, ..]` takes the value as an int[])
                    var rit = bp.RightPattern.InputType != null ? T.Resolve(bp.RightPattern.InputType, Map) : vt;
                    if (Types.Key(T.Canonical(rit)) != Types.Key(T.Canonical(vt)) && !IsNullable(vt)) EmitPattern(bp.RightPattern, Narrowed(v, vt, rit), rit);
                    else EmitPattern(bp.RightPattern, v, vt);
                    E.Else(); E.I32(0);
                }
                else { E.I32(1); E.Else(); EmitPattern(bp.RightPattern, v, vt); }
                E.End();
                return;
            case ITypePatternOperation tp:
                EmitTestType(v, vt, T.Resolve(tp.MatchedType, Map));
                return;
            case IDeclarationPatternOperation dp:
                {
                    var mt = dp.MatchedType != null ? T.Resolve(dp.MatchedType, Map) : vt;
                    if (dp.MatchesNull) { BindPatternVariable(dp.DeclaredSymbol, v, vt, mt); E.I32(1); return; }
                    EmitTestType(v, vt, mt);
                    if (dp.DeclaredSymbol == null) return;
                    E.If(ValType.I32);
                    BindPatternVariable(dp.DeclaredSymbol, v, vt, mt);
                    E.I32(1);
                    E.Else(); E.I32(0); E.End();
                    return;
                }
            case IRecursivePatternOperation rp:
                {
                    var mt = rp.MatchedType != null ? T.Resolve(rp.MatchedType, Map) : vt;
                    // `o is (a, b)` on a value that is no tuple and has no Deconstruct: by ITuple, its Length and items
                    var ituple = rp.DeconstructionSubpatterns.Length > 0 && rp.DeconstructSymbol is null or ITypeSymbol && !IsValueTuple(mt)
                        ? D.LibType("System.Runtime.CompilerServices.ITuple") : null;
                    if (ituple != null)
                    {
                        EmitTestType(v, vt, mt);
                        E.If(ValType.I32);
                        if (rp.DeclaredSymbol != null) BindPatternVariable(rp.DeclaredSymbol, v, vt, mt);
                        int mv = Narrowed(v, vt, mt);
                        EmitTestType(mv, mt, ituple);
                        E.If(ValType.I32);
                        int tv = Narrowed(mv, mt, ituple);
                        _patternInputs.Add((mv, mt));
                        var length = ituple.GetMembers("Length").OfType<IPropertySymbol>().First().GetMethod!;
                        var item = ituple.GetMembers().OfType<IPropertySymbol>().First(p => p.IsIndexer).GetMethod!;
                        var obj = D.Main.GetSpecialType(SpecialType.System_Object);
                        CallWith(length, true, () => E.Get(tv), () => { }, null, rp.Syntax);
                        E.I32(rp.DeconstructionSubpatterns.Length); E.Op(Op.I32Eq);
                        E.If(ValType.I32);
                        var items = new List<(int local, ITypeSymbol type)>();
                        for (int k = 0; k < rp.DeconstructionSubpatterns.Length; k++)
                        {
                            int off = TempValue(obj);
                            int kk = k;
                            var own = CallWith(item, true, () => E.Get(tv), () => E.I32(kk), null, rp.Syntax);
                            ToOwned(own);
                            int l = E.AddLocal(ValType.I32);
                            E.Set(l);
                            E.Get(_fb); E.Load(Op.I32Load, off); E.Call(Rt.Release);
                            E.Get(_fb); E.Get(l); E.Store(Op.I32Store, off);
                            items.Add((l, obj));
                        }
                        EmitPartPatterns(rp.DeconstructionSubpatterns, items, 0, () => EmitSubpatterns(rp.PropertySubpatterns, 0));
                        E.Else(); E.I32(0); E.End();
                        _patternInputs.RemoveAt(_patternInputs.Count - 1);
                        E.Else(); E.I32(0); E.End();
                        E.Else(); E.I32(0); E.End();
                        return;
                    }
                    EmitTestType(v, vt, mt);
                    E.If(ValType.I32);
                    // narrowed value
                    int nv = Narrowed(v, vt, mt);
                    if (rp.DeclaredSymbol != null) BindPatternVariable(rp.DeclaredSymbol, v, vt, mt);
                    _patternInputs.Add((nv, mt));
                    if (rp.DeconstructionSubpatterns.Length > 0)
                    {
                        var parts = PositionalParts(rp, nv, mt);
                        EmitPartPatterns(rp.DeconstructionSubpatterns, parts, 0, () => EmitSubpatterns(rp.PropertySubpatterns, 0));
                    }
                    else EmitSubpatterns(rp.PropertySubpatterns, 0);
                    _patternInputs.RemoveAt(_patternInputs.Count - 1);
                    E.Else(); E.I32(0); E.End();
                    return;
                }
            case IListPatternOperation lp:
                {
                    EmitListPattern(lp, v, vt);
                    return;
                }
        }
        throw new CompileException("unsupported pattern " + pat.Kind, pat.Syntax.GetLocation());
    }

    /// <summary>Values for a positional pattern: tuple fields, or the out parameters of Deconstruct.</summary>
    List<(int local, ITypeSymbol type)> PositionalParts(IRecursivePatternOperation rp, int nv, ITypeSymbol mt)
    {
        var parts = new List<(int, ITypeSymbol)>();
        if (rp.DeconstructSymbol is IMethodSymbol dm)
        {
            var m = SubstMethod(dm);
            var temps = new List<(int off, ITypeSymbol t)>();
            foreach (var p in m.Parameters.Skip(m.IsStatic ? 1 : 0))
            {
                var pt = T.Canonical(p.Type);
                temps.Add((Types.IsStruct(pt) ? TempStruct(pt) : TempValue(pt), pt));
            }
            CallWith(m, !mt.IsValueType && IsVirtualDispatch(m), m.IsStatic ? null : () => E.Get(nv), () =>
            {
                if (m.IsStatic) E.Get(nv);
                foreach (var (off, _) in temps) { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); }
            }, null, rp.Syntax);
            foreach (var (off, pt) in temps)
            {
                int l = E.AddLocal(Types.WasmType(pt) ?? ValType.I32);
                E.Get(_fb);
                if (Types.IsStruct(pt)) { E.I32(off); E.Op(Op.I32Add); } else E.Load(Types.LoadOp(pt), off);
                E.Set(l);
                parts.Add((l, pt));
            }
            return parts;
        }
        if (IsValueTuple(mt))
        {
            foreach (var slot in TupleFields(mt))
            {

                int l = E.AddLocal(Types.WasmType(slot.Type) ?? ValType.I32);
                E.Get(nv);
                if (Types.IsStruct(slot.Type)) { if (slot.Offset != 0) { E.I32(slot.Offset); E.Op(Op.I32Add); } } else E.Load(Types.LoadOp(slot.Type), slot.Offset);
                E.Set(l);
                parts.Add((l, slot.Type));
            }
            return parts;
        }
        throw new CompileException("positional pattern on " + mt + " (ITuple) is not supported yet", rp.Syntax.GetLocation());
    }

    void EmitPartPatterns(System.Collections.Immutable.ImmutableArray<IPatternOperation> pats, List<(int local, ITypeSymbol type)> parts, int i, Action rest)
    {
        if (i >= pats.Length) { rest(); return; }
        EmitPattern(pats[i], parts[i].local, parts[i].type);
        E.If(ValType.I32);
        EmitPartPatterns(pats, parts, i + 1, rest);
        E.Else(); E.I32(0); E.End();
    }

    /// <summary>[a, b, .. rest, z]: length test, element patterns from the front and the back, slice pattern.</summary>
    void EmitListPattern(IListPatternOperation lp, int v, ITypeSymbol vt)
    {
        var pats = lp.Patterns;
        int slice = -1;
        for (int i = 0; i < pats.Length; i++) if (pats[i] is ISlicePatternOperation) slice = i;
        int fixedCount = pats.Length - (slice >= 0 ? 1 : 0);
        var arr = vt as IArrayTypeSymbol;
        // (the indexer's type in a generic method or type is the type parameter's: resolved through the map)
        var et = arr != null ? T.Canonical(arr.ElementType) : T.Canonical(T.Resolve(((IPropertySymbol)lp.IndexerSymbol!).Type, Map));
        // null input never matches
        if (!vt.IsValueType) { E.Get(v); E.Op(Op.I32Eqz); E.If(ValType.I32); E.I32(0); E.Else(); }
        int len = E.AddLocal(ValType.I32);
        if (arr != null) { E.Get(v); E.Load(Op.I32Load, 12); }
        else
        {
            var lenProp = (IPropertySymbol)lp.LengthSymbol!;
            var g = SubstMethod(lenProp.GetMethod!);
            CallWith(g, !vt.IsValueType && IsVirtualDispatch(g), () => E.Get(v), () => { }, null, lp.Syntax);
        }
        E.Set(len);
        E.Get(len); E.I32(fixedCount); E.Op(slice >= 0 ? Op.I32GeS : Op.I32Eq);
        E.If(ValType.I32);
        void PushIndex(int i)
        {
            if (slice < 0 || i < slice) E.I32(i);
            else { E.Get(len); E.I32(pats.Length - i); E.Op(Op.I32Sub); }
        }
        int Element(int i)
        {
            int l = E.AddLocal(Types.WasmType(et) ?? ValType.I32);
            if (arr != null)
            {
                int size = T.StorageOf(et).size;
                E.Get(v); PushIndex(i);
                if (size != 1) { E.I32(size); E.Op(Op.I32Mul); }
                E.Op(Op.I32Add);
                if (Types.IsStruct(et)) { E.I32(Types.ArrayData); E.Op(Op.I32Add); } else E.Load(Types.LoadOp(et), Types.ArrayData);
            }
            else
            {
                var idx = (IPropertySymbol)lp.IndexerSymbol!;
                var g = SubstMethod(idx.GetMethod!);
                var o = CallWith(g, !vt.IsValueType && IsVirtualDispatch(g), () => E.Get(v), () => PushIndex(i), null, lp.Syntax);
                if (Types.IsRef(et) && o == Own.Owned) { int tl = TempRef(et); E.Set(tl); return tl; }
            }
            E.Set(l);
            return l;
        }
        void Next(int i)
        {
            if (i >= pats.Length) { E.I32(1); return; }
            if (pats[i] is ISlicePatternOperation sp)
            {
                if (sp.Pattern == null) { Next(i + 1); return; }
                // the slice value: a sub-array, or Slice(start, length)
                ITypeSymbol sliceT;
                if (arr != null)
                {
                    sliceT = arr;
                    var sub = D.LibType("Cs2Wasm.Runtime.Slices").GetMembers("SubArray").OfType<IMethodSymbol>().First().Construct(et);
                    CallWith(sub, false, null, () => { E.Get(v); E.I32(slice); E.Get(len); E.I32(fixedCount); E.Op(Op.I32Sub); }, null, sp.Syntax);
                }
                else
                {
                    var sm = (IMethodSymbol)sp.SliceSymbol!;
                    var m = SubstMethod(sm);
                    sliceT = T.Canonical(m.ReturnType);
                    CallWith(m, false, () => E.Get(v), () => { E.I32(slice); E.Get(len); E.I32(fixedCount); E.Op(Op.I32Sub); }, null, sp.Syntax);
                }
                int sl = Types.IsRef(sliceT) ? TempRef(sliceT) : E.AddLocal(ValType.I32);
                E.Set(sl);
                EmitPattern(sp.Pattern, sl, sliceT);
                E.If(ValType.I32); Next(i + 1); E.Else(); E.I32(0); E.End();
                return;
            }
            int el = Element(i);
            EmitPattern(pats[i], el, et);
            E.If(ValType.I32); Next(i + 1); E.Else(); E.I32(0); E.End();
        }
        Next(0);
        E.Else(); E.I32(0); E.End();
        if (!vt.IsValueType) E.End();
    }

    void EmitSubpatterns(System.Collections.Immutable.ImmutableArray<IPropertySubpatternOperation> subs, int i)
    {
        if (i >= subs.Length) { E.I32(1); return; }
        var sp = subs[i];
        var mt = TypeOf(sp.Member);
        int mv = BindValue(sp.Member, mt);
        EmitPattern(sp.Pattern, mv, mt);
        E.If(ValType.I32);
        EmitSubpatterns(subs, i + 1);
        E.Else(); E.I32(0); E.End();
    }

    void EmitTestType(int v, ITypeSymbol vt, ITypeSymbol mt)
    {
        if (IsNullable(vt) && !IsNullable(mt))
        {
            E.Get(v); NullableHas(vt);
            if (!(Types.Key(Underlying(vt)) == Types.Key(T.Canonical(mt)) || T.IsSubtype(Underlying(vt), mt))) { E.Op(Op.Drop); E.I32(0); }
            return;
        }
        if (vt.IsValueType)
        {
            E.I32(Types.Key(T.Canonical(vt)) == Types.Key(T.Canonical(mt)) || T.IsSubtype(vt, mt) ? 1 : 0);
            return;
        }
        if (!mt.IsValueType && T.IsSubtype(vt, mt)) { E.Get(v); E.I32(0); E.Op(Op.I32Ne); return; }
        E.Get(v); EmitTypeTest(mt);
    }

    int Narrowed(int v, ITypeSymbol vt, ITypeSymbol mt)
    {
        if (IsNullable(vt) && !IsNullable(mt))
        {
            var u = Underlying(vt);
            int nv0 = E.AddLocal(Types.WasmType(u) ?? ValType.I32);
            E.Get(v); NullableVal(vt); E.Set(nv0);
            if (Types.IsRef(mt) && !Types.IsRef(u)) { var bo = Own.None; E.Get(nv0); EmitBox(u, bo); int bl = TempRef(mt); E.Set(bl); return bl; }
            return nv0;
        }
        if (!vt.IsValueType && mt.IsValueType)
        {
            int nv = E.AddLocal(Types.WasmType(mt) ?? ValType.I32);
            E.Get(v);
            int off = BoxDataOffset(T, mt);
            if (Types.IsStruct(mt)) { E.I32(off); E.Op(Op.I32Add); } else E.Load(Types.LoadOp(mt), off);
            E.Set(nv);
            return nv;
        }
        // a value seen as an interface or a class it converts to (`rest is ITuple t` in generic code over a struct):
        // its box
        if (vt.IsValueType && Types.IsRef(mt)) { E.Get(v); EmitBox(vt, Own.None); int bl = TempRef(mt); E.Set(bl); return bl; }
        return v;
    }

    void BindPatternVariable(ISymbol? sym, int v, ITypeSymbol vt, ITypeSymbol mt)
    {
        if (sym is not ILocalSymbol l) return;
        int nv = Narrowed(v, vt, mt);
        EmitStoreToVarFrom(l, mt, () =>
        {
            E.Get(nv);
            return Types.IsRef(mt) ? Own.Stable : Own.None;
        });
    }

    Own EmitSwitchExpression(ISwitchExpressionOperation se)
    {
        var vt = TypeOf(se.Value);
        var rt = TypeOf(se);
        int v = BindValue(se.Value, vt);
        var wt = Types.WasmType(rt) ?? ValType.I32;
        var done = E.Block(wt);
        bool isRef = Types.IsRef(rt);
        // section 4.1, as a conditional's: the value is borrowed when every arm's is (an arm's temporaries are the
        // statement's, so what it borrows from lives as long); owned when one arm's is, the borrowed arms' then counted
        var arms = new List<(Own o, int end)>();
        foreach (var arm in se.Arms)
        {
            EmitPattern(arm.Pattern, v, vt);
            if (arm.Guard != null) { E.If(ValType.I32); EmitCondition(arm.Guard); E.Else(); E.I32(0); E.End(); }
            E.If();
            var o = EmitValueAs(arm.Value, rt);
            if (isRef && (s_noSwitchBorrows || D.ThreadMode)) { ToOwned(o, rt); o = Own.Owned; }
            if (isRef) arms.Add((o, E.Fn.Body.Count));
            E.Br(done);
            E.End();
        }
        E.Call(Rt.ThrowSwitch);
        E.Op(Op.Unreachable);
        E.End();
        if (!isRef) return Own.None;
        var joined = Own.None;
        foreach (var (o, _) in arms) joined = JoinOwn(joined, o);
        if (joined == Own.Owned)
            for (int i = arms.Count - 1; i >= 0; i--)
                if (arms[i].o is Own.Stable or Own.Unstable) E.Fn.Body.Insert(arms[i].end, new Instr { Op = Op.Call, Ref = Rt.RetainOf(rt), Pos = E.CurrentPos });
        _lastFieldLoad = null;
        return joined;
    }

    /// <summary>CS2WASM_NO_SWITCH_BORROWS=1: every reference arm of a switch expression counted, as before.</summary>
    static readonly bool s_noSwitchBorrows = Environment.GetEnvironmentVariable("CS2WASM_NO_SWITCH_BORROWS") == "1";

    // =====================================================================
    // pointers (no public IOperation kind: read the syntax)
    // =====================================================================
    Own EmitPointerOp(IOperation op)
    {
        if (op.Syntax is StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax) return EmitStackAlloc(op);
        var t = op.Type != null ? TypeOf(op) : throw new CompileException($"unsupported construct '{op.Syntax}'", op.Syntax.GetLocation());
        int off = EmitPointerAddress(op);
        if (Types.IsStruct(t)) { if (off != 0) { E.I32(off); E.Op(Op.I32Add); } return Own.None; }
        E.Load(Types.LoadOp(t), off);
        return Types.IsRef(t) ? Own.Unstable : Own.None;
    }

    /// <summary>stackalloc: into a span (array-backed, so it may outlive nothing it should not), or raw shadow-stack memory for a pointer.</summary>
    Own EmitStackAlloc(IOperation op)
    {
        var model = D.ModelFor(op.Syntax.SyntaxTree);
        InitializerExpressionSyntax? init = op.Syntax switch
        {
            StackAllocArrayCreationExpressionSyntax s => s.Initializer,
            ImplicitStackAllocArrayCreationExpressionSyntax i => i.Initializer,
            _ => null,
        };
        ExpressionSyntax? sizeExpr = op.Syntax is StackAllocArrayCreationExpressionSyntax sa && sa.Type is ArrayTypeSyntax at2 && at2.RankSpecifiers[0].Sizes[0] is not OmittedArraySizeExpressionSyntax ? at2.RankSpecifiers[0].Sizes[0] : null;
        var t = op.Type != null ? TypeOf(op) : throw new CompileException("stackalloc without a type", op.Syntax.GetLocation());
        ITypeSymbol et;
        bool span = t is INamedTypeSymbol { Arity: 1 } nt && Types.FullName(nt) is "System.Span`1" or "System.ReadOnlySpan`1";
        if (span) et = T.Canonical(((INamedTypeSymbol)t).TypeArguments[0]);
        else if (t is IPointerTypeSymbol pt) et = T.Resolve(pt.PointedAtType, Map);
        else throw new CompileException("unsupported stackalloc target " + t, op.Syntax.GetLocation());
        int size = T.StorageOf(et).size;
        var elems = init?.Expressions.Select(e => model.GetOperation(e)!).ToList() ?? [];
        void PushCount()
        {
            if (sizeExpr != null) EmitIndexLength(model.GetOperation(sizeExpr)!);
            else E.I32(elems.Count);
        }
        var spanPtrCtor = span ? ((INamedTypeSymbol)T.Canonical(t)).InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 2 && c.Parameters[0].Type is IPointerTypeSymbol) : null;
        if (span && spanPtrCtor == null) throw new CompileException("stackalloc into " + t + ": no (pointer, length) constructor", op.Syntax.GetLocation());
        // carve zeroed memory off the shadow stack; the function's exit restores the stack pointer (a span over it
        // cannot outlive the method: C#'s ref safety rules)
        _usesFrame = true;
        int bytes = Scratch(ValType.I32);
        int count = E.AddLocal(ValType.I32);
        PushCount(); E.Tee(count); E.I32(size); E.Op(Op.I32Mul); E.I32(7); E.Op(Op.I32Add); E.I32(-8); E.Op(Op.I32And); E.Set(bytes);
        // (more than the stack has left: a trap, as the stack's overflow; the size is the program's own number)
        E.Get(bytes); E.GGet(D.StackPointer); E.GGet(D.StackLo!); E.Op(Op.I32Sub);
        E.Op(Op.I32GtU);
        E.If(); E.Op(Op.Unreachable); E.End();
        E.GGet(D.StackPointer); E.Get(bytes); E.Op(Op.I32Sub);
        int p = E.AddLocal(ValType.I32); E.Tee(p); E.GSet(D.StackPointer);
        E.Get(p); E.I32(0); E.Get(bytes); E.Op(Op.MemoryFill);
        FreeScratch(ValType.I32, bytes);
        if (span)
        {
            for (int i = 0; i < elems.Count; i++)
            {
                E.Get(p);
                EmitValueAs(elems[i], et);
                E.Store(Types.StoreOp(et), i * size);
            }
            // Span<T>(void* pointer, int length)
            int off = TempStruct(t);
            CallWith(spanPtrCtor!, false, () => { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); }, () => { E.Get(p); E.Get(count); }, null, op.Syntax);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
            return Own.None;
        }
        for (int i = 0; i < elems.Count; i++)
        {
            var wt = Types.WasmType(et)!.Value;
            E.Get(p);
            EmitValueAs(elems[i], et);
            E.Store(Types.StoreOp(et), i * size);
        }
        E.Get(p);
        return Own.None;
    }

    int EmitPointerAddress(IOperation op)
    {
        var syn = op.Syntax;
        var children = op.ChildOperations.ToList();
        switch (syn)
        {
            case PrefixUnaryExpressionSyntax pu when pu.IsKind(SyntaxKind.PointerIndirectionExpression):
                EmitValue(children[0]); return 0;
            // p->f: the field's instance is the implicit *p, whose syntax is p
            case ExpressionSyntax e when e.Parent is MemberAccessExpressionSyntax ma && ma.IsKind(SyntaxKind.PointerMemberAccessExpression) && ma.Expression == e && children.Count == 1:
                EmitValue(children[0]); return 0;
            case ElementAccessExpressionSyntax when children.Count == 2 && children[0].Type is IPointerTypeSymbol pt:
                {
                    EmitValue(children[0]);
                    var ip = Types.PrimOf(TypeOf(children[1]));
                    EmitValue(children[1]); if (Types.Is64(ip)) E.Op(Op.I32WrapI64);
                    int es = T.StorageOf(T.Resolve(pt.PointedAtType, Map)).size;
                    if (es != 1) { E.I32(es); E.Op(Op.I32Mul); }
                    E.Op(Op.I32Add);
                    return 0;
                }
        }
        throw new CompileException($"unsupported construct '{syn}'", syn.GetLocation());
    }
}
