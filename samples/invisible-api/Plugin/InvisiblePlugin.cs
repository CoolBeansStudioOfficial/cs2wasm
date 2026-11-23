// The Invisible API's first slice (docs/FINISH.md 9.4) as an il2wasm plugin against interface version 7. It reads a
// game written in plain C# (../Game/Arena.cs) and turns it into a server's view of it without changing the game:
//
//  1. Classifies each write of the game's state: by the paths of calls that reach it (PathConditions), whether it
//     runs under a value from a player's input (a condition that depends on an input export's parameters), only
//     from the tick, or only while the world is set up; a member an unsure site touches (UnsureSites: a `ref`, an
//     unknown delegate, reflection) or a write no hook can follow is unknown.
//  2. Sees every input as it arrives (InterceptValue on the reads of the input exports' parameters): the server's
//     log of inputs, and the place it can replace one with the input it decided on.
//  3. Marks the interactions changed: each write that runs under input (InstrumentWrite) and each entry write under
//     input with its key, the player's id (InstrumentEntryWrite, version 7), so the server knows what to send after
//     each input. Deterministic writes (the tick's) are not marked: every client runs them itself.
//  4. Generates one gate (AddSource): whether a player's fire would be accepted now, made from the conditions the
//     game's own TryFire checks, reading the player's private fields; exported for the server with a view of a
//     player's private state.
//  5. Solves the wave timer (Solve, GenerateAdvance): the server advances it n ticks in closed form, exactly.
//
// Everything is registered from RunBeforeCompile, so the program is compiled once with its hooks.
using System.Text;
using Il2Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace InvisibleApi;

public enum WriteClass
{
    /// <summary>Runs only while the world is set up (static initializers, constructors reached from them).</summary>
    Setup,
    /// <summary>Runs only from the tick: deterministic, every client runs it.</summary>
    Tick,
    /// <summary>Runs from an input export, with no condition on the input's values.</summary>
    Input,
    /// <summary>Runs from an input export under a condition on the input's values.</summary>
    InputGated,
    /// <summary>The member is touched where the analyses cannot follow (an unsure site), or this write has no hook.</summary>
    Unknown,
}

/// <summary>One write of the game's state and what the plugin made of it.</summary>
public sealed record WriteInfo(string Member, string Method, int Line, bool Entry, string? Key, WriteClass Class, bool Marked, string Why)
{
    public override string ToString() => $"{Member}{(Entry ? "[" + (Key ?? "") + "]" : "")} in {Method} (line {Line}): {Class}{(Marked ? ", marked" : "")}; {Why}";
}

public sealed class InvisiblePlugin : ICompilerPluginBeforeCompile
{
    /// <summary>The exports a player's input arrives through, and the tick's.</summary>
    public string[] InputExports { get; init; } = ["join", "input"];
    public string[] TickExports { get; init; } = ["tick"];
    /// <summary>The game's namespace: the state classified is its types' fields and properties.</summary>
    public string GameNamespace { get; init; } = "Arena";

    public int InterfaceVersion => 7;
    public string Name => "invisible-api";

    /// <summary>What the plugin found, for its embedder (the test checks it).</summary>
    public List<WriteInfo> Writes { get; } = [];
    public List<string> UnknownMembers { get; } = [];
    public List<string> InputReads { get; } = [];
    public string Gate { get; private set; } = "";
    public string Generated { get; private set; } = "";
    public ClosedForm? Timer { get; private set; }
    public string? Problem { get; private set; }
    public int RunsBefore { get; private set; }
    public int RunsAfter { get; private set; }

    public void RunBeforeCompile(PluginContext c)
    {
        RunsBefore++;
        Writes.Clear(); UnknownMembers.Clear(); InputReads.Clear(); Problem = null;
        try { Build(c); }
        catch (Exception ex) { Problem = ex.ToString(); }
    }

    /// <summary>After the compile: nothing is added (it would compile the instrumented code again).</summary>
    public void Run(PluginContext c) { RunsAfter++; }

    void Build(PluginContext c)
    {
        var exports = new Dictionary<string, IMethodSymbol>();
        foreach (var m in c.ReachableMethods)
            foreach (var a in m.GetAttributes())
                if (a.AttributeClass?.Name == "UnmanagedCallersOnlyAttribute" && a.NamedArguments.FirstOrDefault(n => n.Key == "EntryPoint").Value.Value is string name)
                    exports[name] = m;
        var inputRoots = InputExports.Select(n => exports.TryGetValue(n, out var m) ? m : throw new InvalidOperationException("no export " + n)).ToHashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var tickRoots = TickExports.Select(n => exports[n]).ToHashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        bool InGame(ISymbol s) => s.ContainingNamespace?.ToDisplayString() == GameNamespace;

        // ---- unknown: what the analyses cannot follow
        var unsure = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var u in c.UnsureSites)
            if (u.Member is { } m && InGame(m)) { unsure.Add(m.OriginalDefinition); UnknownMembers.Add($"{m.ContainingType.Name}.{m.Name}: {u.Kind} in {u.Method.Name}, {u.Reason}"); }

        // ---- the server's code: hooks, the gate, the solved timer
        var memberNames = new List<string>();
        int Tag(ISymbol m) { var n = m.ContainingType.Name + "." + m.Name; int i = memberNames.IndexOf(n); if (i < 0) { memberNames.Add(n); i = memberNames.Count - 1; } return i; }

        // the classification
        var plan = new List<(MemberAccess access, WriteClass cls)>();
        foreach (var a in c.Accesses)
        {
            if (a.Kind == AccessKind.Read || !InGame(a.Member) || !InGame(a.Method)) continue;
            var paths = c.PathConditions(a.Operation);
            WriteClass cls; string why;
            var fromInput = paths.Where(p => p.Start == PathStart.Entry && inputRoots.Contains(p.Root)).ToList();
            if (unsure.Contains(a.Member.OriginalDefinition)) { cls = WriteClass.Unknown; why = "an unsure site touches the member"; }
            else if (!a.Instrumentable) { cls = WriteClass.Unknown; why = "no hook can run at this write"; }
            else if (fromInput.Count > 0)
            {
                var gate = fromInput.SelectMany(p => p.Conditions.Where(b => new Taint(c, p, inputRoots).Of(b.Test)).Select(b => b.Test.Syntax.ToString())).Distinct().ToList();
                cls = gate.Count > 0 ? WriteClass.InputGated : WriteClass.Input;
                why = "from " + string.Join(", ", fromInput.Select(p => ExportName(p.Root)).Distinct()) + (gate.Count > 0 ? " under " + string.Join(" and ", gate) : "");
            }
            else if (paths.Count > 0 && paths.All(p => p.Start == PathStart.Entry && tickRoots.Contains(p.Root))) { cls = WriteClass.Tick; why = "from tick only"; }
            else if (paths.Count > 0 && paths.All(p => p.Root.MethodKind == MethodKind.StaticConstructor)) { cls = WriteClass.Setup; why = "while the world is set up"; }
            else { cls = WriteClass.Unknown; why = "reached from " + string.Join(", ", paths.Select(p => p.Start + " " + p.Root.Name).Distinct()); }
            plan.Add((a, cls));
            Writes.Add(new WriteInfo(a.Member.ContainingType.Name + "." + a.Member.Name, a.Method.ContainingType.Name + "." + a.Method.Name,
                a.Location.GetLineSpan().StartLinePosition.Line + 1, a.IsEntry, a.Key?.Syntax.ToString(), cls, false, why));
        }
        string ExportName(IMethodSymbol m) => exports.First(e => SymbolEqualityComparer.Default.Equals(e.Value, m)).Key;

        // the gate: the conditions TryFire checks before the write it gates (its _shots++), over the player's private
        // fields, as C# the server runs against any player
        var player = c.Compilation.GetTypeByMetadataName(GameNamespace + ".Player")!;
        var shots = c.AccessesOf(player.GetMembers("_shots").Single()).Single(a => a.Kind != AccessKind.Read && a.Method.Name == "TryFire");
        var conds = shots.Conditions.Select(b => (b.WhenTrue ? "" : "!") + "(" + SelfCode(c, b.Test) + ")").ToList();
        Gate = conds.Count == 0 ? "true" : string.Join(" && ", conds);

        // the timer
        var spawner = c.Compilation.GetTypeByMetadataName(GameNamespace + ".Spawner")!;
        Timer = c.Solve(spawner.GetMembers("Tick").OfType<IMethodSymbol>().Single());
        if (!Timer.Solved || !Timer.Exact) { Problem = "the timer is not solved exactly: " + Timer.Reason; return; }

        // the marks (their tags name the members) and the input reads
        foreach (var (a, cls) in plan)
            if (cls is WriteClass.Input or WriteClass.InputGated && a.Method.MethodKind != MethodKind.Constructor) Tag(a.Member);
        var inputReads = new List<IParameterReferenceOperation>();
        foreach (var root in inputRoots)
        {
            var syntax = root.DeclaringSyntaxReferences.Single().GetSyntax();
            var model = c.Compilation.GetSemanticModel(syntax.SyntaxTree);
            foreach (var id in syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
                if (model.GetOperation(id) is IParameterReferenceOperation pr) { inputReads.Add(pr); InputReads.Add(ExportName(root) + "." + pr.Parameter.Name); }
        }

        var src = new StringBuilder();
        src.Append("using System;\nusing System.Collections.Generic;\nusing System.Runtime.InteropServices;\nusing " + GameNamespace + ";\n\n");
        src.Append(Timer.GenerateAdvance("SpawnerAdvance"));
        src.Append("""

            /// <summary>The server's side of the game, generated by the Invisible API plugin.</summary>
            public static class InvisibleServer
            {
                static readonly List<string> s_changes = new();
                static readonly List<string> s_inputs = new();
                static int s_override = -1;

            """);
        src.Append("    static readonly string[] s_members = { " + string.Join(", ", memberNames.Select(n => "\"" + n + "\"")) + " };\n");
        src.Append("    static readonly string[] s_inputNames = { " + string.Join(", ", InputReads.Select(n => "\"" + n + "\"")) + " };\n");
        src.Append("    /// <summary>The gate: whether a player's fire is accepted now (TryFire's own conditions).</summary>\n");
        src.Append("    public static bool FireGate(Player self) => " + Gate + ";\n");
        src.Append("""
                // marks: what changed since the server last asked
                public static void Changed(object o, int tag) => s_changes.Add(s_members[tag] + (o is Player p ? " p" + p.Id : ""));
                public static void EntryChanged(object o, int key, bool keyed, int tag) => s_changes.Add(s_members[tag] + "[" + (keyed ? key.ToString() : "?") + "]");
                // each input as it arrives; the server may replace the next action
                public static int SeeInput(int value, int tag)
                {
                    if (s_inputNames[tag] == "input.action" && s_override >= 0) { value = s_override; s_override = -1; }
                    s_inputs.Add(s_inputNames[tag] + "=" + value);
                    return value;
                }

                [UnmanagedCallersOnly(EntryPoint = "server_changes")]
                static void Changes() { s_changes.Sort(StringComparer.Ordinal); Console.WriteLine("changes: " + string.Join(", ", s_changes)); s_changes.Clear(); }
                [UnmanagedCallersOnly(EntryPoint = "server_inputs")]
                static void Inputs() { Console.WriteLine("inputs: " + string.Join(", ", s_inputs)); s_inputs.Clear(); }
                [UnmanagedCallersOnly(EntryPoint = "server_override")]
                static void Override(int action) => s_override = action;

                [UnmanagedCallersOnly(EntryPoint = "gate_fire")]
                static int GateFire(int id) => Game.s_world._players.TryGetValue(id, out var p) && FireGate(p) ? 1 : 0;

                [UnmanagedCallersOnly(EntryPoint = "gate_view")]
                static void View(int id)
                {
                    var w = Game.s_world;
                    if (!w._players.TryGetValue(id, out var p)) { Console.WriteLine("view p" + id + ": none"); return; }
                    Console.WriteLine("view p" + id + ": " + p._x + "," + p._y + " v " + p._vx + "," + p._vy + " hp " + p._hp + " cd " + p._cooldown + " shots " + p._shots + " coins " + w._coins[id]);
                }

                /// <summary>The wave timer n ticks from now, in closed form, on a copy of it.</summary>
                [UnmanagedCallersOnly(EntryPoint = "server_predict")]
                static void Predict(int n)
                {
                    var live = Game.s_world._spawner;
                    var s = new Spawner();
                    s._ms = live._ms; s._waves = live._waves; s._period = live._period;
                    SpawnerAdvance.Advance(s, World.TickMs, n);
                    Console.WriteLine("predict " + n + ": " + s.Describe());
                }

                /// <summary>The live timer advanced n ticks at once (a server catching up).</summary>
                [UnmanagedCallersOnly(EntryPoint = "server_advance")]
                static void Advance(int n) => SpawnerAdvance.Advance(Game.s_world._spawner, World.TickMs, n);
            }

            """);
        Generated = src.ToString();
        var gen = c.AddSource(Generated, "invisible.g.cs");
        IMethodSymbol H(string n) => gen.GetTypeByMetadataName("InvisibleServer")!.GetMembers(n).OfType<IMethodSymbol>().Single();

        for (int i = 0; i < plan.Count; i++)
        {
            var (a, cls) = plan[i];
            if (cls is not (WriteClass.Input or WriteClass.InputGated)) continue;
            // a constructor's writes are part of the new object: the entry write that stores it is the change
            if (a.Method.MethodKind == MethodKind.Constructor) { Writes[i] = Writes[i] with { Why = Writes[i].Why + "; part of a new object" }; continue; }
            if (a.IsEntry) c.InstrumentEntryWrite(a, H("EntryChanged"), Tag(a.Member));
            else c.InstrumentWrite(a.Operation.Parent!, H("Changed"), Tag(a.Member));
            Writes[i] = Writes[i] with { Marked = true };
        }
        foreach (var (pr, i) in inputReads.Select((p, i) => (p, i))) c.InterceptValue(pr, H("SeeInput"), i);
    }

    /// <summary>C# for a condition of an instance method with `this` named `self`: the member names it reads
    /// unqualified become `self.Name` (the generated code names private members as the program does).</summary>
    static string SelfCode(PluginContext c, IOperation test)
    {
        var model = c.Compilation.GetSemanticModel(test.Syntax.SyntaxTree);
        var expr = test.Syntax;
        var rewritten = expr.ReplaceNodes(expr.DescendantNodesAndSelf().Where(n => n is ThisExpressionSyntax || n is IdentifierNameSyntax id
                && !(id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id) && model.GetSymbolInfo(id).Symbol is { IsStatic: false } s && s.Kind is SymbolKind.Field or SymbolKind.Property),
            (orig, _) => (orig is ThisExpressionSyntax ? SyntaxFactory.IdentifierName("self") : SyntaxFactory.ParseExpression("self." + ((IdentifierNameSyntax)orig).Identifier.Text)).WithTriviaFrom(orig));
        return rewritten.ToString();
    }

    /// <summary>Whether a condition on a path depends on a value from input: it reads a parameter of the input export at
    /// the path's root, or a parameter whose argument on the path does, or a local whose writes do (flow-insensitive:
    /// any write of the local in its method).</summary>
    sealed class Taint(PluginContext c, ConditionPath path, HashSet<IMethodSymbol> inputRoots)
    {
        readonly HashSet<ISymbol> _seen = new(SymbolEqualityComparer.Default);
        List<IMethodSymbol> Chain => [path.Root, .. path.Calls.Select(s => s.Callee)];

        public bool Of(IOperation op) => Of(op.Syntax, Index(op.Syntax));

        int Index(SyntaxNode s)
        {
            var enc = c.Compilation.GetSemanticModel(s.SyntaxTree).GetEnclosingSymbol(s.SpanStart);
            while (enc is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } && enc.ContainingSymbol != null) enc = enc.ContainingSymbol;
            var chain = Chain;
            for (int i = chain.Count - 1; i >= 0; i--)
                if (SymbolEqualityComparer.Default.Equals(chain[i].OriginalDefinition, enc?.OriginalDefinition)) return i;
            return -1;
        }

        bool Of(SyntaxNode syntax, int at)
        {
            if (at < 0) return false;
            var model = c.Compilation.GetSemanticModel(syntax.SyntaxTree);
            foreach (var id in syntax.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var sym = model.GetSymbolInfo(id).Symbol;
                if (sym is IParameterSymbol p)
                {
                    if (at == 0) { if (inputRoots.Contains(path.Root)) return true; continue; }
                    // the argument the call on the path passed for it
                    var call = c.Compilation.GetSemanticModel(path.Calls[at - 1].Call.Syntax.SyntaxTree).GetOperation(path.Calls[at - 1].Call.Syntax);
                    var args = call switch { IInvocationOperation i => i.Arguments, IObjectCreationOperation o => o.Arguments, _ => default };
                    if (!args.IsDefault && args.FirstOrDefault(x => x.Parameter?.Ordinal == p.Ordinal) is { } arg && Of(arg.Value.Syntax, at - 1)) return true;
                }
                else if (sym is ILocalSymbol l && _seen.Add(l))
                {
                    var method = l.ContainingSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                    if (method == null) continue;
                    foreach (var n in method.DescendantNodes())
                    {
                        SyntaxNode? source = n switch
                        {
                            VariableDeclaratorSyntax v when v.Identifier.Text == l.Name && v.Initializer != null => v.Initializer.Value,
                            AssignmentExpressionSyntax asg when model.GetSymbolInfo(asg.Left).Symbol is ILocalSymbol t && SymbolEqualityComparer.Default.Equals(t, l) => asg.Right,
                            // `out var l`, `out l`: the call that writes it
                            ArgumentSyntax { RefKindKeyword.Text: "out" } arg when (arg.Expression is DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax d } && d.Identifier.Text == l.Name
                                || arg.Expression is IdentifierNameSyntax ai && ai.Identifier.Text == l.Name) => arg.Parent?.Parent,
                            ForEachStatementSyntax fe when fe.Identifier.Text == l.Name => fe.Expression,
                            _ => null,
                        };
                        if (source != null && Of(source, at)) return true;
                    }
                }
            }
            return false;
        }
    }
}
