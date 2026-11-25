#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Types;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

// Delegates made at run time (IL front end; the old compiler's Compiler/DynamicDelegates.cs does the same).
//
// Rt.ObjectArrayDelegate(type id, handler): a delegate of the type whose target is the handler (a Func<object[], object>)
// and whose function is the type's object-array thunk: it boxes the arguments into an object[], calls the handler, and
// converts the result back. Used by Delegate.CreateDelegate (Internal.Runtime.Augments.DynamicDelegateAugments).
//
// Delegate.DynamicInvokeImpl (Runtime/System/Delegate.Mono.cs): the count is checked, then Rt.DynamicArguments(d, args)
// makes the array of the arguments as the parameters take them (the argument thunk: an instance check for a reference,
// the value or a widened primitive or the default for a value type, ArgumentException otherwise), then
// Rt.DynamicInvokeRaw(d, array) invokes d (the dynamic thunk: the arguments unboxed, the result boxed). Rt.DelegateArity(d)
// is the count of d's type's parameters, -1 when the program has no thunk for the type.
//
// The thunks are written for each delegate type the program makes (or, for the object-array form, names: every type
// with a type id) whose Invoke has no by-ref, pointer or ref-like parameter. They are made in the lowering's fixpoint
// (RequestDelegateThunks); the dispatch functions are written once the types are final (EmitDelegateSupport).
public sealed partial class IlCompiler
{
    bool _objectArrayUsed, _dynamicUsed;
    WasmFunction? _objectArrayDelegateFn, _delegateArityFn, _dynamicInvokeFn, _dynamicArgumentsFn;
    readonly Dictionary<TypeDesc, WasmFunction> _objectArrayThunks = new(), _dynamicThunks = new(), _argumentThunks = new();

    internal WasmFunction ObjectArrayDelegateFn => _objectArrayDelegateFn ??= Module.AddFunction("rt.object_array_delegate", new FuncType([ValType.I32, ValType.I32], [ValType.I32]));
    internal WasmFunction DelegateArityFn => _delegateArityFn ??= Module.AddFunction("rt.delegate_arity", new FuncType([ValType.I32], [ValType.I32]));
    internal WasmFunction DynamicInvokeFn => _dynamicInvokeFn ??= Module.AddFunction("rt.dynamic_invoke", new FuncType([ValType.I32, ValType.I32], [ValType.I32]));
    internal WasmFunction DynamicArgumentsFn => _dynamicArgumentsFn ??= Module.AddFunction("rt.dynamic_arguments", new FuncType([ValType.I32, ValType.I32], [ValType.I32]));

    /// <summary>The thunks the program's delegate types need, made in the lowering's fixpoint; true if any was made.</summary>
    internal bool RequestDelegateThunks()
    {
        if (!_objectArrayUsed && !_dynamicUsed) return false;
        bool any = false;
        foreach (var t in _types.ToList())
        {
            if (!ThunkableDelegate(t)) continue;
            if (_objectArrayUsed && !_objectArrayThunks.ContainsKey(t))
            {
                // (made by the dispatch at run time: its destructor and its dispatch cases are needed)
                _objectArrayThunks[t] = ObjectArrayThunk(t);
                Constructed(t);
                any = true;
            }
            if (_dynamicUsed && !_dynamicThunks.ContainsKey(t) && _constructed.Contains(t))
            {
                _argumentThunks[t] = ArgumentsThunk(t);
                _dynamicThunks[t] = DynamicThunk(t);
                any = true;
            }
        }
        return any;
    }

    /// <summary>The dispatch functions, written once the thunks are all made (GenerateLate).</summary>
    void EmitDelegateSupport()
    {
        if (_objectArrayDelegateFn != null)
        {
            var e = new Emitter(_objectArrayDelegateFn);
            foreach (var (dt, th) in _objectArrayThunks)
            {
                var tOff = IlContext.FieldOffset(DelegateFieldOf(dt, "_target"));
                var fOff = IlContext.FieldOffset(DelegateFieldOf(dt, "_fn"));
                e.Get(0); e.Tid(TypeId(dt)); e.Op(WOp.I32Eq);
                e.If();
                int obj = e.AddLocal(ValType.I32);
                e.I32(IlContext.InstanceSize((DefType)dt)); e.Tid(TypeId(dt)); e.Call(Rt.AllocObject); e.Set(obj);
                e.Get(obj); e.Get(1); e.Call(Rt.Retain); e.Store(WOp.I32Store, tOff);
                e.Get(obj); e.I32Fn(Module, th); e.Store(WOp.I32Store, fOff);
                e.Get(obj); e.Op(WOp.Return);
                e.End();
            }
            e.I32(0);
        }
        if (_delegateArityFn != null)
        {
            var e = new Emitter(_delegateArityFn);
            foreach (var (dt, _) in _dynamicThunks)
            {
                e.Get(0); e.Load(WOp.I32Load, 4); e.Tid(TypeId(dt)); e.Op(WOp.I32Eq);
                e.If(); e.I32(DelegateInvokeOf(dt).Signature.Length); e.Op(WOp.Return); e.End();
            }
            e.I32(-1);
        }
        if (_dynamicArgumentsFn != null)
        {
            var e = new Emitter(_dynamicArgumentsFn);
            foreach (var (dt, th) in _argumentThunks)
            {
                e.Get(0); e.Load(WOp.I32Load, 4); e.Tid(TypeId(dt)); e.Op(WOp.I32Eq);
                e.If(); e.Get(0); e.Get(1); e.Call(th); e.Op(WOp.Return); e.End();
            }
            e.Op(WOp.Unreachable);
        }
        if (_dynamicInvokeFn != null)
        {
            var e = new Emitter(_dynamicInvokeFn);
            foreach (var (dt, th) in _dynamicThunks)
            {
                e.Get(0); e.Load(WOp.I32Load, 4); e.Tid(TypeId(dt)); e.Op(WOp.I32Eq);
                e.If(); e.Get(0); e.Get(1); e.Call(th); e.Op(WOp.Return); e.End();
            }
            e.Op(WOp.Unreachable);
        }
    }

    /// <summary>A delegate type the thunks serve: a closed delegate type whose Invoke takes and returns no by-ref, pointer
    /// or ref-like value.</summary>
    static bool ThunkableDelegate(TypeDesc t)
    {
        if (!t.IsDelegate || t.IsCanonicalSubtype(CanonicalFormKind.Any) || t.ContainsSignatureVariables()) return false;
        var sig = DelegateInvokeOf(t).Signature;
        foreach (var p in sig) if (!Plain(p)) return false;
        return Plain(sig.ReturnType) || sig.ReturnType.IsVoid;
    }

    static bool Plain(TypeDesc p) => !(p.IsByRef || p.IsPointer || p.IsFunctionPointer || p is MetadataType { IsByRefLike: true });

    static MethodDesc DelegateInvokeOf(TypeDesc t) => t.GetMethods().First(IsDelegateInvoke);

    /// <summary>Func&lt;object[], object&gt;.Invoke: the handler's call.</summary>
    MethodDesc HandlerInvoke()
    {
        var objT = _ctx.GetWellKnownType(Internal.TypeSystem.WellKnownType.Object);
        var func = (MetadataType)_ctx.SystemModule.GetType("System"u8, "Func`2"u8);
        var ft = func.MakeInstantiatedType(objT.MakeArrayType(), objT);
        return ft.GetMethods().First(IsDelegateInvoke);
    }

    /// <summary>The object-array thunk of a delegate type: (handler, args...) -> result, the arguments boxed into an
    /// object[] that the handler is called with, the result unboxed (its box released). Arguments are lent, the result
    /// owned; a struct result is written to the caller's buffer, the last parameter.</summary>
    WasmFunction ObjectArrayThunk(TypeDesc dt)
    {
        var inv = DelegateInvokeOf(dt);
        var sig = inv.Signature;
        var f = Module.AddFunction("delegate object-array thunk " + dt, Sig(inv));
        f.TableRoot = true;
        var e = new Emitter(f);
        var arrT = _ctx.GetWellKnownType(Internal.TypeSystem.WellKnownType.Object).MakeArrayType();
        Constructed(arrT);
        var handler = DelegateInvoke(HandlerInvoke());
        int n = sig.Length;
        int arr = e.AddLocal(ValType.I32);
        e.I32(n); e.I32(4); e.Tid(TypeId(arrT)); e.Call(Rt.AllocArray); e.Set(arr);
        for (int i = 0; i < n; i++)
        {
            var pt = sig[i];
            e.Get(arr);
            if (Kinds.Of(pt) == VK.Ref) { e.Get(i + 1); e.Call(Rt.Retain); }
            else
            {
                // (a value is boxed: its box is the array's)
                Constructed(pt);
                int box = e.AddLocal(ValType.I32);
                e.I32(IlContext.ObjectHeader + Math.Max(4, Kinds.SizeOf(pt))); e.Tid(TypeId(pt)); e.Call(Rt.AllocObject); e.Set(box);
                e.Get(box); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add);
                if (Kinds.Of(pt) == VK.Struct)
                {
                    e.Get(i + 1); e.I32(SizeOfStructOf(pt)); e.Emit(WOp.MemoryCopy);
                    if (Kinds.Counted(pt)) { e.Get(box); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); e.Call(Rt.RetainStruct(pt)); }
                }
                else { e.Get(i + 1); StoreTyped(e, pt, 0); }
                e.Get(box);
            }
            e.Store(WOp.I32Store, ElemDataOffset + 4 * i);
        }
        // (the handler is called with the array, which it borrows; the array is released after: its elements with it)
        int result = e.AddLocal(ValType.I32);
        e.Get(0); e.Get(arr); e.Call(handler); e.Set(result);
        e.Get(arr); e.Call(Rt.Release);
        var rt = sig.ReturnType;
        if (rt.IsVoid) { e.Get(result); e.Call(Rt.Release); }
        else if (Kinds.Of(rt) == VK.Ref) e.Get(result);
        else if (Kinds.Of(rt) == VK.Struct)
        {
            // (the box's copy, with counts of its own; the box goes)
            e.Get(n + 1); e.Get(result); e.Call(UnboxFn(rt)); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); e.I32(SizeOfStructOf(rt)); e.Emit(WOp.MemoryCopy);
            if (Kinds.Counted(rt)) { e.Get(n + 1); e.Call(Rt.RetainStruct(rt)); }
            e.Get(result); e.Call(Rt.Release);
        }
        else
        {
            int v = e.AddLocal(FunctionLowering.ValOf(Kinds.Of(rt)));
            e.Get(result); e.Call(UnboxFn(rt)); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); LoadTyped(e, rt, 0); e.Set(v);
            e.Get(result); e.Call(Rt.Release);
            e.Get(v);
        }
        e.Op(WOp.Return);
        return f;
    }

    /// <summary>The arguments as the parameters take them: (delegate, args: object[]) -> a new object[] (owned) whose
    /// elements are of the parameters' types (a reference: an instance of the parameter's type; a value: the argument, a
    /// widened primitive, or the default value of the type for null). ArgumentException otherwise, from the checks in
    /// Delegate.DynamicValueArgument and Delegate.DynamicArgumentFailed.</summary>
    WasmFunction ArgumentsThunk(TypeDesc dt)
    {
        var sig = DelegateInvokeOf(dt).Signature;
        var f = Module.AddFunction("delegate dynamic arguments " + dt, new FuncType([ValType.I32, ValType.I32], [ValType.I32]));
        var e = new Emitter(f);
        var arrT = _ctx.GetWellKnownType(Internal.TypeSystem.WellKnownType.Object).MakeArrayType();
        Constructed(arrT);
        var valueArg = FunctionFor(CoreMethod("System", "Delegate", "DynamicValueArgument"));
        var refArg = FunctionFor(CoreMethod("System", "Delegate", "DynamicArgumentFailed"));
        int n = sig.Length;
        int res = e.AddLocal(ValType.I32);
        e.I32(n); e.I32(4); e.Tid(TypeId(arrT)); e.Call(Rt.AllocArray); e.Set(res);
        for (int i = 0; i < n; i++)
        {
            var pt = sig[i];
            int o = e.AddLocal(ValType.I32);
            e.Get(1); e.Load(WOp.I32Load, ElemDataOffset + 4 * i); e.Set(o);
            e.Get(res);
            if (Kinds.Of(pt) == VK.Ref)
            {
                // (an instance check when the argument is not null; the array gets its own count)
                e.Get(o); e.If();
                e.Get(o); e.Call(IsInstFn(pt)); e.Op(WOp.I32Eqz);
                e.If(); e.Get(o); e.I32Addr(StringLiteral(TypeName(pt))); e.Call(refArg); e.End();
                e.End();
                e.Get(o); e.Call(Rt.Retain);
            }
            else
            {
                // (the value or the widened primitive, owned; a null argument: a new box of the default value)
                Constructed(pt);
                int c = e.AddLocal(ValType.I32);
                e.Get(o); e.Tid(TypeId(pt)); e.I32(TypeCodeOf(pt)); e.I32Addr(StringLiteral(TypeName(pt))); e.Call(valueArg); e.Set(c);
                e.Get(c); e.Op(WOp.I32Eqz);
                e.If(); e.I32(IlContext.ObjectHeader + Math.Max(4, Kinds.SizeOf(pt))); e.Tid(TypeId(pt)); e.Call(Rt.AllocObject); e.Set(c); e.End();
                e.Get(c);
            }
            e.Store(WOp.I32Store, ElemDataOffset + 4 * i);
        }
        e.Get(res);
        e.Op(WOp.Return);
        return f;
    }

    /// <summary>The dynamic thunk of a delegate type: (delegate, args: object[]) -> the result boxed (null for void), the
    /// arguments unboxed from the array (they are of the parameters' types: ArgumentsThunk). A struct argument is passed
    /// from a copy of its own; a struct result is written to a box made for it.</summary>
    WasmFunction DynamicThunk(TypeDesc dt)
    {
        var inv = DelegateInvokeOf(dt);
        var sig = inv.Signature;
        var f = Module.AddFunction("delegate dynamic thunk " + dt, new FuncType([ValType.I32, ValType.I32], [ValType.I32]));
        var e = new Emitter(f);
        var invoke = DelegateInvoke(inv);
        int n = sig.Length;
        var rt = sig.ReturnType;
        var copies = new List<int>();
        e.Get(0);
        for (int i = 0; i < n; i++)
        {
            var pt = sig[i];
            e.Get(1); e.Load(WOp.I32Load, ElemDataOffset + 4 * i);
            if (Kinds.Of(pt) == VK.Ref) continue;
            e.Call(UnboxFn(pt));
            e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add);
            if (Kinds.Of(pt) != VK.Struct) { LoadTyped(e, pt, 0); continue; }
            // (a struct argument: a copy, so the callee's own writes stay off the boxed value)
            Constructed(pt);
            int src = e.AddLocal(ValType.I32), copy = e.AddLocal(ValType.I32);
            e.Set(src);
            e.I32(IlContext.ObjectHeader + Math.Max(4, Kinds.SizeOf(pt))); e.Tid(TypeId(pt)); e.Call(Rt.AllocObject); e.Set(copy);
            e.Get(copy); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); e.Get(src); e.I32(SizeOfStructOf(pt)); e.Emit(WOp.MemoryCopy);
            if (Kinds.Counted(pt)) { e.Get(copy); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); e.Call(Rt.RetainStruct(pt)); }
            copies.Add(copy);
            e.Get(copy); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add);
        }
        int resBox = -1;
        if (Kinds.Of(rt) == VK.Struct && !rt.IsVoid)
        {
            Constructed(rt);
            resBox = e.AddLocal(ValType.I32);
            e.I32(IlContext.ObjectHeader + Math.Max(4, Kinds.SizeOf(rt))); e.Tid(TypeId(rt)); e.Call(Rt.AllocObject); e.Set(resBox);
            e.Get(resBox); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add);
        }
        // (a struct result is written to the buffer, a void one is nothing: nothing is on the stack after the call then)
        e.Call(invoke);
        int res = -1;
        if (rt.IsVoid || resBox >= 0) { }
        else if (Kinds.Of(rt) == VK.Ref) { res = e.AddLocal(ValType.I32); e.Set(res); }
        else
        {
            int v = e.AddLocal(FunctionLowering.ValOf(Kinds.Of(rt)));
            e.Set(v);
            Constructed(rt);
            res = e.AddLocal(ValType.I32);
            e.I32(IlContext.ObjectHeader + Math.Max(4, Kinds.SizeOf(rt))); e.Tid(TypeId(rt)); e.Call(Rt.AllocObject); e.Set(res);
            e.Get(res); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); e.Get(v); StoreTyped(e, rt, 0);
        }
        foreach (int c in copies) { e.Get(c); e.Call(Rt.Release); }
        if (rt.IsVoid) e.I32(0);
        else if (resBox >= 0) e.Get(resBox);
        else e.Get(res);
        e.Op(WOp.Return);
        return f;
    }

    /// <summary>System.TypeCode of a primitive type (the widening's target), 0 for the others.</summary>
    static int TypeCodeOf(TypeDesc t) => t.Category switch
    {
        TypeFlags.Boolean => 3, TypeFlags.Char => 4, TypeFlags.SByte => 5, TypeFlags.Byte => 6, TypeFlags.Int16 => 7, TypeFlags.UInt16 => 8,
        TypeFlags.Int32 => 9, TypeFlags.UInt32 => 10, TypeFlags.Int64 => 11, TypeFlags.UInt64 => 12, TypeFlags.Single => 13, TypeFlags.Double => 14,
        _ => 0,
    };

    static int SizeOfStructOf(TypeDesc t) => ((DefType)t).InstanceFieldSize.AsInt;

    /// <summary>The offset of element 0 of an object[] (its length is at 12).</summary>
    const int ElemDataOffset = 16;

    /// <summary>A value of a primitive or enum type loaded from an address (as FunctionLowering.Load).</summary>
    static void LoadTyped(Emitter e, TypeDesc t, int offset)
    {
        switch (t.Category)
        {
            case TypeFlags.Boolean: case TypeFlags.Byte: e.Load(WOp.I32Load8U, offset); break;
            case TypeFlags.SByte: e.Load(WOp.I32Load8S, offset); break;
            case TypeFlags.Char: case TypeFlags.UInt16: e.Load(WOp.I32Load16U, offset); break;
            case TypeFlags.Int16: e.Load(WOp.I32Load16S, offset); break;
            case TypeFlags.Int64: case TypeFlags.UInt64: e.Load(WOp.I64Load, offset); break;
            case TypeFlags.Single: e.Load(WOp.F32Load, offset); break;
            case TypeFlags.Double: e.Load(WOp.F64Load, offset); break;
            case TypeFlags.Enum: LoadTyped(e, t.UnderlyingType, offset); break;
            default: e.Load(WOp.I32Load, offset); break;
        }
    }

    /// <summary>A value of a primitive or enum type stored at an address (the address first, then the value).</summary>
    static void StoreTyped(Emitter e, TypeDesc t, int offset)
    {
        switch (t.Category)
        {
            case TypeFlags.Boolean: case TypeFlags.Byte: case TypeFlags.SByte: e.Store(WOp.I32Store8, offset); break;
            case TypeFlags.Char: case TypeFlags.UInt16: case TypeFlags.Int16: e.Store(WOp.I32Store16, offset); break;
            case TypeFlags.Int64: case TypeFlags.UInt64: e.Store(WOp.I64Store, offset); break;
            case TypeFlags.Single: e.Store(WOp.F32Store, offset); break;
            case TypeFlags.Double: e.Store(WOp.F64Store, offset); break;
            case TypeFlags.Enum: StoreTyped(e, t.UnderlyingType, offset); break;
            default: e.Store(WOp.I32Store, offset); break;
        }
    }
}
