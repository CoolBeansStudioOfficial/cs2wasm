using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm;

// Plugin interface version 6: the closed-form solver (audit A4.6). PluginContext.Solve executes a per-tick method or
// a loop body symbolically and gives each state variable's new value as an affine function of the old state vector,
// x' = A x + b, piecewise, with the guards that choose a piece and how each guard's value moves from tick to tick,
// and generates C# that advances the state by n ticks at once.

/// <summary>(Version 6.) What an invariant of a solved region is: a value that does not change while the region runs
/// n times (a constant folded into a term is a coefficient, not an atom).</summary>
public enum SolverAtomKind
{
    /// <summary>A parameter of the per-tick method (each tick is taken to get the same arguments), or of the method
    /// a loop is in.</summary>
    Parameter,
    /// <summary>A field or auto-property of <c>this</c> or a static one that the region does not write.</summary>
    Member,
    /// <summary>A local declared outside the loop that the loop does not write.</summary>
    Local,
    /// <summary>An expression of other invariants that is not a sum or a product: a division, a remainder, a shift,
    /// a conversion, a call of a pure function, a comparison.</summary>
    Expression,
}

/// <summary>(Version 6.) An invariant a coefficient is made of. <see cref="Code"/> is C# that computes it exactly as
/// the program does, in terms of the generated Advance method's parameters (<c>self</c> for <c>this</c>).</summary>
public sealed class SolverAtom
{
    internal SolverAtom(int id, SolverAtomKind kind, ISymbol? symbol, IOperation? operation, ITypeSymbol type, string code)
    { Id = id; Kind = kind; Symbol = symbol; Operation = operation; Type = type; Code = code; }
    internal int Id { get; }
    public SolverAtomKind Kind { get; }
    /// <summary>The parameter, member or local read (null for an expression).</summary>
    public ISymbol? Symbol { get; }
    /// <summary>Where the solver first met it.</summary>
    public IOperation? Operation { get; }
    public ITypeSymbol Type { get; }
    public string Code { get; }
    public override string ToString() => Symbol?.Name ?? Operation?.Syntax.ToString() ?? Code;
}

/// <summary>(Version 6.) One product in a <see cref="SolverTerm"/>: a coefficient times invariants.</summary>
public sealed record SolverMonomial(IReadOnlyList<SolverAtom> Factors)
{
    /// <summary>The coefficient of an integer term (modulo 2^64, or 2^32 for an int).</summary>
    public long IntegerCoefficient { get; init; }
    /// <summary>The coefficient of a floating-point term.</summary>
    public double Coefficient { get; init; }
}

/// <summary>(Version 6.) A polynomial in the region's invariants with numeric coefficients: an entry of A or b, the
/// step of a guard. Integer terms are exact (wrapping, as unchecked arithmetic wraps); floating-point terms are the
/// real-number value of what the program computes in its rounding.</summary>
public sealed class SolverTerm
{
    internal readonly Dictionary<string, (long I, double F, SolverAtom[] Factors)> M;
    internal SolverTerm(bool isFloat, int width, Dictionary<string, (long, double, SolverAtom[])> m) { IsFloat = isFloat; Width = width; M = m; }
    /// <summary>Whether this is a floating-point term (else an integer one).</summary>
    public bool IsFloat { get; }
    internal int Width { get; }
    public IReadOnlyList<SolverMonomial> Monomials => M.Values.Select(v => new SolverMonomial(v.Factors) { IntegerCoefficient = v.I, Coefficient = v.F }).ToList();
    public bool IsZero => M.Count == 0;
    /// <summary>Whether the term is a number (no invariants in it).</summary>
    public bool IsConstant => M.Count == 0 || M.Count == 1 && M.ContainsKey("");
    public long IntegerValue => M.TryGetValue("", out var v) && M.Count == 1 ? v.I : 0;
    public double Value => M.TryGetValue("", out var v) && M.Count == 1 ? v.F : 0;

    public override string ToString()
    {
        if (M.Count == 0) return "0";
        var sb = new StringBuilder();
        foreach (var (key, (i, f, fs)) in M.OrderBy(kv => kv.Key.Length).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string c = IsFloat ? f.ToString("R", CultureInfo.InvariantCulture) : i.ToString(CultureInfo.InvariantCulture);
            bool neg = c.StartsWith('-');
            if (neg) c = c[1..];
            sb.Append(sb.Length == 0 ? (neg ? "-" : "") : neg ? " - " : " + ");
            if (fs.Length == 0) sb.Append(c);
            else
            {
                if (c != "1") sb.Append(c).Append('*');
                sb.Append(string.Join("*", fs.Select(a => a.ToString())));
            }
        }
        return sb.ToString();
    }
}

/// <summary>(Version 6.) An affine function of the old state vector: Σ Coefficients[j]·x[j] + Constant.</summary>
public sealed class SolverAffine
{
    internal SolverAffine(IReadOnlyList<SolverVariable> vars, IReadOnlyList<SolverTerm> coefficients, SolverTerm constant) { _vars = vars; Coefficients = coefficients; Constant = constant; }
    readonly IReadOnlyList<SolverVariable> _vars;
    /// <summary>One per variable (<see cref="ClosedForm.Variables"/>, in order).</summary>
    public IReadOnlyList<SolverTerm> Coefficients { get; }
    public SolverTerm Constant { get; }
    public override string ToString()
    {
        var parts = new List<string>();
        for (int j = 0; j < Coefficients.Count; j++)
        {
            var c = Coefficients[j];
            if (c.IsZero) continue;
            var s = c.ToString();
            parts.Add(s == "1" ? _vars[j].Symbol.Name : s == "-1" ? "-" + _vars[j].Symbol.Name : (c.M.Count > 1 ? "(" + s + ")" : s) + "*" + _vars[j].Symbol.Name);
        }
        if (!Constant.IsZero || parts.Count == 0) parts.Add(Constant.ToString());
        return string.Join(" + ", parts).Replace("+ -", "- ");
    }
}

/// <summary>(Version 6.) A state variable: written by the region (a field or auto-property of <c>this</c>, a static
/// one, a local of the loop's method declared outside the loop, a parameter written in a loop).</summary>
public sealed record SolverVariable(ISymbol Symbol, ITypeSymbol Type, int Index)
{
    public bool IsInteger => Type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64;
}

/// <summary>(Version 6.) How a guard's truth can change while its piece applies.</summary>
public enum SolverFlipKind
{
    /// <summary>Its value does not depend on the state, or does not change under the piece: it never flips there.</summary>
    Never,
    /// <summary>The compared value moves by <see cref="SolverFlip.Step"/> each tick: it flips at the first tick that
    /// crosses the other side (one variable: n = ceil((limit - x0) / step)), wrapping included for integers.</summary>
    Steps,
    /// <summary>After the first tick the value moves by <see cref="SolverFlip.Step"/> each tick (a piece that sets
    /// the value, such as a clamp's bound).</summary>
    StepsAfterFirstTick,
}

/// <summary>(Version 6.) A condition a piece applies under: <c>Left Comparison Right</c> evaluated (on the state at
/// the start of a tick) to <see cref="WhenTrue"/>, or an invariant condition (<see cref="Invariant"/>).</summary>
public sealed class SolverGuard
{
    internal SolverGuard() { }
    /// <summary>The comparison (or the condition) in the program.</summary>
    public required IOperation Operation { get; init; }
    public required bool WhenTrue { get; init; }
    /// <summary>LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual, Equals or NotEquals; null for an invariant
    /// condition.</summary>
    public BinaryOperatorKind? Comparison { get; init; }
    public SolverAffine? Left { get; internal set; }
    public SolverAffine? Right { get; internal set; }
    /// <summary>For a condition that does not depend on the state: its C#.</summary>
    public SolverAtom? Invariant { get; init; }
    internal Compiler.Solver.Lin? L, R;
    internal string Key = "";
    public override string ToString() => (WhenTrue ? "" : "!") + (Invariant != null ? "(" + Invariant.Code + ")" : $"({Left} {Op(Comparison!.Value)} {Right})");
    internal static string Op(BinaryOperatorKind k) => k switch
    {
        BinaryOperatorKind.LessThan => "<", BinaryOperatorKind.LessThanOrEqual => "<=", BinaryOperatorKind.GreaterThan => ">",
        BinaryOperatorKind.GreaterThanOrEqual => ">=", BinaryOperatorKind.Equals => "==", _ => "!=",
    };
}

/// <summary>(Version 6.) When a guard of a piece flips while that piece applies.</summary>
public sealed record SolverFlip(SolverGuard Guard, SolverFlipKind Kind, SolverTerm? Step, string Formula);

/// <summary>(Version 6.) One piece: while all its guards hold, a tick maps the state x to A x + b.</summary>
public sealed class SolverPiece
{
    internal SolverPiece() { }
    public required IReadOnlyList<SolverGuard> Guards { get; init; }
    /// <summary>The tick throws here (a clamp whose bounds are the wrong way round, Math.Abs of the smallest integer,
    /// a throw statement); the generated Advance runs that tick itself, so it throws as the program does.</summary>
    public bool Throws { get; init; }
    /// <summary>Row i: the new value of variable i (empty when the piece throws).</summary>
    public required IReadOnlyList<SolverAffine> Rows { get; init; }
    /// <summary>A[i][j]: the coefficient of the old x[j] in the new x[i].</summary>
    public IReadOnlyList<IReadOnlyList<SolverTerm>> A => Rows.Select(r => r.Coefficients).ToList();
    public IReadOnlyList<SolverTerm> B => Rows.Select(r => r.Constant).ToList();
    /// <summary>For each guard (in order), when it flips while this piece applies.</summary>
    public required IReadOnlyList<SolverFlip> Flips { get; init; }
    internal Compiler.Solver.Lin[]? Map;
    public override string ToString() => (Guards.Count == 0 ? "always" : string.Join(" && ", Guards)) + ": "
        + (Throws ? "throws" : string.Join("; ", Rows.Select((r, i) => "x" + i + "' = " + r)));
}

/// <summary>(Version 6.) The closed form of a per-tick method or a loop body (audit A4.6): from <c>PluginContext.Solve</c>.
/// When <see cref="Solved"/> is false, <see cref="Reason"/> says why (the first thing the solver could not follow, at
/// <see cref="ReasonAt"/>). <see cref="Exact"/>: every variable is an integer and every guard an integer comparison
/// (or an invariant): the generated Advance gives exactly the state that running the ticks gives, wrapping
/// included (powers of the affine map by repeated squaring, no geometric-sum division). Otherwise the closed form is
/// approximate: it computes in double and rounds differently from the ticks, so a display or a prediction may use
/// it, a simulation must run the ticks.</summary>
public sealed class ClosedForm
{
    internal ClosedForm() { }
    /// <summary>The per-tick method's body or the loop (null when the method has no body in source).</summary>
    public IOperation? Region { get; init; }
    /// <summary>The per-tick method, or the method the loop is in.</summary>
    public required IMethodSymbol Method { get; init; }
    /// <summary>Whether the region is a loop's body (else a per-tick method).</summary>
    public bool IsLoop { get; init; }
    public bool Solved { get; init; }
    public string? Reason { get; init; }
    public IOperation? ReasonAt { get; init; }
    public Location? ReasonLocation => ReasonAt?.Syntax.GetLocation();
    public bool Exact { get; init; }
    public IReadOnlyList<SolverVariable> Variables { get; init; } = [];
    public IReadOnlyList<SolverPiece> Pieces { get; init; } = [];
    /// <summary>The invariants the coefficients are made of.</summary>
    public IReadOnlyList<SolverAtom> Invariants { get; init; } = [];
    /// <summary>The generated Advance method's parameters before the state and n, as C# ("Game.Spawner self",
    /// "float dt"; for a loop, `ref` for each state local, then the invariant locals and parameters).</summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];
    internal Func<string, string>? Generate;

    /// <summary>C# for a static class named <paramref name="className"/> (in the global namespace) with
    /// <c>public static void Advance(..., long n)</c>: the state after n more ticks, the same as calling the per-tick
    /// method n times with the same arguments (or running the loop body n times), in time logarithmic in n for each
    /// stretch in one piece; a plugin adds it with <c>AddSource</c>. For a per-tick method the parameters are the
    /// receiver (<c>ref</c> for a struct), the method's parameters, then n; for a loop, <see cref="Parameters"/> then
    /// n. Null when not solved.</summary>
    public string? GenerateAdvance(string className = "ClosedFormAdvance") => Generate?.Invoke(className);

    public override string ToString() => Solved
        ? $"{(Exact ? "exact" : "approximate")}: " + string.Join(" | ", Pieces)
        : "not solved: " + Reason;
}
