using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;

namespace Cs2Wasm.Compiler;

/// <summary>
/// What reflection's runtime half asks of a type at run time, by type id (Cs2Wasm.Runtime.TypeInfo in the library):
/// its base, element type, rank, namespace, attributes, element kind, generic arguments and interfaces. Each is a
/// table function emitted only when the program reaches it; the types those tables name get ids too (closed over at
/// the start of Finish, before any per-type table is emitted).
/// </summary>
internal sealed partial class RuntimeFns
{
    readonly Dictionary<string, WasmFunction> _typeInfo = [];
    static readonly FuncType s_idToI32 = new([ValType.I32], [ValType.I32]);

    public WasmFunction TypeInfo(string kind) =>
        _typeInfo.TryGetValue(kind, out var f) ? f : _typeInfo[kind] = M.AddFunction("rt.type_" + kind + "_by_id", s_idToI32);

    public IReadOnlyDictionary<string, WasmFunction> TypeInfoFns => _typeInfo;
}

sealed partial class RuntimeGen
{
    /// <summary>Gives ids to the types the requested tables refer to.</summary>
    void CloseTypeInfo()
    {
        var kinds = D.Rt.TypeInfoFns.Keys.ToHashSet();
        if (kinds.Count == 0) return;
        for (int i = 1; i < D.TypeById.Count; i++)
        {
            if (D.SyntheticById.ContainsKey(i) || D.DisplayById.ContainsKey(i)) continue;
            foreach (var r in Related(D.TypeById[i], kinds)) D.TypeId(r);
        }
    }

    IEnumerable<ITypeSymbol> Related(ITypeSymbol t, HashSet<string> kinds)
    {
        if (kinds.Contains("base") && BaseOf(t) is { } b) yield return b;
        if (kinds.Contains("element") && ElementOf(t) is { } e) yield return e;
        if (kinds.Contains("generic_args") && t is INamedTypeSymbol { IsGenericType: true } g)
            foreach (var a in g.TypeArguments) if (a is not ITypeParameterSymbol) yield return D.Types.Canonical(a);
        if (kinds.Contains("declaring") && t.ContainingType is { } ct) yield return D.Types.Canonical(ct);
        if (kinds.Contains("interfaces"))
            foreach (var i in t.AllInterfaces) yield return D.Types.Canonical(i);
    }

    ITypeSymbol? BaseOf(ITypeSymbol t) => t switch
    {
        IArrayTypeSymbol => D.LibType("System.Array"),
        IPointerTypeSymbol => null,
        _ when t.TypeKind == TypeKind.Interface => null,
        _ when Types.IsObject(t) => null,
        _ when t.TypeKind == TypeKind.Enum => D.LibType("System.Enum"),
        _ => t.BaseType is { } b ? D.Types.Canonical(b) : null,
    };

    static ITypeSymbol? ElementOf(ITypeSymbol t) => t switch
    {
        IArrayTypeSymbol a => a.ElementType,
        IPointerTypeSymbol p => p.PointedAtType,
        _ => null,
    };

    void EmitTypeInfo()
    {
        int n = D.TypeById.Count;
        bool Real(int i) => i > 0 && !D.SyntheticById.ContainsKey(i) && !D.DisplayById.ContainsKey(i);
        IAddress Ids(IEnumerable<ITypeSymbol> types)
        {
            var ids = types.Select(x => D.TypeId(D.Types.Canonical(x))).ToList();
            return D.DataBlob([.. BitConverter.GetBytes(ids.Count), .. ids.SelectMany(BitConverter.GetBytes)], 4);
        }
        foreach (var (kind, fn) in D.Rt.TypeInfoFns)
        {
            Func<int, (IAddress?, int)> entry = kind switch
            {
                "base" => i => (null, Real(i) && BaseOf(D.TypeById[i]) is { } b ? D.TypeId(b) : -1),
                "element" => i => (null, Real(i) && ElementOf(D.TypeById[i]) is { } e ? D.TypeId(D.Types.Canonical(e)) : -1),
                "rank" => i => (null, Real(i) && D.TypeById[i] is IArrayTypeSymbol a ? a.Rank : 0),
                "namespace" => i => (D.StringLiteral(Real(i) ? NamespaceOf(D.TypeById[i]) : ""), 0),
                "attrs" => i => (null, Real(i) ? AttributesOf(D.TypeById[i]) : 0),
                "cor" => i => (null, Real(i) ? CorElementType(D.TypeById[i]) : 0x1C),
                "flags2" => i => (null, Real(i) ? Flags2(D.TypeById[i]) : 0),
                "generic_args" => i => (Real(i) && D.TypeById[i] is INamedTypeSymbol { IsGenericType: true } g ? Ids(g.TypeArguments) : Ids([]), 0),
                "interfaces" => i => (Ids(Real(i) ? D.TypeById[i].AllInterfaces : []), 0),
                "declaring" => i => (null, Real(i) && D.TypeById[i].ContainingType is { } ct ? D.TypeId(D.Types.Canonical(ct)) : -1),
                // a value type's size (the data of its box), an object's instance size
                "size" => i => (null, Real(i) && D.TypeById[i] is INamedTypeSymbol nt and not { TypeKind: TypeKind.Interface } ? (nt.IsValueType ? D.Types.StorageOf(nt).size : D.Types.LayoutOf(nt).Size) : 0),
                "box_offset" => i => (null, Real(i) && D.TypeById[i].IsValueType ? FunctionCompilerHelpers.BoxDataOffset(D.Types, D.TypeById[i]) : 0),
                _ => throw new CompileException("type info " + kind),
            };
            EmitIdTable(fn, n, entry);
        }
    }

    static string NamespaceOf(ITypeSymbol t)
    {
        while (t is IArrayTypeSymbol a) t = a.ElementType;
        while (t.ContainingType != null) t = t.ContainingType;
        return t.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "";
    }

    /// <summary>System.Reflection.TypeAttributes.</summary>
    static int AttributesOf(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol or IPointerTypeSymbol) return 0x1 | 0x100 | 0x2000; // Public | Sealed | Serializable
        int a = t.ContainingType == null
            ? (t.DeclaredAccessibility == Accessibility.Public ? 0x1 : 0x0)
            : t.DeclaredAccessibility switch
            {
                Accessibility.Public => 0x2, Accessibility.Private => 0x3, Accessibility.Protected => 0x4,
                Accessibility.Internal => 0x5, Accessibility.ProtectedAndInternal => 0x6, _ => 0x7,
            };
        if (t.TypeKind == TypeKind.Interface) a |= 0x20 | 0x80;
        if (t.IsAbstract && t.TypeKind != TypeKind.Interface) a |= 0x80;
        if (t.IsSealed || t.IsValueType || t.IsStatic) a |= 0x100;
        if (t.IsStatic) a |= 0x80;
        return a;
    }

    /// <summary>Mono's element kind of a type (MonoTypeEnum = CorElementType).</summary>
    static int CorElementType(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol a) return a.Rank == 1 ? 0x1D : 0x14;
        if (t is IPointerTypeSymbol) return 0x0F;
        if (Types.IsString(t)) return 0x0E;
        if (Types.IsObject(t)) return 0x1C;
        if (t.TypeKind != TypeKind.Enum)
            switch (Types.PrimOf(t))
            {
                case Prim.Bool: return 0x02; case Prim.Char: return 0x03; case Prim.I8: return 0x04; case Prim.U8: return 0x05;
                case Prim.I16: return 0x06; case Prim.U16: return 0x07; case Prim.I32: return 0x08; case Prim.U32: return 0x09;
                case Prim.I64: return 0x0A; case Prim.U64: return 0x0B; case Prim.F32: return 0x0C; case Prim.F64: return 0x0D;
                case Prim.IntPtr: return 0x18; case Prim.UIntPtr: return 0x19;
            }
        if (t is INamedTypeSymbol { IsGenericType: true }) return 0x15;
        if (Types.IsVoid(t)) return 0x01;
        return t.IsValueType ? 0x11 : 0x12;
    }

    int Flags2(ITypeSymbol t)
    {
        int f = 0;
        bool counted = t switch
        {
            IArrayTypeSymbol a => Types.IsRef(a.ElementType) || D.Types.NeedsRc(a.ElementType),
            INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Delegate } c => D.Types.LayoutOf(c).HasRefs,
            _ => t.IsValueType && D.Types.NeedsRc(t),
        };
        if (counted) f |= 1;
        if (t.IsRefLikeType) f |= 2;
        if (t is INamedTypeSymbol { IsGenericType: true }) f |= 4;
        if (t.ContainingType != null) f |= 8;
        if (t.TypeKind == TypeKind.Delegate) f |= 16;
        if (t is IPointerTypeSymbol) f |= 32;
        return f;
    }
}
