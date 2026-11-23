using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Il2Wasm.Compiler;

// Owned arrays (section 4.3; docs/FINISH.md stream J). The type graph has one node per array type, so the arrays a
// syntax node keeps as its children and the arrays a pooled builder fills over and over are one node: the builder's
// element stores make every array of that type a general site, and the nodes that hold such arrays in their readonly
// fields are then on a cycle through it (Roslyn's green nodes and ArrayElement<GreenNode>[]: 268 types in one group).
//
// A field is *sealed* when every array it can ever hold is never written once it is there: each value stored into
// the field is an array made by code that wrote its elements while no one else could know it and then handed it
// over (to the field, or to a parameter that only hands it on), or an array already in a sealed field; and every read
// of the field only reads (elements, the length, a copy from it, a parameter that only reads). Such a field's arrays
// close no cycle through their elements: each element was stored while the array was still its maker's alone, of a
// value that existed already, and nothing stores into the array afterwards; so ordered by when each object stopped
// being only its maker's, every element edge goes back in time, as a readonly field's does. In the type graph the
// field then leads to a node of its own for the array type, whose element edges are not ones that close a cycle;
// arrays of the same type held anywhere else keep the array type's node and stay as they were.
//
// The proof is a greatest fixpoint over "carriers" (fields, parameters, locals that hold such arrays, the methods
// that take a fresh array to write into, the methods that return one): each is assumed to hold until a use of it
// is found that does not fit, and a failure undoes everything that leaned on it. All code the program can run is
// looked at (every compiled body, field initializer and static initializer, found by the identifiers it names), so
// a write of any kind (an element store, Array.Copy into it, a span or a reference to an element, Unsafe, a store of
// the array anywhere else, reflection) is a use that does not fit and the field stays unsealed.
internal sealed partial class Driver
{
    static readonly bool s_noOwnedArrays = Environment.GetEnvironmentVariable("IL2WASM_NO_OWNED_ARRAYS") == "1";
    internal static bool NoOwnedArrays => s_noOwnedArrays;

    internal sealed class OwnedArrays
    {
        readonly Driver D;
        static readonly bool s_debug = Environment.GetEnvironmentVariable("IL2WASM_ARENA_DEBUG") == "1";

        public OwnedArrays(Driver d) { D = d; }

        // ---- every body the program can run, by the identifiers it names ----

        readonly List<SyntaxReference> _code = [];
        readonly Dictionary<string, List<int>> _byName = [];
        readonly Dictionary<int, IOperation?> _roots = [];
        HashSet<IMethodSymbol> _bodies = null!, _compiled = null!;
        HashSet<ISymbol> _reflected = null!;
        // (key for code with a target-typed `new(...)`, and for constructor initializers: `: base(...)`, `: this(...)`)
        const string ImplicitNew = " new(", CtorInit = " :init";

        void Index()
        {
            var seen = new HashSet<(SyntaxTree, Microsoft.CodeAnalysis.Text.TextSpan)>();
            void Add(SyntaxReference r) { if (seen.Add((r.SyntaxTree, r.Span))) _code.Add(r); }
            _bodies = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            _compiled = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            foreach (var mi in D.AllMethods.ToList())
            {
                if (!mi.Compiled) continue;
                var def = mi.Sym.OriginalDefinition;
                _compiled.Add(def);
                if (mi.Impl == MethodImpl.Body) _bodies.Add(def);
                // (a lambda's or a local function's body is in its method's)
                if (def.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction) continue;
                foreach (var r in def.DeclaringSyntaxReferences)
                    if (r.GetSyntax() is not TypeDeclarationSyntax) Add(r);
                if (def.MethodKind == MethodKind.Constructor) foreach (var r in Initializers(def.ContainingType, false)) Add(r);
            }
            foreach (var (ct, _, _, _) in D._compiledCctors.ToList())
                foreach (var r in Initializers(ct.OriginalDefinition, true)) Add(r);
            for (int i = 0; i < _code.Count; i++)
                foreach (var t in _code[i].GetSyntax().DescendantTokens())
                {
                    string? k = t.Kind() switch
                    {
                        SyntaxKind.IdentifierToken => t.ValueText,
                        SyntaxKind.NewKeyword when t.Parent is ImplicitObjectCreationExpressionSyntax => ImplicitNew,
                        SyntaxKind.BaseKeyword or SyntaxKind.ThisKeyword when t.Parent is ConstructorInitializerSyntax => CtorInit,
                        _ => null,
                    };
                    if (k == null) continue;
                    if (!_byName.TryGetValue(k, out var l)) _byName[k] = l = [];
                    if (l.Count == 0 || l[^1] != i) l.Add(i);
                }
            // (what reflection can call or set: the members with an invoke thunk; one with a record and no thunk, a
            // library frame's, throws when invoked, RuntimeMethodInfo.InternalInvoke)
            _reflected = new HashSet<ISymbol>(D.ThunkedMembers, SymbolEqualityComparer.Default);
        }

        static IEnumerable<SyntaxReference> Initializers(INamedTypeSymbol t, bool statics) =>
            t.GetMembers().Where(x => x.IsStatic == statics && x is IFieldSymbol or IPropertySymbol)
                .SelectMany(x => x.DeclaringSyntaxReferences)
                .Select(x => x.GetSyntax() switch
                {
                    VariableDeclaratorSyntax { Initializer: { } vi } => vi.SyntaxTree.GetReference(vi),
                    PropertyDeclarationSyntax { Initializer: { } pi } => pi.SyntaxTree.GetReference(pi),
                    _ => null,
                }).OfType<SyntaxReference>();

        IOperation? Root(int i)
        {
            if (_roots.TryGetValue(i, out var r)) return r;
            var s = _code[i].GetSyntax();
            return _roots[i] = D.ModelFor(s.SyntaxTree).GetOperation(s);
        }

        readonly Dictionary<string, List<IOperation>> _named = [];

        /// <summary>The references, calls and initializers in the code that names this identifier (a symbol's
        /// references are among them).</summary>
        List<IOperation> Named(string name)
        {
            if (_named.TryGetValue(name, out var r)) return r;
            r = [];
            if (_byName.TryGetValue(name, out var l))
                foreach (var i in l)
                {
                    if (Root(i) is not { } root) continue;
                    // (a constructor's initializer is all that the key stands for)
                    if (name == CtorInit) root = (root as IConstructorBodyOperation)?.Initializer!;
                    if (root == null) continue;
                    foreach (var op in root.DescendantsAndSelf())
                        if (op is IFieldReferenceOperation or IFieldInitializerOperation or IInvocationOperation or IObjectCreationOperation or IMethodReferenceOperation) r.Add(op);
                }
            return _named[name] = r;
        }

        static IMethodSymbol Def(IMethodSymbol m) => (m.ReducedFrom ?? m).OriginalDefinition;
        static bool Eq(ISymbol? a, ISymbol? b) => SymbolEqualityComparer.Default.Equals(a, b);

        // ---- carriers and the fixpoint ----

        abstract record Carrier;
        /// <summary>A field whose arrays are never written (all its values are sealed, all its reads only read).</summary>
        sealed record FieldC(IFieldSymbol F) : Carrier { public override string ToString() => "field " + F.ToDisplayString(); }
        /// <summary>A parameter that only reads the array it is given (or hands it to another carrier).</summary>
        sealed record ReadParamC(IMethodSymbol M, int I) : Carrier { public override string ToString() => $"read parameter {I} of {M.ToDisplayString()}"; }
        /// <summary>That, and every call gives it a sealed array (its value may then go into a sealed field).</summary>
        sealed record HandParamC(IMethodSymbol M, int I) : Carrier { public override string ToString() => $"handing parameter {I} of {M.ToDisplayString()}"; }
        /// <summary>A local given only sealed arrays, whose uses only read.</summary>
        sealed record LocalC(ILocalSymbol L) : Carrier { public override string ToString() => "local " + L.Name + " in " + L.ContainingSymbol.ToDisplayString(); }
        /// <summary>A parameter given an array while its maker still has it alone: it may write the elements, and keeps
        /// the array nowhere.</summary>
        sealed record WriteParamC(IMethodSymbol M, int I) : Carrier { public override string ToString() => $"writing parameter {I} of {M.ToDisplayString()}"; }
        /// <summary>A method that returns, at every return, an array it made and kept to itself.</summary>
        sealed record FreshReturnC(IMethodSymbol M) : Carrier { public override string ToString() => "fresh return of " + M.ToDisplayString(); }

        readonly Dictionary<Carrier, bool> _state = [];
        readonly List<Carrier> _all = [];
        readonly Dictionary<Carrier, string> _why = [];
        Carrier? _checking;

        bool Holds(Carrier c)
        {
            if (_state.TryGetValue(c, out var v)) return v;
            _state[c] = true;
            _all.Add(c);
            return true;
        }

        bool No(string why)
        {
            if (_checking != null && !_why.ContainsKey(_checking)) _why[_checking] = why;
            return false;
        }

        /// <summary>The fields among these whose arrays are never written once there.</summary>
        public HashSet<IFieldSymbol> Seal(IEnumerable<IFieldSymbol> fields)
        {
            var result = new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
            // (a plugin's hook may be given any value a method computes: no proof then)
            if (D._instrumented.Count > 0) return result;
            Index();
            var wanted = fields.Select(f => f.OriginalDefinition).Distinct<IFieldSymbol>(SymbolEqualityComparer.Default).ToList();
            foreach (var f in wanted) Holds(new FieldC(f));
            bool changed = true;
            int passes = 0;
            while (changed)
            {
                changed = false;
                passes++;
                for (int i = 0; i < _all.Count; i++)
                {
                    var c = _all[i];
                    if (!_state[c]) continue;
                    _checking = c;
                    bool ok = Check(c);
                    _checking = null;
                    if (!ok) { _state[c] = false; changed = true; }
                }
            }
            foreach (var f in wanted) if (_state[new FieldC(f)]) result.Add(f);
            if (s_debug)
            {
                Console.Error.WriteLine($"owned arrays: {passes} passes, {_all.Count} carriers, {_code.Count} bodies indexed");
                foreach (var c in _all)
                    Console.Error.WriteLine($"owned arrays: {(_state[c] ? "holds" : "fails")} {c}{(_why.TryGetValue(c, out var w) ? ": " + w : "")}");
            }
            return result;
        }

        bool Check(Carrier c) => c switch
        {
            FieldC f => CheckField(f.F),
            ReadParamC p => CheckParamUses(p.M, p.I, write: false),
            HandParamC p => CheckParamUses(p.M, p.I, write: false) && CheckCallers(p.M, p.I),
            LocalC l => CheckLocal(l.L),
            WriteParamC p => CheckParamUses(p.M, p.I, write: true),
            FreshReturnC r => CheckFreshReturn(r.M),
            _ => false,
        };

        static bool IsArrayOfRefs(ITypeSymbol? t) => t is IArrayTypeSymbol { Rank: 1 };

        bool CheckField(IFieldSymbol f)
        {
            if (!IsArrayOfRefs(f.Type)) return No("not an array");
            if (f.ContainingType.TypeKind != TypeKind.Class) return No("not a class's field (a struct is copied whole)");
            if (f.DeclaringSyntaxReferences.Length == 0) return No("no source");
            // (a record's copy constructor and its other members are the compiler's, with no body to look at)
            if (f.ContainingType.IsRecord) return No("a record's");
            if (_reflected.Contains(f)) return No("reflection can set it");
            if (Library.FieldSubstitution(f) != null) return No("substituted");
            // (a field another overlaps can be written through that one)
            if (f.ContainingType.GetMembers().OfType<IFieldSymbol>().Any(x => x.GetAttributes().Any(a => a.AttributeClass?.Name == "FieldOffsetAttribute"))) return No("explicit layout");
            foreach (var op in Named(f.Name))
            {
                if (op is IFieldInitializerOperation fi && fi.InitializedFields.Any(x => Eq(x.OriginalDefinition, f)))
                {
                    if (!Source(fi.Value)) return No("initialized with " + fi.Value.Syntax);
                    continue;
                }
                if (op is not IFieldReferenceOperation fr || !Eq(fr.Field.OriginalDefinition, f) || InNameOf(fr)) continue;
                if (fr.Parent is ISimpleAssignmentOperation { IsRef: false } sa && ReferenceEquals(sa.Target, fr))
                {
                    if (!Source(sa.Value)) return No("given " + sa.Value.Syntax);
                    continue;
                }
                if (!SafeUse(fr)) return No("used in " + (fr.Parent?.Syntax ?? fr.Syntax));
            }
            return true;
        }

        /// <summary>The methods a call can run: the target, or every compiled override of a virtual one; null when
        /// that is not known (an interface's, a delegate's).</summary>
        List<IMethodSymbol>? Callees(IMethodSymbol target, bool isVirtual)
        {
            var t = Def(target);
            if (t.ContainingType.TypeKind == TypeKind.Interface) return null;
            bool overridable = (t.IsVirtual || t.IsAbstract || t.IsOverride) && !t.IsSealed && !t.ContainingType.IsSealed;
            if (!isVirtual || !overridable) return [t];
            var r = new List<IMethodSymbol>();
            if (!t.IsAbstract) r.Add(t);
            // (every compiled override: one without a body of its own fails as a carrier)
            foreach (var m in _compiled)
                if (m.Name == t.Name && !Eq(m, t))
                    for (var o = m.OverriddenMethod; o != null; o = o.OverriddenMethod)
                        if (Eq(o.OriginalDefinition, t)) { r.Add(m); break; }
            return r;
        }

        IOperation? Body(IMethodSymbol m)
        {
            foreach (var sr in m.DeclaringSyntaxReferences)
            {
                var s = sr.GetSyntax();
                if (s is TypeDeclarationSyntax) return null;
                if (D.ModelFor(s.SyntaxTree).GetOperation(s) is { } b) return b;
            }
            return null;
        }

        bool CheckParamUses(IMethodSymbol m, int i, bool write)
        {
            if (i >= m.Parameters.Length || m.Parameters[i].RefKind != RefKind.None) return No("by reference");
            if (!_bodies.Contains(m)) return No("no compiled body");
            if (Library.Substitution(m) != null || D.IsIntrinsic(m) || m.IsExtern) return No("its body is not what runs");
            if (m.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction) return No("a lambda");
            // (a writing callee must be done with the array when it returns: no part of it runs later)
            if (write && (m.IsAsync || m.IsIterator)) return No("runs on after it returns");
            var body = Body(m);
            if (body == null) return No("no body found");
            var p = m.Parameters[i];
            var nested = write ? NestedOps(body) : null;
            if (write && body.Descendants().Any(o => o.Kind is OperationKind.YieldReturn or OperationKind.YieldBreak)) return No("an iterator");
            foreach (var op in body.DescendantsAndSelf())
            {
                if (op is not IParameterReferenceOperation pr || !Eq(pr.Parameter.OriginalDefinition, p) || InNameOf(pr)) continue;
                // (a lambda may run after the call, when the array is no longer only its maker's)
                if (nested != null && nested.Contains(pr)) return No("used in a lambda");
                if (IsWriteOf(pr)) return No("assigned: " + pr.Parent?.Syntax);
                if (!(write ? MakerUse(pr) : SafeUse(pr))) return No("used in " + (pr.Parent?.Syntax ?? pr.Syntax));
            }
            return true;
        }

        bool CheckCallers(IMethodSymbol m, int i)
        {
            if (m.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor)) return No("not called by name");
            if (m.IsVirtual || m.IsAbstract || m.IsOverride) return No("virtual: callers through its base");
            if (_reflected.Contains(m)) return No("reflection can call it");
            if (!m.IsStatic && m.MethodKind == MethodKind.Ordinary && m.ContainingType.AllInterfaces.SelectMany(x => x.GetMembers()).Any(x => Eq(m.ContainingType.FindImplementationForInterfaceMember(x)?.OriginalDefinition, m)))
                return No("implements an interface's");
            bool ctor = m.MethodKind == MethodKind.Constructor;
            // (an attribute is made by its factory, from the attribute's arguments, with no call in a body)
            for (var b = m.ContainingType; ctor && b != null; b = b.BaseType)
                if (b.ContainingNamespace?.ToDisplayString() == "System" && b.Name == "Attribute") return No("an attribute's");
            // (a primary constructor's base call is in a type's declaration, not in a body)
            if (ctor && m.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax)) return No("a primary constructor");
            var names = ctor ? new[] { m.ContainingType.Name, ImplicitNew, CtorInit } : [m.Name];
            foreach (var name in names)
                foreach (var op in Named(name))
                {
                    IMethodSymbol? target; ImmutableArrayView args;
                    switch (op)
                    {
                        case IInvocationOperation inv: target = inv.TargetMethod; args = new(inv.Arguments); break;
                        case IObjectCreationOperation oc when oc.Constructor != null: target = oc.Constructor; args = new(oc.Arguments); break;
                        case IMethodReferenceOperation mr when Eq(Def(mr.Method), m): return No("made a delegate: " + mr.Syntax);
                        default: continue;
                    }
                    if (!Eq(Def(target), m)) continue;
                    var a = args.Items.FirstOrDefault(x => x.Parameter?.Ordinal == i);
                    if (a == null) return No("a call without it: " + op.Syntax);
                    if (!Source(a.Value)) return No("given " + a.Value.Syntax);
                }
            // (a derived type's primary constructor passes on what it likes)
            if (ctor && !m.ContainingType.IsSealed && D.AllMethods.Any(x => x.Compiled && x.Sym.MethodKind == MethodKind.Constructor
                && Eq(x.Sym.ContainingType.OriginalDefinition.BaseType?.OriginalDefinition, m.ContainingType)
                && x.Sym.OriginalDefinition.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax)))
                return No("a derived type's primary constructor");
            return true;
        }

        readonly struct ImmutableArrayView(System.Collections.Immutable.ImmutableArray<IArgumentOperation> items)
        {
            public readonly System.Collections.Immutable.ImmutableArray<IArgumentOperation> Items = items;
        }

        bool CheckLocal(ILocalSymbol l)
        {
            if (l.IsRef || !IsArrayOfRefs(l.Type)) return No("a reference, or not an array");
            var decl = l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (decl is not VariableDeclaratorSyntax || D.ModelFor(decl.SyntaxTree).GetOperation(decl) is not IVariableDeclaratorOperation vd) return No("not declared by a declarator");
            var root = (IOperation)vd;
            while (root.Parent != null) root = root.Parent;
            var init = vd.Initializer?.Value ?? (vd.Parent as IVariableDeclarationOperation)?.Initializer?.Value;
            if (init != null && !Source(init)) return No("given " + init.Syntax);
            foreach (var op in root.DescendantsAndSelf())
            {
                if (op is not ILocalReferenceOperation lr || !Eq(lr.Local, l) || InNameOf(lr)) continue;
                if (lr.Parent is ISimpleAssignmentOperation { IsRef: false } sa && ReferenceEquals(sa.Target, lr)) { if (!Source(sa.Value)) return No("given " + sa.Value.Syntax); continue; }
                if (IsWriteOf(lr)) return No("written: " + lr.Parent?.Syntax);
                if (!SafeUse(lr)) return No("used in " + (lr.Parent?.Syntax ?? lr.Syntax));
            }
            return true;
        }

        bool CheckFreshReturn(IMethodSymbol m)
        {
            if (!_bodies.Contains(m) || m.IsAsync || m.IsIterator || m.ReturnsByRef || m.ReturnsByRefReadonly || !IsArrayOfRefs(m.ReturnType)) return No("not a plain method returning an array");
            if (Library.Substitution(m) != null || D.IsIntrinsic(m) || m.IsExtern) return No("its body is not what runs");
            var body = Body(m);
            if (body == null) return No("no body found");
            var nested = NestedOps(body);
            var rets = body.DescendantsAndSelf().OfType<IReturnOperation>().Where(o => !nested.Contains(o)).ToList();
            if (rets.Count == 0) return No("never returns");
            foreach (var ret in rets)
            {
                var v = Unconvert(ret.ReturnedValue);
                if (v?.ConstantValue is { HasValue: true, Value: null }) continue;
                if (FreshExpr(v)) continue;
                if (v is ILocalReferenceOperation lr && FreshLocal(lr)) continue;
                return No("returns " + ret.ReturnedValue?.Syntax);
            }
            return true;
        }

        // ---- values and uses ----

        static IOperation? Unconvert(IOperation? v)
        {
            while (true)
            {
                if (v is IParenthesizedOperation pa) { v = pa.Operand; continue; }
                if (v is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference && c.Conversion.IsImplicit && c.Operand.Type is IArrayTypeSymbol && c.Type is IArrayTypeSymbol)) { v = c.Operand; continue; }
                return v;
            }
        }

        /// <summary>The operation that consumes v's value, past conversions that keep it the same array.</summary>
        static IOperation Up(IOperation v, bool toArray = false)
        {
            while (v.Parent is IParenthesizedOperation
                || v.Parent is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity
                    || c.Conversion.IsReference && c.Conversion.IsImplicit && (c.Type is IArrayTypeSymbol || toArray && c.Type?.SpecialType == SpecialType.System_Array)))
                v = v.Parent;
            return v;
        }

        /// <summary>A value that may go into a carrier: a sealed array, or null.</summary>
        bool Source(IOperation? e)
        {
            if (e?.ConstantValue is { HasValue: true, Value: null }) return true;
            e = Unconvert(e);
            switch (e)
            {
                case null: return false;
                case IDefaultValueOperation: return true;
                case IConditionalOperation { IsRef: false } c: return Source(c.WhenTrue) && Source(c.WhenFalse);
                case ICoalesceOperation co: return Source(co.Value) && Source(co.WhenNull);
                case IFieldReferenceOperation fr: return Holds(new FieldC(fr.Field.OriginalDefinition));
                case IParameterReferenceOperation pr:
                    return pr.Parameter.ContainingSymbol is IMethodSymbol pm && Holds(new HandParamC(pm.OriginalDefinition, pr.Parameter.Ordinal));
                case ILocalReferenceOperation lr:
                    return FreshDeclared(lr.Local) ? FreshLocal(lr) : Holds(new LocalC(lr.Local));
                default: return FreshExpr(e);
            }
        }

        /// <summary>An expression that makes an array no one else knows: `new T[n]` (its initializer's elements
        /// stored before it exists for anyone), a collection expression, or a call that returns one.</summary>
        bool FreshExpr(IOperation? e)
        {
            e = Unconvert(e);
            switch (e)
            {
                case IArrayCreationOperation { DimensionSizes.Length: 1 }: return true;
                case ICollectionExpressionOperation { Type: IArrayTypeSymbol }: return true;
                case IInvocationOperation inv:
                    {
                        var cs = Callees(inv.TargetMethod, inv.IsVirtual);
                        return cs != null && cs.Count > 0 && cs.All(m => Holds(new FreshReturnC(m)));
                    }
                default: return false;
            }
        }

        bool FreshDeclared(ILocalSymbol l)
        {
            if (l.IsRef) return false;
            var decl = l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (decl is not VariableDeclaratorSyntax { Initializer: { } eq }) return false;
            var model = D.ModelFor(decl.SyntaxTree);
            return model.GetOperation(decl) is IVariableDeclaratorOperation vd
                && (vd.Initializer?.Value ?? (vd.Parent as IVariableDeclarationOperation)?.Initializer?.Value) is { } init && FreshExpr(init);
        }

        readonly Dictionary<IOperation, (bool jumps, HashSet<IOperation> nested)> _rootFacts = [];

        static HashSet<IOperation> NestedOps(IOperation body) =>
            body.Descendants().Where(o => o is IAnonymousFunctionOperation or ILocalFunctionOperation).SelectMany(o => o.DescendantsAndSelf()).ToHashSet();

        /// <summary>The local holds an array made here (FreshDeclared), and `handoff` is the one place it lets it out:
        /// every other use writes or reads its elements, its length, or gives it to a callee that writes into it and
        /// keeps it nowhere, and all of them come before (in the text, with no jump back: no goto, and no loop around
        /// the hand-off that the declaration is outside of).</summary>
        bool FreshLocal(ILocalReferenceOperation handoff)
        {
            var l = handoff.Local;
            if (!FreshDeclared(l)) return false;
            var root = (IOperation)handoff;
            while (root.Parent != null) root = root.Parent;
            if (!_rootFacts.TryGetValue(root, out var facts))
                _rootFacts[root] = facts = (root.DescendantsAndSelf().Any(o => o is ILabeledOperation || o is IBranchOperation { BranchKind: BranchKind.GoTo } || o.Kind is OperationKind.YieldReturn or OperationKind.YieldBreak), NestedOps(root));
            if (facts.jumps) return No("goto or yield in " + l.ContainingSymbol.ToDisplayString());
            if (Driver.InLoopOutsideDeclaration(handoff, l, root)) return No("handed over in a loop " + l.Name + " is declared outside of");
            foreach (var op in root.DescendantsAndSelf())
            {
                if (op is not ILocalReferenceOperation lr || !Eq(lr.Local, l)) continue;
                if (facts.nested.Contains(lr)) return No(l.Name + " used in a lambda");
                if (ReferenceEquals(lr, handoff)) continue;
                if (lr.IsDeclaration || InNameOf(lr)) continue;
                if (lr.Syntax.SpanStart >= handoff.Syntax.SpanStart) return No(l.Name + " used after it is handed over: " + lr.Parent?.Syntax);
                if (IsWriteOf(lr)) return No(l.Name + " assigned again");
                if (!MakerUse(lr)) return No(l.Name + " used in " + (lr.Parent?.Syntax ?? lr.Syntax));
            }
            return true;
        }

        /// <summary>The reference itself is written (not the array it holds).</summary>
        static bool IsWriteOf(IOperation r)
        {
            var p = r.Parent;
            return p is IAssignmentOperation a && ReferenceEquals(a.Target, r) || p is IIncrementOrDecrementOperation || p is IAddressOfOperation
                || p is IArgumentOperation { Parameter.RefKind: not RefKind.None and not RefKind.In and not RefKind.RefReadOnlyParameter }
                || p is ITupleOperation { Parent: IDeconstructionAssignmentOperation }
                || r.Syntax.Parent is RefExpressionSyntax;
        }

        static bool IsArrayType(ISymbol? t) => t is INamedTypeSymbol { SpecialType: SpecialType.System_Array };

        /// <summary>Array.Copy's (and Array.Clear's, Array.Fill's) arrays: what the call reads, what it writes.</summary>
        static (bool reads, bool writes)? LibraryArrayParam(IArgumentOperation a)
        {
            if (a.Parent is not IInvocationOperation inv || a.Parameter == null) return null;
            var m = inv.TargetMethod;
            if (!IsArrayType(m.ContainingType)) return null;
            return (m.Name, a.Parameter.Name) switch
            {
                ("Copy" or "ConstrainedCopy", "sourceArray") => (true, false),
                ("Copy" or "ConstrainedCopy", "destinationArray") => (false, true),
                ("Clear" or "Fill", "array") => (false, true),
                ("IndexOf" or "LastIndexOf" or "BinarySearch" or "Exists" or "Find" or "FindIndex" or "FindLast" or "FindLastIndex" or "TrueForAll" or "Contains", "array") => (true, false),
                _ => null,
            };
        }

        /// <summary>A use of a sealed array that only reads it (or hands it to another carrier).</summary>
        bool SafeUse(IOperation x)
        {
            x = Up(x, toArray: x.Parent is IConversionOperation { Parent: IArgumentOperation });
            var p = x.Parent;
            switch (p)
            {
                case IArrayElementReferenceOperation ae when ReferenceEquals(ae.ArrayReference, x):
                    return ElementUse(ae, write: false);
                case IPropertyReferenceOperation pr when ReferenceEquals(pr.Instance, x):
                    return IsArrayType(pr.Property.ContainingType) && pr.Property.Name is "Length" or "LongLength" or "Rank" && !IsWriteOf(pr);
                case IInvocationOperation inv when ReferenceEquals(inv.Instance, x):
                    return (IsArrayType(inv.TargetMethod.ContainingType) && inv.TargetMethod.Name is "GetLength" or "GetLongLength" or "GetLowerBound" or "GetUpperBound" or "Clone")
                        || inv.TargetMethod is { Name: "GetHashCode" or "GetType", Parameters.Length: 0 };
                case IArgumentOperation a when ReferenceEquals(a.Value, x):
                    {
                        if (a.Parameter == null || a.Parameter.RefKind != RefKind.None || a.ArgumentKind is not (ArgumentKind.Explicit or ArgumentKind.DefaultValue)) return false;
                        if (LibraryArrayParam(a) is { } lib) return !lib.writes;
                        if (a.Parent is IObjectCreationOperation { Constructor: { } ctor }) return Holds(new ReadParamC(ctor.OriginalDefinition, a.Parameter.Ordinal));
                        if (a.Parent is not IInvocationOperation call) return false;
                        var cs = Callees(call.TargetMethod, call.IsVirtual);
                        return cs != null && cs.All(m => Holds(new ReadParamC(m, a.Parameter.Ordinal)));
                    }
                case ISimpleAssignmentOperation { IsRef: false } sa when ReferenceEquals(sa.Value, x):
                    return sa.Target switch
                    {
                        IFieldReferenceOperation tf => Holds(new FieldC(tf.Field.OriginalDefinition)),
                        ILocalReferenceOperation tl => !tl.Local.IsRef && Holds(new LocalC(tl.Local)),
                        _ => false,
                    } && (sa.Parent is IExpressionStatementOperation || SafeUse(sa));
                case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol: { RefKind: RefKind.None } il } }:
                    return Holds(new LocalC(il));
                case IFieldInitializerOperation fi:
                    return fi.InitializedFields.All(f => Holds(new FieldC(f.OriginalDefinition)));
                case IForEachLoopOperation fe when ReferenceEquals(fe.Collection, x):
                    return !fe.Locals.Any(l => l.IsRef) && fe.LoopControlVariable is not IVariableDeclaratorOperation { Symbol.IsRef: true };
                case IBinaryOperation { OperatorMethod: null }:
                case IIsTypeOperation:
                case IExpressionStatementOperation:
                    return true;
                case IIsPatternOperation ip when ReferenceEquals(ip.Value, x):
                    return ip.Pattern is ITypePatternOperation or IConstantPatternOperation or INegatedPatternOperation { Pattern: ITypePatternOperation or IConstantPatternOperation }
                        or IDeclarationPatternOperation { DeclaredSymbol: null };
                case IConditionalOperation { IsRef: false } c when !ReferenceEquals(c.Condition, x):
                    return SafeUse(c);
                case ICoalesceOperation co:
                    return SafeUse(co);
                default:
                    return false;
            }
        }

        /// <summary>A use of an array its maker still has alone (or a writing parameter): its elements written or read,
        /// its length, given to a callee that writes into it and keeps it nowhere.</summary>
        bool MakerUse(IOperation x)
        {
            x = Up(x, toArray: x.Parent is IConversionOperation { Parent: IArgumentOperation });
            var p = x.Parent;
            switch (p)
            {
                case IArrayElementReferenceOperation ae when ReferenceEquals(ae.ArrayReference, x):
                    return ElementUse(ae, write: true);
                case IPropertyReferenceOperation pr when ReferenceEquals(pr.Instance, x):
                    return IsArrayType(pr.Property.ContainingType) && pr.Property.Name is "Length" or "LongLength" or "Rank" && !IsWriteOf(pr);
                case IInvocationOperation inv when ReferenceEquals(inv.Instance, x):
                    return IsArrayType(inv.TargetMethod.ContainingType) && inv.TargetMethod.Name is "GetLength" or "GetLongLength" or "GetLowerBound" or "GetUpperBound";
                case IArgumentOperation a when ReferenceEquals(a.Value, x):
                    {
                        if (a.Parameter == null || a.Parameter.RefKind != RefKind.None || a.ArgumentKind is not (ArgumentKind.Explicit or ArgumentKind.DefaultValue)) return false;
                        if (LibraryArrayParam(a) != null) return true;
                        if (a.Parent is IObjectCreationOperation { Constructor: { } ctor }) return Holds(new WriteParamC(ctor.OriginalDefinition, a.Parameter.Ordinal));
                        if (a.Parent is not IInvocationOperation call) return false;
                        var cs = Callees(call.TargetMethod, call.IsVirtual);
                        return cs != null && cs.All(m => Holds(new WriteParamC(m, a.Parameter.Ordinal)));
                    }
                case IForEachLoopOperation fe when ReferenceEquals(fe.Collection, x):
                    return !fe.Locals.Any(l => l.IsRef) && fe.LoopControlVariable is not IVariableDeclaratorOperation { Symbol.IsRef: true };
                case IBinaryOperation { OperatorMethod: null }:
                case IIsTypeOperation:
                case IExpressionStatementOperation:
                    return true;
                case IIsPatternOperation ip when ReferenceEquals(ip.Value, x):
                    return ip.Pattern is ITypePatternOperation or IConstantPatternOperation or INegatedPatternOperation { Pattern: ITypePatternOperation or IConstantPatternOperation }
                        or IDeclarationPatternOperation { DeclaredSymbol: null };
                default:
                    return false;
            }
        }

        /// <summary>An element (or a field of a struct element) used by value; written too where `write`: never by
        /// reference (a `ref` to it could write it later). An element that is a reference is a value wherever it is
        /// not a place written or taken by reference; a struct element only in the uses listed (its methods and
        /// properties, an `await`, a deconstruction, a pattern, a `foreach` could all run on the element in place).</summary>
        static bool ElementUse(IOperation e, bool write)
        {
            if (e.Syntax.Parent is RefExpressionSyntax || e.Syntax.Parent is ArgumentSyntax { RefKindKeyword.RawKind: not 0 }) return false;
            var p = e.Parent;
            while (p is IParenthesizedOperation) { e = p; p = p.Parent; }
            bool valueType = e.Type?.IsValueType != false;
            switch (p)
            {
                case IAssignmentOperation a when ReferenceEquals(a.Target, e):
                    return write && a is not ISimpleAssignmentOperation { IsRef: true };
                case ISimpleAssignmentOperation { IsRef: true }:
                    return false;
                case ISimpleAssignmentOperation:
                    return true; // (the value of an assignment)
                case IIncrementOrDecrementOperation:
                    return write;
                case IAddressOfOperation:
                    return false;
                case ITupleOperation { Parent: IDeconstructionAssignmentOperation da } t when ReferenceEquals(da.Target, t):
                    return write;
                case IArgumentOperation a:
                    return a.Parameter?.RefKind == RefKind.None;
                case IFieldReferenceOperation fr when ReferenceEquals(fr.Instance, e):
                    return !valueType || ElementUse(fr, write);
                case IInvocationOperation inv when ReferenceEquals(inv.Instance, e):
                    return !valueType || inv.TargetMethod.IsReadOnly || inv.TargetMethod.ContainingType.IsReadOnly;
                case IPropertyReferenceOperation pr when ReferenceEquals(pr.Instance, e):
                    return !valueType || pr.Property.GetMethod is { IsReadOnly: true } && pr.Property.SetMethod == null && !pr.Property.ReturnsByRef && !IsWriteOf(pr);
                case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } }:
                case IConditionalOperation { IsRef: true }:
                    return false;
                case IVariableInitializerOperation:
                case IConversionOperation:
                case IReturnOperation:
                case IBinaryOperation:
                case IExpressionStatementOperation:
                    return true;
                default:
                    return !valueType && p is not (IDynamicInvocationOperation or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation);
            }
        }

        static bool InNameOf(IOperation op)
        {
            for (var p = op.Parent; p != null; p = p.Parent) if (p is INameOfOperation) return true;
            return false;
        }
    }
}

internal sealed partial class Arenas
{
    /// <summary>Owned arrays in the type graph: a field site on a cyclic component whose only way round is an array
    /// type's element stores, and whose field is sealed (Driver.OwnedArrays), leads to a node of its own for each array
    /// type it holds, with that type's element edges as edges that close no cycle. Should such a node still be on a
    /// cycle (through something else that closes it), it is joined to its array type again, both ways: the arrays are
    /// then members as they were. Returns whether the graph changed (`scc` is then its components).</summary>
    bool SealOwnedArrays(List<List<int>> adj, Dictionary<int, Site> hubSite, Dictionary<Site, List<int>> siteNodes, ref int[] scc, int n)
    {
        var c0 = scc;
        // the array types whose element stores close a cycle of their component
        var closing = new HashSet<int>();
        for (int u = 0; u < n; u++)
            if (scc[u] != 0 && D.TypeById[u] is IArrayTypeSymbol)
                foreach (var h in adj[u])
                    if (scc[h] == scc[u] && hubSite.TryGetValue(h, out var hs) && hs.IsArray && !hs.Immutable) { closing.Add(u); break; }
        if (closing.Count == 0) return false;
        // the field sites on such a component that lead to one of them
        var candidates = new List<Site>();
        foreach (var (s, hubs) in siteNodes)
            if (!s.IsArray && s.ContainerId < 0 && s.Member is IFieldSymbol && s.SlotType is IArrayTypeSymbol
                && hubs.Any(h => c0[h] != 0 && adj[h].Any(u => u < n && closing.Contains(u) && c0[u] == c0[h])))
                candidates.Add(s);
        if (candidates.Count == 0) return false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sealedFields = new Driver.OwnedArrays(D).Seal(candidates.Select(s => (IFieldSymbol)s.Member!));
        D.Mark($"    arenas: owned arrays ({candidates.Count} fields asked, {sealedFields.Count} sealed, {sw.ElapsedMilliseconds} ms)");
        if (sealedFields.Count == 0) return false;
        var own = new Dictionary<int, int>();
        int Own(int u)
        {
            if (own.TryGetValue(u, out var o)) return o;
            o = adj.Count; adj.Add([]); own[u] = o;
            // (copies of the array type's element hubs: the same targets, but no site's, so no store of them closes a cycle)
            foreach (var h in adj[u].ToList()) { int c = adj.Count; adj.Add(adj[h]); adj[o].Add(c); }
            return o;
        }
        foreach (var s in candidates)
        {
            if (!sealedFields.Contains(((IFieldSymbol)s.Member!).OriginalDefinition)) continue;
            if (s_arenaDebug) Console.Error.WriteLine($"owned arrays: site {s.Key} leads to arrays of its own");
            foreach (var h in siteNodes[s])
                for (int i = 0; i < adj[h].Count; i++)
                    if (adj[h][i] < n && D.TypeById[adj[h][i]] is IArrayTypeSymbol) adj[h][i] = Own(adj[h][i]);
        }
        scc = Components(adj, hubSite);
        // (an owned array's node on a cycle that closes elsewhere: its arrays must be members, as their type's are)
        while (true)
        {
            bool joined = false;
            foreach (var (u, o) in own)
                if (scc[o] != 0 && !adj[o].Contains(u))
                {
                    if (s_arenaDebug) Console.Error.WriteLine($"owned arrays: {Types.Key(D.TypeById[u])} still on a cycle: joined to its type again");
                    adj[o].Add(u); adj[u].Add(o); joined = true;
                }
            if (!joined) break;
            scc = Components(adj, hubSite);
        }
        return true;
    }
}
