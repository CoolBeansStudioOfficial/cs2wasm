using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Variables of an iterator or async method that its lambdas and local functions capture live in displays, as in
/// Roslyn's lowering: one for the method (allocated when MoveNext first runs) and one per loop iteration for
/// variables declared in a loop body (allocated each time the body's scope is entered), each held by a slot of the
/// state machine. Lambdas keep the display, not the machine, so a lambda stored in a local of the method makes no
/// cycle, and each iteration's lambdas see their own variable.
/// </summary>
sealed class MachineDisplay
{
    public required Display D;
    public required int SlotOff;
    public SyntaxNode? Loop;          // null: the method's display
    public MachineDisplay? Parent;    // null: the machine's own environment (the enclosing closure, if any)
}

sealed class MachineDisplays
{
    public readonly List<MachineDisplay> All = [];
    public MachineDisplay? Fn;
    public readonly Dictionary<ISymbol, MachineDisplay> BySymbol = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<SyntaxNode, MachineDisplay> ByLoop = [];
    public readonly List<(ISymbol sym, ISymbol machineKey)> CopyIn = []; // parameters and `this`, copied in at entry
}

sealed partial class FunctionCompiler
{
    /// <summary>Kickoff: decide which variables move into displays.</summary>
    MachineDisplays? PlanMachineDisplays(MachineInfo info, IOperation body, ISymbol thisKey, bool nested)
    {
        var self = M;
        var nestedOps = body.Descendants().Where(o => o is IAnonymousFunctionOperation or ILocalFunctionOperation).ToList();
        if (nestedOps.Count == 0) return null;
        var md = new MachineDisplays();
        var machine = info.Machine;
        Display NewDisplay() => new Display { Owner = null, Name = machine.Name + ".display" };
        MachineDisplay DisplayFor(object key)
        {
            if (key is IOperation loop)
            {
                if (md.ByLoop.TryGetValue(loop.Syntax, out var l)) return l;
                var d = new MachineDisplay { D = NewDisplay(), SlotOff = machine.Extra("disp" + md.All.Count, D.ObjectType, T), Loop = loop.Syntax };
                md.All.Add(d); md.ByLoop[loop.Syntax] = d;
                return d;
            }
            if (md.Fn == null)
            {
                md.Fn = new MachineDisplay { D = NewDisplay(), SlotOff = machine.Extra("disp" + md.All.Count, D.ObjectType, T) };
                md.All.Add(md.Fn);
            }
            return md.Fn;
        }
        void Add(ISymbol sym, object key, ITypeSymbol t)
        {
            if (md.BySymbol.ContainsKey(sym)) return;
            var d = DisplayFor(key);
            var (size, align) = T.StorageOf(t);
            d.D.Size = (d.D.Size + align - 1) / align * align;
            d.D.Slots[sym] = (d.D.Size, t);
            d.D.Vars.Add(sym);
            d.D.Size += size;
            md.BySymbol[sym] = d;
        }
        foreach (var n in nestedOps)
            foreach (var r in n.Descendants())
            {
                ISymbol? sym = r switch { ILocalReferenceOperation l => l.Local, IParameterReferenceOperation p => p.Parameter, _ => null };
                if (sym != null)
                {
                    if (!SymbolEqualityComparer.Default.Equals(sym.ContainingSymbol, self) && !SymbolEqualityComparer.Default.Equals(sym.ContainingSymbol?.OriginalDefinition, self.OriginalDefinition)) continue;
                    var key = DisplayKeyFor(sym, body);
                    var st = T.Resolve(sym switch { ILocalSymbol l => l.Type, IParameterSymbol p => p.Type, _ => throw new CompileException("capture") }, Map);
                    if (sym is IParameterSymbol ps) { Add(ps.OriginalDefinition, s_fnKey, st); md.BySymbol[ps] = md.BySymbol[ps.OriginalDefinition]; }
                    else Add(sym, key, st);
                }
                else if (r is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } && !nested && !M.IsStatic)
                    Add(thisKey, s_fnKey, T.Resolve(M.ContainingType, Map));
            }
        if (md.All.Count == 0) return null;
        DisplayFor(s_fnKey);
        // loop displays: the innermost enclosing loop display, else the method's
        foreach (var d in md.All)
        {
            if (d.Loop == null) continue;
            var loopOp = body.DescendantsAndSelf().First(o => o.Syntax == d.Loop && o is ILoopOperation);
            MachineDisplay? parent = null;
            for (var l = LoopIterationOf(loopOp, body); l != null && parent == null; l = LoopIterationOf(l, body))
                md.ByLoop.TryGetValue(l.Syntax, out parent);
            d.Parent = parent ?? DisplayFor(s_fnKey);
        }
        foreach (var d in md.All) { d.D.Parent = d.Parent?.D ?? machine.Parent; d.D.TypeId = D.DisplayTypeId(d.D); }
        foreach (var sym in md.Fn?.D.Vars ?? [])
            if (sym is IParameterSymbol || ReferenceEquals(sym, thisKey)) md.CopyIn.Add((sym, sym));
        return md;
    }

    /// <summary>The display a lambda or local function written at `node` closes over.</summary>
    Display? MachineEnvAt(SyntaxNode node)
    {
        var md = _machine!.Displays;
        if (md == null) return _closure?.Env;
        for (var s = node.Parent; s != null; node = s, s = s.Parent)
        {
            SyntaxNode? bodyStmt = s switch
            {
                ForEachStatementSyntax fe => fe.Statement,
                ForEachVariableStatementSyntax fv => fv.Statement,
                WhileStatementSyntax w => w.Statement,
                DoStatementSyntax d => d.Statement,
                ForStatementSyntax f => f.Statement,
                _ => null,
            };
            if (bodyStmt != null && bodyStmt == node && md.ByLoop.TryGetValue(s, out var ld)) return ld.D;
        }
        return md.Fn?.D ?? _closure?.Env;
    }

    bool TryMachineCapture(ISymbol s, out MachineDisplay d)
    {
        d = null!;
        return _machine?.Displays is { } md && md.BySymbol.TryGetValue(s, out d!);
    }

    MachineDisplay? MachineDisplayOf(Display target) => _machine?.Displays?.All.FirstOrDefault(x => ReferenceEquals(x.D, target));

    /// <summary>Allocates a fresh display into its machine slot (releasing the previous iteration's).</summary>
    void EmitAllocMachineDisplay(MachineDisplay d)
    {
        E.Get(0); E.Load(Op.I32Load, d.SlotOff); E.Call(Rt.Release);
        E.Get(0); E.I32(d.D.Size); E.I32(d.D.TypeId); E.Call(Rt.AllocObject); E.Store(Op.I32Store, d.SlotOff);
        E.Get(0); E.Load(Op.I32Load, d.SlotOff);
        if (d.Parent != null) { E.Get(0); E.Load(Op.I32Load, d.Parent.SlotOff); E.Call(Rt.Retain); }
        else if (_machine!.Machine.Parent != null) { E.Get(0); E.Load(Op.I32Load, 12); E.Call(Rt.Retain); }
        else E.I32(0);
        E.Store(Op.I32Store, 12);
    }

    /// <summary>MoveNext entry: the method's display, with the captured parameters and `this` copied in.</summary>
    void EmitMachineDisplaysEntry()
    {
        var md = _machine!.Displays;
        if (md?.Fn == null) return;
        EmitAllocMachineDisplay(md.Fn);
        var machine = _machine.Machine;
        foreach (var (sym, key) in md.CopyIn)
        {
            if (!machine.Slots.TryGetValue(key, out var src)) continue;
            var (dst, t) = md.Fn.D.Slots[sym];
            int obj = E.AddLocal(ValType.I32);
            E.Get(0); E.Load(Op.I32Load, md.Fn.SlotOff); E.Set(obj);
            CopyField(E, obj, dst, 0, src.offset, t);
        }
    }

    /// <summary>Loop displays whose scope starts at this block: the CFG region holding their variables.</summary>
    Dictionary<int, List<MachineDisplay>>? _displayStarts;

    void PlanDisplayStarts()
    {
        var md = _machine!.Displays;
        if (md == null) return;
        _displayStarts = [];
        foreach (var d in md.All)
        {
            if (d.Loop == null) continue;
            ControlFlowRegion? best = null;
            foreach (var r in AllRegions(_cfg!.Root))
            {
                if (!r.Locals.Any(l => d.D.Slots.ContainsKey(l))) continue;
                if (best == null || Encloses(r, best)) best = r;
            }
            if (best == null) continue;
            if (!_displayStarts.TryGetValue(best.FirstBlockOrdinal, out var list)) _displayStarts[best.FirstBlockOrdinal] = list = [];
            list.Add(d);
        }
        // outer displays first (a parent is allocated before its children)
        foreach (var list in _displayStarts.Values) list.Sort((a, b) => Depth(a) - Depth(b));
        static int Depth(MachineDisplay d) { int n = 0; for (var p = d.Parent; p != null; p = p.Parent) n++; return n; }
    }

    static bool Encloses(ControlFlowRegion outer, ControlFlowRegion inner)
    {
        for (var x = inner.EnclosingRegion; x != null; x = x.EnclosingRegion) if (ReferenceEquals(x, outer)) return true;
        return false;
    }

    void EmitDisplayStartsFor(BasicBlock b)
    {
        if (_displayStarts != null && _displayStarts.TryGetValue(b.Ordinal, out var list))
            foreach (var d in list) EmitAllocMachineDisplay(d);
    }
}
