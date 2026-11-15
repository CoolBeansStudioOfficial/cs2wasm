using Microsoft.CodeAnalysis;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Constructed types indexed by what they may be subtypes of, so that "which of these types are subtypes of T" looks
/// at a superset of the answer instead of at every type (Types.IsSubtype still decides). A type is listed under the
/// key of itself, of each base type and of each interface, and (for variance) under the generic definitions of those;
/// arrays, closure objects and synthetic types are listed apart (special), for every query to look at. Lists only
/// grow, in the order types were added: a caller can resume a list where it stopped.
/// </summary>
sealed class SubtypeIndex(Types types)
{
    readonly Dictionary<string, List<int>> _bySuper = [];
    readonly Dictionary<string, List<int>> _byDef = [];
    readonly HashSet<int> _added = [];
    public readonly List<int> All = [];
    public readonly List<int> Special = [];

    /// <summary>Adds a type (null: a special one).</summary>
    public bool Add(int id, ITypeSymbol? t)
    {
        if (!_added.Add(id)) return false;
        All.Add(id);
        if (t == null) { Special.Add(id); return true; }
        var defs = new HashSet<string>();
        foreach (var k in types.SupertypeKeys(t).Append(Types.Key(types.Canonical(t))).Distinct()) Get(_bySuper, k).Add(id);
        foreach (var s in Types.BaseChain(t).Cast<ITypeSymbol>().Concat(t.AllInterfaces))
            if (s is INamedTypeSymbol { IsGenericType: true } g) defs.Add(Types.Key(types.Canonical(g.OriginalDefinition)));
        foreach (var k in defs) Get(_byDef, k).Add(id);
        return true;
    }

    static List<int> Get(Dictionary<string, List<int>> d, string k)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = [];
        return l;
    }

    /// <summary>The (non-special) types that may be subtypes of t: a list that keeps growing as types are added.</summary>
    public List<int> Candidates(ITypeSymbol t)
    {
        if (Types.IsObject(t) || t is not INamedTypeSymbol n) return All;
        if (n is { IsGenericType: true, TypeKind: TypeKind.Interface or TypeKind.Delegate } && n.OriginalDefinition.TypeParameters.Any(p => p.Variance != VarianceKind.None))
            return Get(_byDef, Types.Key(types.Canonical(n.OriginalDefinition)));
        return Get(_bySuper, Types.Key(types.Canonical(n)));
    }
}
