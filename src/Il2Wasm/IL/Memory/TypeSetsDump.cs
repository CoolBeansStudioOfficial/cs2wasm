#nullable enable
using System;
using System.Linq;
using System.Text;

namespace Il2Wasm.IL.Memory;

public sealed partial class ProgramSolution
{
    /// <summary>IL2WASM_TYPESETS_DUMPALL=file: every type set the solve found (variables, then values by method and
    /// node), sorted, appended per solve: two builds of the solver must give the same file.</summary>
    internal void DumpTypeSets()
    {
        if (!TypeSetsSolved || Environment.GetEnvironmentVariable("IL2WASM_TYPESETS_DUMPALL") is not { Length: > 0 } file || _ts is not { } st) return;
        string Set(IdSet? s) => s is { } x ? "{" + string.Join(",", x.Items.ToArray().Select(t => StableKey.Of(_all![t])).OrderBy(n => n, StringComparer.Ordinal)) + "}" : "?";
        var lines = st.Vars.Select((v, i) =>
        {
            string name = v.Kind switch
            {
                VarKind.Key => "key " + v.Key,
                VarKind.AddrParam or VarKind.AddrRet => v.Kind + " " + StableKey.Of(v.Method!) + " #" + v.Index + " = " + (st.Sets[i] is { } a ? "{" + string.Join(",", a.Items.ToArray().Select(k => Keys[k].ToString()).OrderBy(n => n, StringComparer.Ordinal)) + "}" : "?"),
                _ => v.Kind + " " + StableKey.Of(v.Method!) + " #" + v.Index,
            };
            return v.Kind is VarKind.AddrParam or VarKind.AddrRet ? name : name + " = " + Set(st.Sets[i]);
        }).Concat(_methodTypes.SelectMany(e => Enumerable.Range(0, e.Value.Types.Length).Where(i => e.Value.Has[i]).Select(i => "node " + StableKey.Of(e.Key) + " v" + i + " = " + Set(e.Value.Types[i]))))
          .Concat(_methodTypes.SelectMany(e => (e.Value.Targets ?? []).Select(p => "targets " + StableKey.Of(e.Key) + " v" + p.Key + " = " + string.Join(",", p.Value.Select(StableKey.Of).OrderBy(n => n, StringComparer.Ordinal)))))
          .OrderBy(l => l, StringComparer.Ordinal);
        var sb = new StringBuilder("# type sets\n");
        foreach (var l in lines) sb.Append(l).Append('\n');
        System.IO.File.AppendAllText(file, sb.ToString());
    }
}
