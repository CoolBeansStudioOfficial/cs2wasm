using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Init-only fields (section 4.1, with section 4.5's threads): a `readonly` field of a class is written only by its
/// type's constructors and initializers. When every way of constructing an object writes it at most once (its
/// initializer, then the constructor chain), its first write replaces null and nothing writes it again: from then on the
/// field holds the same object for the containing object's life, on every thread. A borrow from such a field (a local
/// that is never reassigned, a temporary held across a call) needs no count, whatever the code it runs meanwhile, and
/// in a program with threads too: no thread can change the field. Decided from the source, at compile time.
/// </summary>
internal sealed partial class Driver
{
    readonly Dictionary<string, IFieldSymbol> _borrowFields = [];
    readonly Dictionary<IFieldSymbol, bool> _initOnly = new(SymbolEqualityComparer.Default);
    static readonly bool s_noInitOnly = Environment.GetEnvironmentVariable("CS2WASM_NO_INIT_ONLY") == "1";

    /// <summary>The field a borrow key names (FieldKey), for InitOnly.</summary>
    internal void NoteBorrowField(string key, ISymbol storage, ITypeSymbol? type = null)
    {
        if (storage is IFieldSymbol f) _borrowFields.TryAdd(key, f);
        if (type != null) _borrowFieldTypes.TryAdd(key, type);
    }
    /// <summary>Whether a borrow key names a static field.</summary>
    internal bool IsStaticKey(string key) => _borrowFields.TryGetValue(key, out var f) && f.IsStatic;
    /// <summary>The name of the function a type's static initializer's code is in, before the type's key.</summary>
    internal const string StaticInitBodyPrefix = "static init body ";
    /// <summary>The type of the field a borrow key names, in the instance the key is of.</summary>
    readonly Dictionary<string, ITypeSymbol> _borrowFieldTypes = [];

    /// <summary>Whether the field a key names is init-only (asked once the whole program is compiled: a field that is
    /// not `readonly` qualifies when only its own type's constructors write it).</summary>
    internal bool InitOnlyKey(string key)
    {
        if (s_noInitOnly || !_borrowFields.TryGetValue(key, out var f) || !OncePerConstruction(f)) return false;
        if (FieldsAddressTaken.Contains(key)) return false;
        return f.IsReadOnly && f.AssociatedSymbol is not IPropertySymbol { SetMethod: not null } || OnlyConstructorsWrite(key, f);
    }

    Dictionary<WasmFunction, INamedTypeSymbol>? _ctorFns;

    /// <summary>Every function that stores to the field (FieldWrites) is a constructor of the field's own type.</summary>
    bool OnlyConstructorsWrite(string key, IFieldSymbol f)
    {
        if (_ctorFns == null)
        {
            _ctorFns = [];
            foreach (var mi in AllMethods) if (mi.Sym.MethodKind == MethodKind.Constructor) _ctorFns[mi.Fn] = mi.Sym.ContainingType.OriginalDefinition;
        }
        // (the answer per key, from the functions that write each key, gathered once: asked for every borrow of a key,
        // this went through every function's writes each time. Asked only once the whole program is compiled, as
        // _ctorFns already assumes)
        if (_onlyCtorsWrite.TryGetValue(key, out var known)) return known;
        if (_writersOf == null)
        {
            _writersOf = [];
            foreach (var (fn, keys) in FieldWrites)
                foreach (var k in keys) { if (!_writersOf.TryGetValue(k, out var l)) _writersOf[k] = l = []; l.Add(fn); }
        }
        var own = f.OriginalDefinition.ContainingType;
        bool r = true;
        if (_writersOf.TryGetValue(key, out var writers))
            foreach (var fn in writers)
                if (!_ctorFns.TryGetValue(fn, out var ct) || !SymbolEqualityComparer.Default.Equals(ct, own)) { r = false; break; }
        return _onlyCtorsWrite[key] = r;
    }
    Dictionary<string, List<WasmFunction>>? _writersOf;
    readonly Dictionary<string, bool> _onlyCtorsWrite = [];

    internal bool InitOnly(IFieldSymbol f) => f.IsReadOnly && f.AssociatedSymbol is not IPropertySymbol { SetMethod: not null } && OncePerConstruction(f);

    /// <summary>Every way of constructing an object writes the field at most once, from null.</summary>
    bool OncePerConstruction(IFieldSymbol f)
    {
        f = f.OriginalDefinition;
        if (_initOnly.TryGetValue(f, out var r)) return r;
        _initOnly[f] = r = ComputeOnce(f);
        if (BorrowDebug) Console.Error.WriteLine($"once per construction {f.ContainingType.Name}.{f.Name}: {r}");
        return r;
    }

    bool ComputeOnce(IFieldSymbol f)
    {
        if (f.IsStatic || f.IsVolatile || f.ContainingType is not { TypeKind: TypeKind.Class } ct) return false;
        // (an `init` accessor's backing field is written by object initializers and `with` too)
        if (f.AssociatedSymbol is IPropertySymbol { SetMethod: { IsInitOnly: true } }) return false;
        if (ct.DeclaringSyntaxReferences.Length == 0) return false;
        int init = HasInitializer(f) ? 1 : 0;
        var memo = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        foreach (var c in ct.InstanceConstructors)
            if (Writes(c, f, init, memo, 0) is < 0 or > 1) return false;
        return true;
    }

    static bool HasInitializer(IFieldSymbol f)
    {
        foreach (var sr in (f.AssociatedSymbol ?? f).DeclaringSyntaxReferences)
            switch (sr.GetSyntax())
            {
                case VariableDeclaratorSyntax { Initializer: not null }: return true;
                case PropertyDeclarationSyntax { Initializer: not null }: return true;
            }
        return false;
    }

    /// <summary>How many times constructing through c writes f (its chain and the initializer included); -1: not
    /// countable (a write in a loop, through another receiver, or by address).</summary>
    int Writes(IMethodSymbol c, IFieldSymbol f, int init, Dictionary<IMethodSymbol, int> memo, int depth)
    {
        if (memo.TryGetValue(c, out var m)) return m;
        if (depth > 16) return -1;
        memo[c] = -1; // (a cycle: not countable)
        int r;
        var decl = c.DeclaringSyntaxReferences.Select(s => s.GetSyntax()).OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (c.IsImplicitlyDeclared || decl == null)
        {
            // an implicit constructor runs the initializers (a record's copy constructor copies the field: once)
            r = c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, c.ContainingType) ? 1 : init;
            if (c.DeclaringSyntaxReferences.Length > 0 && decl == null) r = init; // (a primary constructor)
        }
        else if (ModelFor(decl.SyntaxTree).GetOperation(decl) is not IConstructorBodyOperation body) r = -1;
        else
        {
            int own = 0;
            foreach (var o in body.Descendants())
            {
                if (o is not IFieldReferenceOperation fr || !SymbolEqualityComparer.Default.Equals(fr.Field.OriginalDefinition, f)) continue;
                if (!IsWrite(fr, out bool countable)) continue;
                if (!countable || fr.Instance is not IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } || InLoop(fr)) { own = -1; break; }
                own++;
            }
            if (own < 0) r = -1;
            else if (decl.Initializer is { ThisOrBaseKeyword.Text: "this" } && body.Initializer is IExpressionStatementOperation { Operation: IInvocationOperation { TargetMethod: var chained } })
            {
                int w = Writes(chained.OriginalDefinition, f, init, memo, depth + 1);
                r = w < 0 ? -1 : own + w;
            }
            else r = own + init;
        }
        memo[c] = r;
        return r;
    }

    /// <summary>Whether the reference is written (an assignment's target, an increment, a deconstruction's element,
    /// passed by `ref` or `out`, its address taken); countable: a plain write the count can follow.</summary>
    static bool IsWrite(IFieldReferenceOperation fr, out bool countable)
    {
        countable = true;
        IOperation x = fr;
        // (a field of a struct field: writing it writes the outer field)
        while (x.Parent is IFieldReferenceOperation outer && ReferenceEquals(outer.Instance, x)) x = outer;
        switch (x.Parent)
        {
            case IAssignmentOperation a when ReferenceEquals(a.Target, x): return true;
            case IIncrementOrDecrementOperation: return true;
            case ITupleOperation { Parent: IDeconstructionAssignmentOperation }: return true;
            case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out or RefKind.In or RefKind.RefReadOnlyParameter }: countable = false; return true;
            case IAddressOfOperation: countable = false; return true;
            case IInvocationOperation inv when ReferenceEquals(inv.Instance, x) && x.Type?.IsValueType == true && !inv.TargetMethod.IsReadOnly: countable = false; return true;
            case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.IsRef: true } }: countable = false; return true;
            case ISimpleAssignmentOperation { IsRef: true } ra when ReferenceEquals(ra.Value, x): countable = false; return true;
        }
        return false;
    }

    // ---- a constructor's first store to its own field: no release of the old value (null) ----
    readonly List<(WasmFunction fn, string key, WasmFunction marker)> _initStores = [];
    readonly Dictionary<string, WasmFunction> _initStoreMarkers = [];

    /// <summary>The release of a field's old value in a constructor's store to its own object, as a placeholder.</summary>
    internal WasmFunction InitStoreRelease(WasmFunction fn, string key)
    {
        if (!_initStoreMarkers.TryGetValue(key, out var m))
        {
            _initStoreMarkers[key] = m = Module.AddFunction("init store? " + key, new Wasm.FuncType([Wasm.ValType.I32], []));
            _initStores.Add((fn, key, m));
        }
        return m;
    }

    /// <summary>The placeholders become nothing (the field held null) or the release (RuntimeGen.Finish).</summary>
    internal void ResolveInitStores()
    {
        if (_initStoreMarkers.Count == 0) return;
        var reflected = ReflectedFieldKeys();
        var to = new Dictionary<WasmFunction, bool>();
        foreach (var (key, marker) in _initStoreMarkers)
        {
            bool ok = !s_noInitOnly && _borrowFields.TryGetValue(key, out var f) && !reflected.Contains(key) && !FieldsAddressTaken.Contains(key)
                && OncePerConstruction(f) && (f.IsReadOnly || OnlyConstructorsWrite(key, f));
            if (BorrowDebug) Console.Error.WriteLine($"init store {key}: {ok}");
            to[marker] = ok;
        }
        foreach (var fn in Module.Functions)
        {
            var b = fn.Body;
            for (int i = 0; i < b.Count; i++)
                if (b[i].Op == Op.Call && b[i].Ref is WasmFunction g && to.TryGetValue(g, out var ok))
                    b[i] = ok ? b[i] with { Op = Op.Drop, Ref = null } : b[i] with { Ref = Rt.Release };
        }
        Module.Functions.RemoveAll(f => to.ContainsKey(f));
    }
}
