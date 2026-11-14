using System.Collections.Concurrent;
using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cs2Wasm.Compiler;

internal enum Prim { None, Bool, Char, I8, U8, I16, U16, I32, U32, I64, U64, F32, F64, IntPtr, UIntPtr }

/// <summary>Substitution of type parameters for one method instance (monomorphization).</summary>
internal sealed class TypeMap
{
    public static readonly TypeMap Empty = new([]);
    public readonly Dictionary<ITypeParameterSymbol, ITypeSymbol> Map;
    public TypeMap(Dictionary<ITypeParameterSymbol, ITypeSymbol> m) { Map = m; }

    public static TypeMap For(IMethodSymbol m)
    {
        var d = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        for (var t = m.ContainingType; t != null; t = t.ContainingType)
            for (int i = 0; i < t.TypeArguments.Length; i++)
                d[t.OriginalDefinition.TypeParameters[i]] = t.TypeArguments[i];
        for (int i = 0; i < m.TypeArguments.Length; i++)
            d[m.OriginalDefinition.TypeParameters[i]] = m.TypeArguments[i];
        // Reduced extension/local functions may carry their own type parameters too.
        return new TypeMap(d);
    }

    public TypeMap With(TypeMap inner)
    {
        var d = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(Map, SymbolEqualityComparer.Default);
        foreach (var kv in inner.Map) d[kv.Key] = kv.Value;
        return new TypeMap(d);
    }
}

internal sealed class FieldSlot
{
    public required ISymbol Symbol; // IFieldSymbol, or the IPropertySymbol/IEventSymbol owning the storage
    public required ITypeSymbol Type;
    public int Offset;
}

internal sealed class Layout
{
    public int Size;
    public int Align = 1;
    public List<FieldSlot> Fields = [];
    public bool HasRefs; // contains counted references (directly or in nested structs)
    public FieldSlot? Find(ISymbol s) => Fields.FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.Symbol.OriginalDefinition, s.OriginalDefinition));
}

/// <summary>Type identity, lowering of C# types to WASM types, and object/struct layout.</summary>
internal sealed class Types
{
    public const int ObjectHeader = 12; // rc, typeId, hash
    public const int ArrayData = 16;    // header + length
    /// <summary>Where elements start: one-dimensional arrays at 16; others after their dimensions (from 16), 8-aligned.</summary>
    public static int ArrayDataOffset(int rank) => rank == 1 ? ArrayData : (ArrayData + 4 * rank + 7) / 8 * 8;
    readonly Driver _d;
    readonly ConcurrentDictionary<string, Layout> _layouts = new();
    readonly HashSet<string> _layoutInProgress = [];

    public Types(Driver d) { _d = d; }

    // ---------------- keys ----------------
    public static string Key(ITypeSymbol t)
    {
        switch (t)
        {
            case IArrayTypeSymbol a: return Key(a.ElementType) + "[" + new string(',', a.Rank - 1) + "]";
            case IPointerTypeSymbol p: return Key(p.PointedAtType) + "*";
            case IFunctionPointerTypeSymbol: return "fnptr";
            case ITypeParameterSymbol tp: return "!" + tp.Name;
            case INamedTypeSymbol n:
                {
                    if (n.IsTupleType && n.TupleUnderlyingType is { } u && !SymbolEqualityComparer.Default.Equals(u, n)) return Key(u);
                    string baseName = n.ContainingType != null ? Key(n.ContainingType) + "+" : Ns(n.ContainingNamespace);
                    baseName += n.MetadataName;
                    if (n.TypeArguments.Length > 0) baseName += "<" + string.Join(",", n.TypeArguments.Select(Key)) + ">";
                    return baseName;
                }
            case IDynamicTypeSymbol: return "System.Object";
            default: return t.ToDisplayString();
        }
    }

    static string Ns(INamespaceSymbol? ns) => ns == null || ns.IsGlobalNamespace ? "" : Ns(ns.ContainingNamespace) + ns.Name + ".";

    /// <summary>
    /// A member's identity independent of which compilation declares it (reference assembly or cs2wasm's library):
    /// kind, containing type, name, arity and parameter types. (Roslyn's documentation ids differ between the two for
    /// some nested generic types, so they are not used for matching.)
    /// </summary>
    public static string SigId(ISymbol s)
    {
        s = s.OriginalDefinition;
        var sb = new System.Text.StringBuilder();
        switch (s)
        {
            case IMethodSymbol m:
                sb.Append("M:").Append(TypeSig(m.ContainingType)).Append('.').Append(m.MetadataName);
                sb.Append('(');
                for (int i = 0; i < m.Parameters.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    var p = m.Parameters[i];
                    if (p.RefKind != RefKind.None) sb.Append('&');
                    sb.Append(TypeSig(p.Type));
                }
                sb.Append(')');
                if (m.MethodKind == MethodKind.Conversion) sb.Append('~').Append(TypeSig(m.ReturnType));
                return sb.ToString();
            case IPropertySymbol p:
                sb.Append("P:").Append(TypeSig(p.ContainingType)).Append('.').Append(p.MetadataName);
                if (p.Parameters.Length > 0) sb.Append('(').Append(string.Join(",", p.Parameters.Select(x => TypeSig(x.Type)))).Append(')');
                return sb.ToString();
            case INamedTypeSymbol t: return "T:" + TypeSig(t);
            default: return (s.Kind.ToString()[0]) + ":" + (s.ContainingType != null ? TypeSig(s.ContainingType) + "." : "") + s.MetadataName;
        }
    }

    static string TypeSig(ITypeSymbol t) => t switch
    {
        ITypeParameterSymbol tp => tp.TypeParameterKind == TypeParameterKind.Method ? "!!" + tp.Ordinal : "!" + tp.Ordinal + "@" + tp.DeclaringType?.MetadataName,
        IArrayTypeSymbol a => TypeSig(a.ElementType) + "[" + new string(',', a.Rank - 1) + "]",
        IPointerTypeSymbol p => TypeSig(p.PointedAtType) + "*",
        INamedTypeSymbol n => (n.ContainingType != null ? TypeSig(n.ContainingType.OriginalDefinition) + "+" : Ns(n.ContainingNamespace)) + n.MetadataName
                              + (n.TypeArguments.Length > 0 && !SymbolEqualityComparer.Default.Equals(n, n.OriginalDefinition) ? "<" + string.Join(",", n.TypeArguments.Select(TypeSig)) + ">" : ""),
        IFunctionPointerTypeSymbol => "fnptr",
        _ => t.ToDisplayString(),
    };

    public static string MethodKey(IMethodSymbol m)
    {
        var def = m.OriginalDefinition;
        var id = SigId(def);
        int paren = id.IndexOf('(');
        string sig = paren >= 0 ? id[paren..] : "";
        string name = def.MetadataName;
        if (def.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction)
            name += "@" + LocationKey(def);
        return (m.ContainingType != null ? Key(m.ContainingType) : "?") + "::" + name + sig +
               (m.TypeArguments.Length > 0 ? "<" + string.Join(",", m.TypeArguments.Select(Key)) + ">" : "");
    }

    static string LocationKey(ISymbol s)
    {
        var loc = s.Locations.FirstOrDefault();
        return loc == null ? "" : loc.SourceTree?.FilePath + ":" + loc.SourceSpan.Start;
    }

    public static string DocId(ISymbol s) => s is INamedTypeSymbol ? DocumentationCommentId.CreateDeclarationId(s.OriginalDefinition) ?? s.ToDisplayString() : SigId(s);
    /// <summary>The documentation comment id (used by the embedder's boundary table).</summary>
    public static string CommentId(ISymbol s)
    {
        var id = DocumentationCommentId.CreateDeclarationId(s.OriginalDefinition) ?? s.ToDisplayString();
        // Roslyn may append "~ReturnType"; the boundary table names methods without it (except conversion operators)
        return StripReturn(id, s is IMethodSymbol { MethodKind: MethodKind.Conversion });
    }

    public static string StripReturn(string id, bool keep = false)
    {
        if (keep) return id;
        int t = id.IndexOf('~', id.IndexOf('(') < 0 ? 0 : id.LastIndexOf(')'));
        return t > 0 ? id[..t] : id;
    }

    // ---------------- substitution + canonicalization ----------------
    public ITypeSymbol Resolve(ITypeSymbol t, TypeMap map) => Canonical(Substitute(t, map));

    public ITypeSymbol Substitute(ITypeSymbol t, TypeMap map)
    {
        switch (t)
        {
            case ITypeParameterSymbol tp:
                if (map.Map.TryGetValue(tp, out var r)) return r;
                if (map.Map.TryGetValue((ITypeParameterSymbol)tp.OriginalDefinition, out r)) return r;
                // A reduced method's type parameter maps onto its definition's.
                if (tp.ReducedFrom is { } rf && map.Map.TryGetValue(rf, out r)) return r;
                return t;
            case IArrayTypeSymbol a:
                return _d.LibCompilation.CreateArrayTypeSymbol(Substitute(a.ElementType, map), a.Rank);
            case IPointerTypeSymbol p:
                return _d.LibCompilation.CreatePointerTypeSymbol(Substitute(p.PointedAtType, map));
            case INamedTypeSymbol n:
                {
                    if (!HasTypeParams(n)) return n;
                    if (n.IsTupleType && n.TupleUnderlyingType is { } u && !SymbolEqualityComparer.Default.Equals(u, n)) n = u;
                    INamedTypeSymbol baseT;
                    if (n.ContainingType != null && HasTypeParams(n.ContainingType))
                    {
                        var c = (INamedTypeSymbol)Substitute(n.ContainingType, map);
                        baseT = c.GetTypeMembers(n.Name, n.Arity).First();
                    }
                    else baseT = n.OriginalDefinition;
                    if (n.Arity == 0) return baseT;
                    return baseT.Construct(n.TypeArguments.Select(a => Substitute(a, map)).ToArray());
                }
            default: return t;
        }
    }

    static bool HasTypeParams(ITypeSymbol t) => t switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol a => HasTypeParams(a.ElementType),
        IPointerTypeSymbol p => HasTypeParams(p.PointedAtType),
        INamedTypeSymbol n => n.TypeArguments.Any(HasTypeParams) || (n.ContainingType != null && HasTypeParams(n.ContainingType)),
        _ => false,
    };

    readonly Dictionary<string, ITypeSymbol> _canon = [];

    /// <summary>Maps library types that came from reference assemblies onto cs2wasm's library sources.</summary>
    public ITypeSymbol Canonical(ITypeSymbol t)
    {
        if (t is IDynamicTypeSymbol) return _d.ObjectType;
        if (t is IArrayTypeSymbol a) return _d.LibCompilation.CreateArrayTypeSymbol(Canonical(a.ElementType), a.Rank);
        if (t is IPointerTypeSymbol p) return _d.LibCompilation.CreatePointerTypeSymbol(Canonical(p.PointedAtType));
        if (t is not INamedTypeSymbol n) return t;
        if (n.IsTupleType && n.TupleUnderlyingType is { } tu && !SymbolEqualityComparer.Default.Equals(tu, n)) n = tu;
        var key = Key(n);
        lock (_canon)
            if (_canon.TryGetValue(key, out var c)) return c;
        ITypeSymbol result;
        if (n.ContainingType != null)
        {
            var cont = (INamedTypeSymbol)Canonical(n.ContainingType);
            var member = cont.GetTypeMembers(n.Name, n.Arity).FirstOrDefault() ?? n.OriginalDefinition;
            result = n.Arity > 0 ? member.Construct(n.TypeArguments.Select(Canonical).ToArray()) : member;
        }
        else
        {
            var def = n.OriginalDefinition;
            var lib = _d.Lib.FindType(def) ?? def;
            result = n.Arity > 0 ? lib.Construct(n.TypeArguments.Select(Canonical).ToArray()) : lib;
        }
        lock (_canon) _canon[key] = result;
        return result;
    }

    public static string FullName(ITypeSymbol t) => t is INamedTypeSymbol n && n.ContainingType == null ? Ns(n.ContainingNamespace) + n.MetadataName : Key(t.OriginalDefinition);

    // ---------------- classification ----------------
    public static Prim PrimOf(ITypeSymbol t)
    {
        if (t.TypeKind == TypeKind.Enum && t is INamedTypeSymbol e) return PrimOf(e.EnumUnderlyingType!);
        if (t is IPointerTypeSymbol or IFunctionPointerTypeSymbol) return Prim.U32;
        if (t is not INamedTypeSymbol n || n.ContainingType != null || n.Arity != 0) return Prim.None;
        if (n.ContainingNamespace?.Name != "System" || n.ContainingNamespace.ContainingNamespace?.IsGlobalNamespace != true) return Prim.None;
        return n.MetadataName switch
        {
            "Boolean" => Prim.Bool, "Char" => Prim.Char, "SByte" => Prim.I8, "Byte" => Prim.U8,
            "Int16" => Prim.I16, "UInt16" => Prim.U16, "Int32" => Prim.I32, "UInt32" => Prim.U32,
            "Int64" => Prim.I64, "UInt64" => Prim.U64, "Single" => Prim.F32, "Double" => Prim.F64,
            "IntPtr" => Prim.IntPtr, "UIntPtr" => Prim.UIntPtr,
            _ => Prim.None,
        };
    }

    public static bool IsVoid(ITypeSymbol t) => t.SpecialType == SpecialType.System_Void || (t.Name == "Void" && t.ContainingNamespace?.Name == "System");

    public static bool IsRef(ITypeSymbol t) => t.TypeKind is TypeKind.Class or TypeKind.Interface or TypeKind.Array or TypeKind.Delegate or TypeKind.Dynamic
                                              || (t is ITypeParameterSymbol tp && tp.IsReferenceType);

    public static bool IsStruct(ITypeSymbol t) => t.TypeKind == TypeKind.Struct && PrimOf(t) == Prim.None && !IsVoid(t);

    public static bool IsString(ITypeSymbol t) => t.SpecialType == SpecialType.System_String || FullName(t) == "System.String";
    public static bool IsObject(ITypeSymbol t) => t.SpecialType == SpecialType.System_Object || FullName(t) == "System.Object";

    public static bool IsSigned(Prim p) => p is Prim.I8 or Prim.I16 or Prim.I32 or Prim.I64 or Prim.IntPtr;
    public static bool IsFloat(Prim p) => p is Prim.F32 or Prim.F64;
    public static bool Is64(Prim p) => p is Prim.I64 or Prim.U64;

    public static ValType WasmPrim(Prim p) => p switch
    {
        Prim.I64 or Prim.U64 => ValType.I64,
        Prim.F32 => ValType.F32,
        Prim.F64 => ValType.F64,
        _ => ValType.I32,
    };

    /// <summary>WASM type of a value of this C# type on the operand stack (structs: their address).</summary>
    public static ValType? WasmType(ITypeSymbol t)
    {
        if (IsVoid(t)) return null;
        var p = PrimOf(t);
        if (p != Prim.None) return WasmPrim(p);
        return ValType.I32;
    }

    public static int PrimSize(Prim p) => p switch
    {
        Prim.Bool or Prim.I8 or Prim.U8 => 1,
        Prim.Char or Prim.I16 or Prim.U16 => 2,
        Prim.I64 or Prim.U64 or Prim.F64 => 8,
        _ => 4,
    };

    /// <summary>Size and alignment of a value of this type when stored in memory (field, element, local slot).</summary>
    public (int size, int align) StorageOf(ITypeSymbol t)
    {
        var p = PrimOf(t);
        if (p != Prim.None) { var s = PrimSize(p); return (s, s); }
        if (IsStruct(t)) { var l = LayoutOf(t); return (l.Size, l.Align); }
        return (4, 4);
    }

    public bool NeedsRc(ITypeSymbol t) => IsRef(t) || (IsStruct(t) && LayoutOf(t).HasRefs);

    public static Op LoadOp(ITypeSymbol t) => LoadOp(PrimOf(t) is var p && p != Prim.None ? p : Prim.I32);
    public static Op LoadOp(Prim p) => p switch
    {
        Prim.Bool or Prim.U8 => Op.I32Load8U,
        Prim.I8 => Op.I32Load8S,
        Prim.Char or Prim.U16 => Op.I32Load16U,
        Prim.I16 => Op.I32Load16S,
        Prim.I64 or Prim.U64 => Op.I64Load,
        Prim.F32 => Op.F32Load,
        Prim.F64 => Op.F64Load,
        _ => Op.I32Load,
    };
    public static Op StoreOp(ITypeSymbol t) => StoreOp(PrimOf(t) is var p && p != Prim.None ? p : Prim.I32);
    public static Op StoreOp(Prim p) => p switch
    {
        Prim.Bool or Prim.U8 or Prim.I8 => Op.I32Store8,
        Prim.Char or Prim.U16 or Prim.I16 => Op.I32Store16,
        Prim.I64 or Prim.U64 => Op.I64Store,
        Prim.F32 => Op.F32Store,
        Prim.F64 => Op.F64Store,
        _ => Op.I32Store,
    };

    // ---------------- layout ----------------
    public Layout LayoutOf(ITypeSymbol t)
    {
        t = Canonical(t);
        var key = Key(t);
        if (_layouts.TryGetValue(key, out var l)) return l;
        lock (_layoutInProgress)
        {
            if (_layouts.TryGetValue(key, out l)) return l;
            if (!_layoutInProgress.Add(key)) throw new CompileException("struct layout cycle in " + key);
            try { l = Compute((INamedTypeSymbol)t); }
            finally { _layoutInProgress.Remove(key); }
            _layouts[key] = l;
            return l;
        }
    }

    public static bool IsInstanceStorage(ISymbol m) => m is IFieldSymbol f && !f.IsStatic && !f.IsConst;

    Layout Compute(INamedTypeSymbol t)
    {
        var l = new Layout();
        var p = PrimOf(t);
        if (p != Prim.None)
        {
            l.Size = l.Align = PrimSize(p);
            foreach (var f in t.GetMembers().Where(IsInstanceStorage))
                l.Fields.Add(new FieldSlot { Symbol = f, Type = t, Offset = 0 });
            return l;
        }
        if (t.TypeKind == TypeKind.Enum)
        {
            l.Size = l.Align = PrimSize(PrimOf(t));
            return l;
        }
        int offset = 0;
        if (t.TypeKind is TypeKind.Class or TypeKind.Delegate)
        {
            if (t.BaseType != null && !IsObject(t.BaseType))
            {
                var b = LayoutOf(t.BaseType);
                l.Fields.AddRange(b.Fields);
                offset = b.Size;
                l.HasRefs = b.HasRefs;
            }
            else offset = ObjectHeader;
            l.Align = 8;
        }
        foreach (var m in t.GetMembers())
        {
            if (!IsInstanceStorage(m)) continue;
            var f = (IFieldSymbol)m;
            var ft = Canonical(f.Type);
            var (size, align) = StorageOf(ft);
            if (f.IsFixedSizeBuffer) size *= Math.Max(1, f.FixedSize);
            offset = (offset + align - 1) / align * align;
            l.Fields.Add(new FieldSlot { Symbol = f, Type = ft, Offset = offset });
            offset += size;
            l.Align = Math.Max(l.Align, align);
            if (NeedsRc(ft)) l.HasRefs = true;
        }
        // primary constructor parameters (C# 12 classes and structs): captured state
        foreach (var pc in t.InstanceConstructors.Where(Driver.IsPrimaryCtor))
            foreach (var pp in pc.Parameters)
            {
                var pt = Canonical(pp.Type);
                var (psize, palign) = StorageOf(pt);
                offset = (offset + palign - 1) / palign * palign;
                l.Fields.Add(new FieldSlot { Symbol = pp.OriginalDefinition, Type = pt, Offset = offset });
                offset += psize;
                l.Align = Math.Max(l.Align, palign);
                if (NeedsRc(pt)) l.HasRefs = true;
            }
        // field-like events: storage for the delegate
        foreach (var ev in t.GetMembers().OfType<IEventSymbol>())
        {
            if (ev.IsStatic || !Driver.IsFieldLikeEvent(ev)) continue;
            var et = Canonical(ev.Type);
            offset = (offset + 3) / 4 * 4;
            l.Fields.Add(new FieldSlot { Symbol = ev, Type = et, Offset = offset });
            offset += 4;
            l.Align = Math.Max(l.Align, 4);
            l.HasRefs = true;
        }
        if (t.TypeKind == TypeKind.Struct)
        {
            if (l.Fields.Count == 0 && offset == 0) offset = 1; // empty struct is one byte, as in .NET
            offset = (offset + l.Align - 1) / l.Align * l.Align;
        }
        l.Size = offset;
        return l;
    }

    public FieldSlot Field(IFieldSymbol f, TypeMap map) => FieldIn(Resolve(f.ContainingType, map), f);

    public FieldSlot FieldIn(ITypeSymbol owner, IFieldSymbol f)
    {
        if (f.CorrespondingTupleField is { } tf) f = tf;
        owner = Canonical(owner);
        var l = LayoutOf(owner);
        var slot = l.Find(f) ?? l.Fields.FirstOrDefault(x => x.Symbol.Name == f.Name && DocId(x.Symbol) == DocId(f));
        return slot ?? throw new CompileException($"field {f.ToDisplayString()} has no storage in {Key(owner)}");
    }

    // ---------------- hierarchy ----------------
    public static IEnumerable<INamedTypeSymbol> BaseChain(ITypeSymbol t)
    {
        for (var b = t as INamedTypeSymbol; b != null; b = b.BaseType) yield return b;
    }

    public bool IsSubtype(ITypeSymbol c, ITypeSymbol target)
    {
        if (IsObject(target)) return true;
        var tk = Key(Canonical(target));
        if (Key(Canonical(c)) == tk) return true;
        if (c is IArrayTypeSymbol ca)
        {
            if (target is IArrayTypeSymbol ta)
                return ca.Rank == ta.Rank && (Key(ca.ElementType) == Key(ta.ElementType) || (IsRef(ca.ElementType) && IsRef(ta.ElementType) && IsSubtype(ca.ElementType, ta.ElementType)));
            var fn = FullName(target);
            if (fn is "System.Array" or "System.Collections.IEnumerable" or "System.Collections.ICollection" or "System.Collections.IList" or "System.ICloneable") return true;
            if (ca.Rank == 1 && target is INamedTypeSymbol nt && nt.Arity == 1 && nt.TypeKind == TypeKind.Interface &&
                (fn.StartsWith("System.Collections.Generic.I") || fn.StartsWith("System.Collections.Generic.IReadOnly")))
                return Key(Canonical(nt.TypeArguments[0])) == Key(Canonical(ca.ElementType));
            return false;
        }
        if (c.TypeKind == TypeKind.Delegate && (FullName(target) is "System.Delegate" or "System.MulticastDelegate")) return true;
        if (c.TypeKind == TypeKind.Enum && FullName(target) is "System.Enum" or "System.ValueType") return true;
        if (c.IsValueType && FullName(target) == "System.ValueType") return true;
        foreach (var b in BaseChain(c)) if (Key(Canonical(b)) == tk) return true;
        foreach (var i in c.AllInterfaces) if (Key(Canonical(i)) == tk) return true;
        return false;
    }
}

internal sealed class CompileException(string message, Location? location = null) : Exception(message)
{
    public Location? Location { get; } = location;
}
