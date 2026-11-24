#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Reach;

/// <summary>A value as the constant propagation knows it: not yet seen (Top), one integer constant (a null reference is
/// the constant 0 of a reference), a reference known not null, or anything (Bottom).</summary>
public readonly struct Val : IEquatable<Val>
{
    /// <summary>Typed: null, or an object of exactly Type.</summary>
    public enum S : byte { Top, Const, NonNull, Typed, Bottom }
    public readonly S State;
    public readonly long V;
    /// <summary>A non-null reference's exact type, when known (a new object's, a literal's).</summary>
    public readonly Internal.TypeSystem.TypeDesc? Type;
    Val(S s, long v, Internal.TypeSystem.TypeDesc? t = null) { State = s; V = v; Type = t; }
    public static readonly Val Top = new(S.Top, 0), Bottom = new(S.Bottom, 0), NonNull = new(S.NonNull, 0), Null = new(S.Const, 0);
    public static Val Of(long v) => new(S.Const, v);
    public static Val Exact(Internal.TypeSystem.TypeDesc t) => new(S.NonNull, 0, t);
    public bool IsConst => State == S.Const;
    public static Val Meet(Val a, Val b)
    {
        if (a.State == S.Top) return b;
        if (b.State == S.Top) return a;
        if (a.Equals(b)) return a;
        if (a.State == S.NonNull && b.State == S.NonNull) return NonNull;
        // (null, or one exact type: a receiver whose call has one implementation, or throws)
        var t = a.Type ?? b.Type;
        if (t != null && (a.Type == t || a.Equals(Null)) && (b.Type == t || b.Equals(Null))) return new(S.Typed, 0, t);
        return Bottom;
    }
    public bool Equals(Val o) => State == o.State && V == o.V && Type == o.Type;
    public override bool Equals(object? o) => o is Val v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(State, V, Type);
    public override string ToString() => State == S.Const ? V.ToString() : Type != null ? State + " " + Type : State.ToString();
}

/// <summary>What the constant propagation asks of the whole program: an argument's value (constant arguments), a
/// call's value (a substitution, a pure call under constant arguments: with <c>removable</c> when the call can go
/// altogether), a static field's (an unwritten one, a substituted one).</summary>
public interface ISccpOracle
{
    Val Arg(Body body, int index);
    Val Call(Node call, Val[] args, out bool removable);
    Val StaticField(Node load);
    Val InstanceField(Node load);
}

/// <summary>Sparse conditional constant propagation (Wegman and Zadeck) over one method's SSA IR: the blocks that can
/// run, given the constants the program supplies, and the value of each node in them. Integers are exact in their
/// width with the CLR's rules (wrapping, and nothing folded that would throw: a division by zero, an overflow-checked
/// operation out of range); references are null, known non-null, or unknown. Floating-point values are not folded.
/// The result is the facts for a second import (ImportFacts): each conditional branch and switch that goes one way,
/// and each static call that is pure under its constant arguments.</summary>
public sealed class Sccp
{
    readonly Body _body;
    readonly ISccpOracle _oracle;
    readonly Dictionary<Node, Val> _val = new();
    readonly Dictionary<Node, List<Node>> _uses = new();
    readonly HashSet<Block> _exec = [];
    readonly HashSet<(Block, Block)> _edges = [];
    readonly Queue<(Block?, Block)> _cfg = new();
    readonly Queue<Node> _ssa = new();
    readonly Dictionary<Block, List<Block>> _handlersOf = new();
    readonly HashSet<Node> _removable = [];
    readonly Dictionary<Node, Block> _blockOf = new();

    Sccp(Body body, ISccpOracle oracle) { _body = body; _oracle = oracle; }

    public static Sccp Solve(Body body, ISccpOracle oracle)
    {
        var s = new Sccp(body, oracle);
        s.Run();
        return s;
    }

    public Val ValueOf(Node n) => _val.GetValueOrDefault(n, Val.Top);
    public bool Executable(Block b) => _exec.Contains(b);
    public bool IsRemovable(Node call) => _removable.Contains(call);
    public IEnumerable<Block> ExecutableBlocks => _body.Blocks.Where(_exec.Contains);

    /// <summary>The facts for the second import, or null when nothing was decided.</summary>
    public static ImportFacts? Facts(Body body, Sccp s)
    {
        var f = new ImportFacts();
        foreach (var b in body.Blocks)
        {
            if (!s._exec.Contains(b) || b.ILStart < 0 || b.Term is not { } t) continue;
            if (t.Op is Opc.BrIf or Opc.Switch && s.Taken(b) is int i && b.Succs.Distinct().Count() > 1) f.BranchTo[b.ILStart] = b.Succs[i].ILStart;
            foreach (var n in b.Code)
                if (n.Op == Opc.Call && s._removable.Contains(n) && n.ILOffset >= 0 && (n.Kind == VK.Void || s.ValueOf(n).IsConst && n.Kind is VK.I32 or VK.I64))
                    f.Calls[n.ILOffset] = (n.Kind, s.ValueOf(n).V);
        }
        return f.IsEmpty ? null : f;
    }

    void Run()
    {
        foreach (var b in _body.Blocks)
        {
            foreach (var n in b.Phis.Concat(b.Code))
            {
                _blockOf[n] = b;
                foreach (var a in n.A) if (a != null) { if (!_uses.TryGetValue(a, out var l)) _uses[a] = l = []; l.Add(n); }
            }
        }
        foreach (var r in _body.Regions)
        {
            if (!(r.IsHandler || r.Kind == RegionKind.Filter) || r.Try == null) continue;
            if (!_handlersOf.TryGetValue(r.Try.Entry, out var l)) _handlersOf[r.Try.Entry] = l = [];
            l.Add(r.Entry);
        }
        var entry = _body.Blocks[0];
        _cfg.Enqueue((null, entry));
        while (_cfg.Count > 0 || _ssa.Count > 0)
        {
            while (_cfg.Count > 0)
            {
                var (from, to) = _cfg.Dequeue();
                if (from != null && !_edges.Add((from, to))) continue;
                if (_exec.Add(to))
                {
                    foreach (var n in to.Phis) Visit(n);
                    foreach (var n in to.Code) Visit(n);
                    if (to.Term == null) foreach (var s in to.Succs) _cfg.Enqueue((to, s));
                    if (_handlersOf.TryGetValue(to, out var hs)) foreach (var h in hs) _cfg.Enqueue((null, h));
                }
                else foreach (var n in to.Phis) Visit(n);
            }
            while (_ssa.Count > 0)
            {
                var n = _ssa.Dequeue();
                if (_exec.Contains(_blockOf[n])) Visit(n);
            }
        }
    }

    void Visit(Node n)
    {
        if (n.IsTerminator) { Branch(n); return; }
        if (!n.HasValue && n.Op != Opc.Call) return;
        var v = Eval(n);
        var old = ValueOf(n);
        // (monotone: a value only goes down)
        v = Val.Meet(old, v);
        if (v.Equals(old) && _val.ContainsKey(n)) return;
        _val[n] = v;
        if (_uses.TryGetValue(n, out var us)) foreach (var u in us) _ssa.Enqueue(u);
    }

    int? Taken(Block b)
    {
        var t = b.Term!;
        var c = ValueOf(t.A[0]);
        if (!c.IsConst) return null;
        if (t.Op == Opc.BrIf) return c.V != 0 ? 0 : 1;
        int cases = b.Succs.Count - 1;
        return (ulong)(uint)c.V < (ulong)cases ? (int)(uint)c.V : cases;
    }

    void Branch(Node t)
    {
        var b = _blockOf[t];
        if (t.Op is Opc.BrIf or Opc.Switch)
        {
            var c = ValueOf(t.A[0]);
            if (c.State == Val.S.Top) return;
            if (Taken(b) is int i) { _cfg.Enqueue((b, b.Succs[i])); return; }
        }
        foreach (var s in b.Succs) _cfg.Enqueue((b, s));
    }

    Val Eval(Node n)
    {
        Val A(int i) => ValueOf(n.A[i]);
        switch (n.Op)
        {
            case Opc.Const: return n.Kind is VK.I32 or VK.Ptr ? Val.Of((int)n.I) : n.Kind == VK.I64 ? Val.Of(n.I) : Val.Bottom;
            case Opc.LdNull: return Val.Null;
            // (a box of a Nullable<T> without a value is null)
            case Opc.Box: return n.X is TypeDesc bt && bt.IsNullable || n.Type is { IsNullable: true } ? Val.Bottom : Val.NonNull;
            case Opc.TypeOf: return Val.NonNull;
            case Opc.LdStr: case Opc.NewArr: return n.Type is { } st ? Val.Exact(st) : Val.NonNull;
            case Opc.NewObj: return n.Kind == VK.Ref ? (n.Type is { } nt ? Val.Exact(nt) : Val.NonNull) : Val.Bottom;
            case Opc.Arg: return _oracle.Arg(_body, (int)n.I);
            case Opc.Phi:
            {
                var b = _blockOf[n];
                var v = Val.Top;
                for (int i = 0; i < n.A.Length; i++)
                    if (n.A[i] != null && i < b.Preds.Count && _edges.Contains((b.Preds[i], b))) v = Val.Meet(v, ValueOf(n.A[i]));
                return v;
            }
            case Opc.Bin: return Bin(n, A(0), A(1));
            case Opc.Un:
            {
                var a = A(0);
                if (a.State == Val.S.Top) return Val.Top;
                if (!a.IsConst || n.Kind is not (VK.I32 or VK.I64 or VK.Ptr)) return Val.Bottom;
                long r = (UnOp)n.Sub == UnOp.Neg ? unchecked(-a.V) : ~a.V;
                return Val.Of(Narrow(r, n.Kind));
            }
            case Opc.Cmp: return Cmp(n, A(0), A(1));
            case Opc.Conv: return Conv(n, A(0));
            case Opc.IsInst: case Opc.CastClass:
            {
                var a = A(0);
                if (a.State == Val.S.Top) return Val.Top;
                if (a.Equals(Val.Null)) return Val.Null;
                // (an object of a known exact type: the test is decided; a failed cast throws, so its value is not known)
                if (a.Type != null && n.X is Internal.TypeSystem.TypeDesc u)
                    return a.Type.CanCastTo(u) ? a : n.Op == Opc.IsInst ? Val.Null : Val.Bottom;
                return Val.Bottom;
            }
            case Opc.LdSFld: return _oracle.StaticField(n);
            case Opc.LdFld: return _oracle.InstanceField(n);
            case Opc.Call:
            {
                var args = new Val[n.A.Length];
                for (int i = 0; i < args.Length; i++) { args[i] = A(i); if (args[i].State == Val.S.Top) return Val.Top; }
                var v = _oracle.Call(n, args, out bool removable);
                if (removable) _removable.Add(n); else _removable.Remove(n);
                return v;
            }
            default: return Val.Bottom;
        }
    }

    static bool Is32(VK k) => k is VK.I32 or VK.Ptr;
    static long Narrow(long v, VK k) => Is32(k) ? (int)v : v;

    static Val Bin(Node n, Val a, Val b)
    {
        if (a.State == Val.S.Top || b.State == Val.S.Top) return Val.Top;
        if (!a.IsConst || !b.IsConst || n.Kind is not (VK.I32 or VK.I64 or VK.Ptr)) return Val.Bottom;
        bool w32 = Is32(n.Kind);
        long x = a.V, y = b.V;
        ulong ux = w32 ? (uint)x : (ulong)x, uy = w32 ? (uint)y : (ulong)y;
        var op = (BinOp)n.Sub;
        if (n.Ovf)
        {
            // add.ovf, sub.ovf, mul.ovf: folded only in range (out of range it throws)
            Int128 r = op switch
            {
                BinOp.Add => n.Unsigned ? (Int128)ux + uy : (Int128)x + y,
                BinOp.Sub => n.Unsigned ? (Int128)ux - uy : (Int128)x - y,
                BinOp.Mul => n.Unsigned ? (Int128)ux * uy : (Int128)x * y,
                _ => Int128.MinValue,
            };
            if (r == Int128.MinValue) return Val.Bottom;
            Int128 lo = n.Unsigned ? 0 : w32 ? int.MinValue : long.MinValue;
            Int128 hi = n.Unsigned ? (w32 ? uint.MaxValue : ulong.MaxValue) : (w32 ? int.MaxValue : long.MaxValue);
            if (r < lo || r > hi) return Val.Bottom;
            return Val.Of(Narrow((long)(ulong)(UInt128)(r & ulong.MaxValue), n.Kind));
        }
        int mask = w32 ? 31 : 63;
        long res;
        switch (op)
        {
            case BinOp.Add: res = unchecked(x + y); break;
            case BinOp.Sub: res = unchecked(x - y); break;
            case BinOp.Mul: res = unchecked(x * y); break;
            case BinOp.And: res = x & y; break;
            case BinOp.Or: res = x | y; break;
            case BinOp.Xor: res = x ^ y; break;
            case BinOp.Shl: res = x << (int)(y & mask); break;
            case BinOp.Shr: res = w32 ? (int)x >> (int)(y & mask) : x >> (int)(y & mask); break;
            case BinOp.ShrUn: res = (long)(ux >> (int)(y & mask)); break;
            case BinOp.Div: case BinOp.Rem:
                // (a division by zero or MinValue / -1 throws: not folded)
                if (y == 0 || y == -1 && x == (w32 ? int.MinValue : long.MinValue)) return Val.Bottom;
                res = op == BinOp.Div ? x / y : x % y; break;
            case BinOp.DivUn: case BinOp.RemUn:
                if (uy == 0) return Val.Bottom;
                res = (long)(op == BinOp.DivUn ? ux / uy : ux % uy); break;
            default: return Val.Bottom;
        }
        return Val.Of(Narrow(res, n.Kind));
    }

    static Val Cmp(Node n, Val a, Val b)
    {
        if (a.State == Val.S.Top || b.State == Val.S.Top) return Val.Top;
        var op = (CmpOp)n.Sub;
        var k = n.A[0].Kind;
        if (k == VK.Ref || n.A[1].Kind == VK.Ref)
        {
            // references: null against null or against one known not null
            bool aNull = a.Equals(Val.Null), bNull = b.Equals(Val.Null);
            bool aNon = a.State == Val.S.NonNull, bNon = b.State == Val.S.NonNull;
            if (!(aNull && (bNull || bNon) || bNull && aNon)) return Val.Bottom;
            bool eq = aNull && bNull;
            // (cgt.un / clt.un: a non-null reference is above null)
            return op switch
            {
                CmpOp.Eq => Val.Of(eq ? 1 : 0), CmpOp.Ne => Val.Of(eq ? 0 : 1),
                CmpOp.GtUn => Val.Of(aNon && bNull ? 1 : 0), CmpOp.LtUn => Val.Of(aNull && bNon ? 1 : 0),
                _ => Val.Bottom,
            };
        }
        if (!a.IsConst || !b.IsConst || k is not (VK.I32 or VK.I64 or VK.Ptr) || n.A[1].Kind is not (VK.I32 or VK.I64 or VK.Ptr)) return Val.Bottom;
        bool w32 = Is32(k) && Is32(n.A[1].Kind);
        long x = a.V, y = b.V;
        ulong ux = w32 ? (uint)x : (ulong)x, uy = w32 ? (uint)y : (ulong)y;
        bool r = op switch
        {
            CmpOp.Eq => x == y, CmpOp.Ne => x != y,
            CmpOp.Gt => x > y, CmpOp.Ge => x >= y, CmpOp.Lt => x < y, CmpOp.Le => x <= y,
            CmpOp.GtUn => ux > uy, CmpOp.GeUn => ux >= uy, CmpOp.LtUn => ux < uy, CmpOp.LeUn => ux <= uy,
            _ => false,
        };
        return Val.Of(r ? 1 : 0);
    }

    static Val Conv(Node n, Val a)
    {
        if (a.State == Val.S.Top) return Val.Top;
        var from = n.A[0].Kind;
        var to = (VK)n.Sub;
        if (!a.IsConst || from is not (VK.I32 or VK.I64 or VK.Ptr) || to is not (VK.I32 or VK.I64 or VK.Ptr)) return Val.Bottom;
        bool fromUn = n.I == 1;
        long s = a.V;
        int bits = to == VK.I64 ? 64 : to == VK.Ptr ? 32 : n.Small switch { 1 => 8, 2 => 16, _ => 32 };
        if (n.Ovf)
        {
            // the source as its signedness reads it, against the target's range
            Int128 v = Is32(from) ? (fromUn ? (Int128)(uint)s : (int)s) : (fromUn ? (Int128)(ulong)s : s);
            Int128 lo = n.Unsigned ? 0 : -((Int128)1 << (bits - 1));
            Int128 hi = n.Unsigned ? ((Int128)1 << bits) - 1 : ((Int128)1 << (bits - 1)) - 1;
            if (v < lo || v > hi) return Val.Bottom;
            return Val.Of(to == VK.I64 ? (long)v : (int)(long)v);
        }
        long r;
        if (to == VK.I64) r = Is32(from) ? (n.Unsigned ? (long)(uint)s : (int)s) : s;
        else r = bits switch
        {
            8 => n.Unsigned ? (byte)s : (sbyte)s,
            16 => n.Unsigned ? (ushort)s : (short)s,
            _ => (int)s,
        };
        return Val.Of(r);
    }
}
