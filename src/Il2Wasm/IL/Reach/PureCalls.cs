#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Reach;

/// <summary>Calls evaluated at compile time (NetWasm's ConstantCallEvaluator, on the IR): a static method whose
/// arguments are all constants, and whose code, under those constants, provably does nothing observable and cannot
/// throw, is replaced by its value (or removed, for a void one). The proof is the constant propagation of the callee
/// with its arguments bound: every node that can run is integer arithmetic that cannot trap (a division or a checked
/// operation only when its operands fold it in range), a comparison, a conversion between integers, a phi, a branch, or
/// a call that is itself such a call; the blocks that can run have no cycle (so it ends); every return that can run
/// gives the same constant. No field, no memory, no allocation, no exception region, no static constructor it could
/// start (a type with a .cctor and no beforefieldinit runs it at a static call), no intrinsic (whose IL is not what
/// runs) and no P/Invoke. IL2WASM_NO_PURE_CALLS=1: none.</summary>
public sealed class PureCalls
{
    public static readonly bool Off = Environment.GetEnvironmentVariable("IL2WASM_NO_PURE_CALLS") == "1";
    const int MaxDepth = 8, MaxIL = 512;
    readonly Substitutions _subs;
    readonly Dictionary<MethodDesc, Body?> _bodies = new();
    readonly Dictionary<(MethodDesc, string), (bool, Val)> _memo = new();
    int _depth;

    public PureCalls(Substitutions subs) { _subs = subs; }

    /// <summary>(pure, value): the value is Bottom for a void method.</summary>
    /// <summary>valueOnly: the call stays (an instance method's, whose receiver the call checks), only its value is
    /// wanted; its arguments may be unknown, and the body must still not depend on them for its value.</summary>
    public (bool Pure, Val Value) Evaluate(MethodDesc m, Val[] args, bool valueOnly = false)
    {
        if (Off) return (false, Val.Bottom);
        if (!valueOnly && !m.Signature.IsStatic) return (false, Val.Bottom);
        var key = (m, string.Join(",", args.Select(a => a.ToString())));
        if (_memo.TryGetValue(key, out var r)) return r;
        if (_depth >= MaxDepth) return (false, Val.Bottom);
        // (a recursive call while this one is evaluated: not pure)
        _memo[key] = (false, Val.Bottom);
        _depth++;
        try { r = Run(m, args); }
        finally { _depth--; }
        _memo[key] = r;
        return r;
    }

    Body? BodyOf(MethodDesc m)
    {
        if (_bodies.TryGetValue(m, out var b)) return b;
        b = null;
        if (m.GetTypicalMethodDefinition() is EcmaMethod em && !em.IsPInvoke && !em.IsInternalCall && !em.IsAbstract && !em.IsSynchronized
            && !em.HasCustomAttribute("Il2Wasm.Runtime", "IntrinsicAttribute") && !em.HasCustomAttribute("System.Runtime.CompilerServices", "IntrinsicAttribute") && !Il2Wasm.IL.Lower.IlCompiler.IsUpstreamIntrinsic(m)
            && !(m.Signature.IsStatic && m.OwningType is MetadataType { HasStaticConstructor: true, IsBeforeFieldInit: false }))
        {
            MethodIL? il = EcmaMethodIL.Create(em);
            if (il != null && il.GetILBytes().Length <= MaxIL && il.GetExceptionRegions().Length == 0)
            {
                if (m != em) il = new InstantiatedMethodIL(m, il);
                try { b = Importer.Import(m, il); }
                catch (Exception e) when (e is NotSupportedException or InvalidProgramException or TypeSystemException) { b = null; }
            }
        }
        _bodies[m] = b;
        return b;
    }

    (bool, Val) Run(MethodDesc m, Val[] args)
    {
        if (m.Signature.IsStatic && m.OwningType is MetadataType { HasStaticConstructor: true, IsBeforeFieldInit: false }) return (false, Val.Bottom);
        if (_subs.Method(m) is { } stub) return (true, stub.HasValue ? Val.Of(stub.Value) : Val.Bottom);
        var body = BodyOf(m);
        if (body == null || body.Regions.Count != 1 || body.Vars.Count != 0 || body.UsesLocAlloc) return (false, Val.Bottom);
        var s = Sccp.Solve(body, new Bound(this, args));
        var ret = Val.Top;
        bool anyRet = false;
        foreach (var b in s.ExecutableBlocks)
            foreach (var n in b.Code)
            {
                if (!Allowed(n, s)) return (false, Val.Bottom);
                if (n.Op == Opc.Ret) { anyRet = true; if (n.A.Length > 0) ret = Val.Meet(ret, s.ValueOf(n.A[0])); }
            }
        if (!anyRet || HasCycle(body, s)) return (false, Val.Bottom);
        if (m.Signature.ReturnType.IsVoid) return (true, Val.Bottom);
        return ret.IsConst ? (true, ret) : (false, Val.Bottom);
    }

    static bool Allowed(Node n, Sccp s)
    {
        switch (n.Op)
        {
            case Opc.Arg: case Opc.Const: case Opc.LdNull: case Opc.Cmp: case Opc.Un:
            case Opc.Br: case Opc.BrIf: case Opc.Switch: case Opc.Ret:
                return true;
            case Opc.Bin:
                return !n.Ovf && (BinOp)n.Sub is not (BinOp.Div or BinOp.DivUn or BinOp.Rem or BinOp.RemUn) || s.ValueOf(n).IsConst;
            case Opc.Conv:
                return n.A[0].Kind is VK.I32 or VK.I64 or VK.Ptr && (!n.Ovf || s.ValueOf(n).IsConst);
            case Opc.Call:
                return s.IsRemovable(n);
            default:
                return false;
        }
    }

    static bool HasCycle(Body body, Sccp s)
    {
        var state = new Dictionary<Block, int>();
        bool Dfs(Block b)
        {
            state[b] = 1;
            foreach (var x in b.Succs)
            {
                if (!s.Executable(x)) continue;
                if (state.TryGetValue(x, out int st)) { if (st == 1) return true; continue; }
                if (Dfs(x)) return true;
            }
            state[b] = 2;
            return false;
        }
        return Dfs(body.Blocks[0]);
    }

    /// <summary>The callee's arguments bound to the call's constants.</summary>
    sealed class Bound(PureCalls p, Val[] args) : ISccpOracle
    {
        public Val Arg(Body body, int index) => index < args.Length ? args[index] : Val.Bottom;
        public Val StaticField(Node load) => Val.Bottom;
        public Val InstanceField(Node load) => Val.Bottom;
        public Val Call(Node call, Val[] a, out bool removable) => p.CallValue(call, a, out removable);
    }

    /// <summary>A call's value for the constant propagation: a substituted method's constant (the call stays: it may
    /// throw for a null receiver), or a pure static call's under constant arguments (the call goes).</summary>
    public Val CallValue(Node call, Val[] args, out bool removable)
    {
        removable = false;
        if (call.X is not MethodDesc m || (CallKind)call.Sub != CallKind.Direct) return Val.Bottom;
        if (m.Signature.IsStatic)
        {
            var (pure, v) = Evaluate(m, args);
            // (a call that takes a reference keeps its object alive until it is made: GC.KeepAlive's empty body is a
            // use, and with counting the end of a lifetime is observable through finalizers and weak references. Such
            // a call stays; only its value is known)
            bool keepsAlive = call.A.Any(a => Kinds.Counted(a.Type ?? m.Context.GetWellKnownType(WellKnownType.Object)) && a.Kind is VK.Ref or VK.Struct && a.Op is not (Opc.LdStr or Opc.LdNull));
            if (pure) { removable = !keepsAlive; return keepsAlive && v.State == Val.S.Bottom ? Val.Bottom : v; }
        }
        else if (!m.IsAbstract)
        {
            // (an instance method's value: the call stays, and with it the receiver's null check)
            var a = (Val[])args.Clone();
            if (a.Length > 0 && !m.OwningType.IsValueType) a[0] = Val.NonNull;
            var (pure, v) = Evaluate(m, a, valueOnly: true);
            if (pure && v.IsConst) return v;
        }
        if (_subs.Method(m) is { HasValue: true } st) return Val.Of(st.Value);
        return Val.Bottom;
    }
}
