using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>
/// `string.Format` with a format string known at compile time (a literal, or a constant argument of a specialized
/// copy: `SR.Format(SR.Arg_X, a)` in upstream's throw paths) is lowered like an interpolated string: the literal parts
/// and each item's text are concatenated, and the composite-format parser is not compiled for it. Items are formatted
/// as `string.Format` formats them without a provider: an IFormattable's `ToString(null, null)` (a call with constant
/// arguments, so its implementations' fast paths are what is compiled), otherwise `ToString()`, null as "". A format
/// with alignment, an item format or a malformed hole is left to `string.Format`. Arguments are evaluated once, in
/// order, as the call would.
/// </summary>
sealed partial class FunctionCompiler
{
    Own? TryLowerStringFormat(IMethodSymbol m, IReadOnlyList<IOperation> args)
    {
        if (m.Name != "Format" || !m.IsStatic || m.ContainingType.SpecialType != SpecialType.System_String) return null;
        var ps = m.Parameters;
        int fi = ps.Length > 0 && ps[0].Name == "format" ? 0 : ps.Length > 1 && ps[1].Name == "format" && ps[0].Type.Name == "IFormatProvider" ? 1 : -1;
        if (fi < 0 || ps[fi].Type.SpecialType != SpecialType.System_String) return null;
        var byParam = new IOperation?[ps.Length];
        foreach (var a in args) if (a is IArgumentOperation { Parameter: { } p } ao) byParam[p.Ordinal] = ao.Value;
        if (byParam.Any(v => v == null)) return null;
        // a provider other than null may be an ICustomFormatter: leave it to string.Format
        if (fi == 1 && !IsNullLiteral(byParam[0]!) && Bound(byParam[0]!) is not { Kind: ConstArg.Null }) return null;
        string? format = byParam[fi]!.ConstantValue is { HasValue: true, Value: string s } ? s : Bound(byParam[fi]!) is { Kind: ConstArg.Value_, Value: string bs } ? bs : null;
        if (format == null) return null;
        // the items: object parameters, or a params object[] created at the call
        var items = new List<IOperation>();
        for (int i = fi + 1; i < ps.Length; i++)
        {
            if (ps[i].Type.SpecialType == SpecialType.System_Object) { items.Add(byParam[i]!); continue; }
            if (ps[i].IsParams && ps[i].Type is IArrayTypeSymbol && byParam[i] is IArrayCreationOperation { Initializer: { } init }) { items.AddRange(init.ElementValues); continue; }
            return null;
        }
        if (Parse(format, items.Count) is not { } parts) return null;

        var obj = D.ObjectType;
        var str = D.Main.GetSpecialType(SpecialType.System_String);
        // the arguments, once and in order
        if (fi == 1) Drop(EmitValue(byParam[0]!), null);
        var locals = new int[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var o = EmitValueAs(items[i], obj);
            ToCallSafe(o, obj);
            locals[i] = E.AddLocal(ValType.I32);
            E.Set(locals[i]);
        }
        var formatItem = D.LibMethod("Cs2Wasm.Runtime.Rt", "FormatItem", 1).Sym;
        // the pieces, as strings each owned by a temporary
        var pieces = new List<Action>();
        foreach (var (text, item) in parts)
        {
            if (item < 0) { var t = text; pieces.Add(() => E.I32Addr(D.StringLiteral(t))); continue; }
            var li = locals[item];
            pieces.Add(() =>
            {
                var r = CallWith(formatItem, false, null, () => E.Get(li), null, null);
                ToCallSafe(r, str);
            });
        }
        if (pieces.Count == 0) { E.I32Addr(D.StringLiteral("")); return Own.Stable; }
        if (pieces.Count == 1 && parts[0].item < 0) { pieces[0](); return Own.Stable; }
        // string.Concat of up to four, then of the result and the next three
        Own acc = Own.None;
        int idx = 0;
        bool first = true;
        while (idx < pieces.Count)
        {
            int take = Math.Min(first ? 4 : 3, pieces.Count - idx);
            int n = take + (first ? 0 : 1);
            if (n == 1) { pieces[idx](); acc = Own.Stable; idx++; first = false; continue; }
            var concat = D.LibType("System.String").GetMembers("Concat").OfType<IMethodSymbol>()
                .First(c => c.Parameters.Length == n && c.Parameters.All(p => p.Type.SpecialType == SpecialType.System_String));
            int from = idx;
            bool firstRound = first;
            Own prev = acc;
            acc = CallWith(concat, false, null, () =>
            {
                if (!firstRound) ToCallSafe(prev, str);
                for (int k = from; k < from + take; k++) pieces[k]();
            }, null, null);
            idx += take;
            first = false;
        }
        return acc;
    }

    /// <summary>The literal parts (item -1) and items of a composite format, or null when it needs string.Format.</summary>
    static List<(string text, int item)>? Parse(string f, int count)
    {
        var parts = new List<(string, int)>();
        var lit = new System.Text.StringBuilder();
        for (int i = 0; i < f.Length; i++)
        {
            char c = f[i];
            if (c == '{')
            {
                if (i + 1 < f.Length && f[i + 1] == '{') { lit.Append('{'); i++; continue; }
                int j = i + 1, n = 0, digits = 0;
                while (j < f.Length && f[j] >= '0' && f[j] <= '9' && digits < 6) { n = n * 10 + (f[j] - '0'); j++; digits++; }
                if (digits == 0 || j >= f.Length || f[j] != '}' || n >= count) return null;
                if (lit.Length > 0) { parts.Add((lit.ToString(), -1)); lit.Clear(); }
                parts.Add(("", n));
                i = j;
                continue;
            }
            if (c == '}')
            {
                if (i + 1 < f.Length && f[i + 1] == '}') { lit.Append('}'); i++; continue; }
                return null;
            }
            lit.Append(c);
        }
        if (lit.Length > 0) parts.Add((lit.ToString(), -1));
        return parts;
    }
}
