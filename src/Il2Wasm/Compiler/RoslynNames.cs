using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Il2Wasm.Compiler;

/// <summary>
/// The names the C# compiler gives the methods and classes it makes for lambdas and local functions, as stack traces
/// and reflection show them: `Program.&lt;&gt;c.&lt;Main&gt;b__9_0()` (a lambda that captures nothing, in the
/// singleton class), `Program.&lt;&gt;c__DisplayClass3_0.&lt;Captured&gt;b__0()` (one that captures, in the class of
/// the environment it lives in), `Program.&lt;Local&gt;g__Div|5_0(Int32 a, &lt;&gt;c__DisplayClass5_0&amp;)` (a local
/// function: its captures in a struct passed by reference).
///
/// The numbers follow Roslyn's closure conversion (Lowering/ClosureConversion, GeneratedNames.cs), emulated on the
/// operation tree: the method's position among its type's members; a tree of scopes (blocks that declare variables,
/// and every lambda's or local function's body); an environment for each scope whose variables are captured, a struct
/// when no function that captures it becomes a delegate (or is async or an iterator), numbered in a walk of the tree
/// (a scope, then the scopes in it); each function's place (the nearest class environment it captures, the type
/// itself when it captures `this` alone, the singleton for a lambda that captures nothing, else a static method of
/// the type); and the functions numbered by the same walk, each scope's own before those of the scopes in it. In a
/// release build environments merge into their parents when the same functions capture both (MergeEnvironments).
/// What is not emulated: the scopes of the temporaries lowering adds around expressions other than object and
/// collection initializers (a lambda inside such an expression is numbered after its scope's other functions in
/// Roslyn), goto statements' effect on merging, variant interfaces.
/// </summary>
internal sealed partial class Driver
{
    internal enum ClosureKind { Singleton, General, ThisOnly, Static }

    internal sealed class CEnv
    {
        public readonly List<object> Vars = [];
        public bool IsStruct;
        public CEnv? Parent;
        public int Ordinal = -1;
        public required CScope Scope;
        public string Name = "";
    }

    internal sealed class CScope
    {
        public required object Node;
        public CScope? Parent;
        public CFn? Fn;
        public readonly List<CFn> Functions = [];
        public readonly List<CScope> Children = [];
        public readonly List<object> Declared = [];
        public CEnv? Env;
        public bool CanMerge = true;
    }

    internal sealed class CFn
    {
        public required IMethodSymbol Sym;
        public required SyntaxNode Syntax;
        public bool IsLambda;
        public readonly List<object> Captured = [];
        public readonly List<CEnv> Envs = [];
        public CEnv? Container;
        public bool CapturesThis;
        /// <summary>A lambda, or a local function made a delegate somewhere.</summary>
        public bool Converted;
        /// <summary>A lambda quoted as an expression tree (or one inside such a lambda): numbered, but no method.</summary>
        public bool InExpressionTree;
        // the results
        public ClosureKind Kind;
        public int Ordinal;
        public string Name = "";
        public List<CEnv> StructEnvs = [];
    }

    internal sealed class ClosureNames
    {
        public required IMethodSymbol Top;
        public int MethodOrdinal;
        public readonly Dictionary<SyntaxNode, CFn> Functions = [];
        public readonly List<CEnv> Envs = [];
        public readonly Dictionary<object, CEnv> EnvOfVar = new(VarComparer.Instance);
        /// <summary>The name of the singleton class of the lambdas that capture nothing (`&lt;&gt;c`, or
        /// `&lt;&gt;c__{ordinal}` in a generic method).</summary>
        public string SingletonName = "<>c";
        public bool Release;
    }

    /// <summary>Variables: symbols compared as symbols, `this` (ThisVar) by reference.</summary>
    sealed class VarComparer : IEqualityComparer<object>
    {
        public static readonly VarComparer Instance = new();
        public new bool Equals(object? a, object? b) => a is ISymbol x && b is ISymbol y ? SymbolEqualityComparer.Default.Equals(x, y) : ReferenceEquals(a, b);
        public int GetHashCode(object o) => o is ISymbol s ? SymbolEqualityComparer.Default.GetHashCode(s) : o.GetHashCode();
    }
    static readonly object ThisVar = new();

    readonly Dictionary<IMethodSymbol, ClosureNames?> _closureNames = new(SymbolEqualityComparer.Default);

    /// <summary>The method ordinal Roslyn gives a method (its index among its type's members).</summary>
    internal static int MemberOrdinal(IMethodSymbol def) => def.ContainingType.GetMembers().IndexOf(def);

    internal ClosureNames? NamesOf(IMethodSymbol outer)
    {
        var def = outer.OriginalDefinition;
        if (_closureNames.TryGetValue(def, out var n)) return n;
        n = null;
        var body = BodyOperation(this, def);
        IOperation? root = body switch
        {
            IMethodBodyOperation mb => (IOperation?)mb.BlockBody ?? mb.ExpressionBody,
            IConstructorBodyOperation cb => cb.BlockBody ?? cb.ExpressionBody,
            _ => body,
        };
        // a constructor runs its type's field and property initializers first (Roslyn compiles them into each
        // constructor that does not call another of its own: a lambda in one is a method of that constructor)
        var inits = new List<IOperation>();
        if (def.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor && def.ContainingType != null
            && !(body is IConstructorBodyOperation { Initializer: IInvocationOperation { TargetMethod: var tm } } && SymbolEqualityComparer.Default.Equals(tm.ContainingType, def.ContainingType)))
            foreach (var m in def.ContainingType.GetMembers())
                if (m is IFieldSymbol or IPropertySymbol && m.IsStatic == def.IsStatic)
                    foreach (var sr in m.DeclaringSyntaxReferences)
                        if (sr.GetSyntax() switch
                            {
                                Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax { Initializer: { } vi } => (SyntaxNode)vi,
                                Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax { Initializer: { } pi } => pi,
                                _ => null,
                            } is { } isyn && ModelFor(isyn.SyntaxTree).GetOperation(isyn) is { } iop && iop.DescendantsAndSelf().Any(o => o is IAnonymousFunctionOperation or ILocalFunctionOperation))
                            inits.Add(iop);
        if ((root != null || inits.Count > 0) && def.ContainingType != null)
        {
            var any = root ?? inits[0];
            bool release = ModelFor(any.Syntax.SyntaxTree).Compilation.Options.OptimizationLevel == OptimizationLevel.Release || s_checkRelease;
            n = new ClosureNames { Top = def, MethodOrdinal = MemberOrdinal(def), Release = release };
            var roots = new List<IOperation>(inits);
            if (body is IConstructorBodyOperation { Initializer: { } ci }) roots.Add(ci);
            if (root != null) roots.Add(root);
            new ClosureAnalysis(def, n).Run([.. roots], inits.Count);
        }
        _closureNames[def] = n;
        return n;
    }

    sealed class ClosureAnalysis(IMethodSymbol top, ClosureNames names)
    {
        CScope _cur = null!;
        CFn? _curFn;
        bool _inTree;
        readonly Dictionary<object, CScope> _localToScope = new(VarComparer.Instance);
        readonly HashSet<IMethodSymbol> _converted = new(SymbolEqualityComparer.Default);
        readonly Stack<CScope> _loopLevels = new();

        public void Run(IOperation[] roots, int initializers = 0)
        {
            // (a constructor's initializers are a scope of their own in it: Roslyn numbers their lambdas after
            // the body's own)
            object main = initializers == roots.Length ? new object() : roots[^1];
            var root = new CScope { Node = main };
            _cur = root;
            Declare(root, top.Parameters);
            if (!top.IsStatic) Declare(root, [ThisVar]);
            if (initializers > 0)
            {
                CreateAndPush(new object());
                for (int i = 0; i < initializers; i++) Visit(roots[i]);
                _cur = root;
            }
            for (int i = initializers; i < roots.Length; i++) Visit(roots[i]);
            if (Environment.GetEnvironmentVariable("IL2WASM_NAMES_DUMP") == "1") Dump(root, "");
            MakeAndAssignEnvironments(root);
            ComputeLambdaScopesAndFrameCaptures(root);
            if (names.Release) MergeEnvironments(root);
            InlineThisOnlyEnvironments(root);
            // the environments, numbered in a walk of the scopes
            int mo = names.MethodOrdinal;
            string arity = top.Arity > 0 ? "`" + top.Arity : "";
            if (top.IsGenericMethod) names.SingletonName = "<>c__" + mo + arity;
            int k = 0;
            VisitScopeTree(root, s =>
            {
                if (s.Env is not { } env) return;
                env.Ordinal = k++;
                env.Name = "<>c__DisplayClass" + mo + "_" + env.Ordinal + arity;
                names.Envs.Add(env);
                foreach (var v in env.Vars) names.EnvOfVar.TryAdd(v, env);
            });
            // the functions, numbered by the same walk
            int lo = 0;
            VisitNestedFunctions(root, (s, f) =>
            {
                if (f.Container != null) f.Kind = ClosureKind.General;
                else if (f.CapturesThis) f.Kind = ClosureKind.ThisOnly;
                else if (f.Envs.Count == 0 && f.IsLambda && _converted.Contains(f.Sym)) f.Kind = ClosureKind.Singleton;
                else f.Kind = ClosureKind.Static;
                f.StructEnvs = f.Envs.Where(e => e.IsStruct).ToList();
                f.Converted = _converted.Contains(f.Sym) || _converted.Contains(f.Sym.OriginalDefinition);
                f.Ordinal = lo++;
                string ids = (f.Kind == ClosureKind.General ? "" : mo + "_") + f.Ordinal;
                f.Name = f.IsLambda ? "<" + top.Name + ">b__" + ids : "<" + top.Name + ">g__" + f.Sym.Name + "|" + ids;
                names.Functions[f.Syntax] = f;
            });
        }

        void Dump(CScope s, string indent)
        {
            Console.Error.WriteLine(indent + "scope " + (s.Node is IOperation o ? o.Kind + " " + o.Syntax.ToString().Split('\n')[0][..Math.Min(40, o.Syntax.ToString().Split('\n')[0].Length)] : s.Node.GetType().Name) + " [" + string.Join(",", s.Declared.Select(v => v is ISymbol y ? y.Name : "this")) + "]");
            foreach (var f in s.Functions) Console.Error.WriteLine(indent + "  fn " + f.Syntax.ToString().Split('\n')[0][..Math.Min(40, f.Syntax.ToString().Split('\n')[0].Length)]);
            foreach (var c in s.Children) Dump(c, indent + "    ");
        }

        static bool Eq(ISymbol? a, ISymbol? b) => SymbolEqualityComparer.Default.Equals(a, b);

        void Declare(CScope s, IEnumerable<object> vars)
        {
            foreach (var v in vars) _localToScope.TryAdd(v, s);
        }

        void PushOrReuse(object node, IEnumerable<ISymbol> locals)
        {
            var l = locals.Cast<object>().ToList();
            if (l.Count > 0 && _cur.Node != node) CreateAndPush(node);
            Declare(_cur, l);
        }

        void CreateAndPush(object node)
        {
            var s = new CScope { Node = node, Parent = _cur, Fn = _curFn };
            // (a scope entered after a loop's head, at the loop's level, is jumped back over: it cannot merge)
            if (_loopLevels.Count > 0 && _loopLevels.Peek() == _cur) s.CanMerge = false;
            _cur.Children.Add(s);
            _cur = s;
        }

        void Pop(CScope s) { if (s != _cur) _cur = _cur.Parent!; }

        void InLoop(Action a)
        {
            _loopLevels.Push(_cur);
            a();
            _loopLevels.Pop();
        }

        void VisitChildren(IOperation op) { foreach (var c in op.ChildOperations) Visit(c); }

        void Visit(IOperation? op)
        {
            if (op == null) return;
            var old = _cur;
            switch (op)
            {
                case IAnonymousFunctionOperation af:
                    {
                        _converted.Add(af.Symbol);
                        // (a lambda quoted as an expression tree takes its number but is no method; its parameters,
                        // and those of the lambdas in it, are not variables a closure holds)
                        bool oldTree = _inTree;
                        _inTree |= af.Parent?.Type is INamedTypeSymbol { Name: "Expression", Arity: 1 } et && et.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions";
                        VisitNested(af.Symbol, af.Syntax, af.Body, lambda: true);
                        _inTree = oldTree;
                        return;
                    }
                case ILocalFunctionOperation lf:
                    VisitNested(lf.Symbol, lf.Syntax, lf.Body ?? lf.IgnoredBody, lambda: false);
                    return;
                case IBlockOperation b:
                    PushOrReuse(b, b.Locals);
                    VisitChildren(b);
                    Pop(old);
                    return;
                case ICatchClauseOperation c:
                    PushOrReuse(c, c.Locals);
                    VisitChildren(c);
                    Pop(old);
                    return;
                case IForLoopOperation f:
                    // { initializer; goto end; start: body; increment; end: if (condition) goto start; }
                    PushOrReuse(f, f.Locals);
                    foreach (var x in f.Before) Visit(x);
                    InLoop(() =>
                    {
                        Visit(f.Body);
                        foreach (var x in f.AtLoopBottom) Visit(x);
                        Visit(f.Condition);
                    });
                    Pop(old);
                    return;
                case IWhileLoopOperation w:
                    if (w.Locals.Length > 0)
                    {
                        // continue: { if (!condition) goto break; body; goto continue; }
                        InLoop(() =>
                        {
                            PushOrReuse(w, w.Locals);
                            if (w.ConditionIsTop) { Visit(w.Condition); Visit(w.Body); } else { Visit(w.Body); Visit(w.Condition); }
                            Pop(old);
                        });
                        return;
                    }
                    // goto continue; start: body; continue: if (condition) goto start;
                    InLoop(() => { Visit(w.Body); Visit(w.Condition); });
                    return;
                case IForEachLoopOperation fe:
                    // { temporaries = collection; ... { iteration variables; body } }
                    CreateAndPush(fe);
                    Visit(fe.Collection);
                    InLoop(() =>
                    {
                        var inner = _cur;
                        if (fe.Locals.Length > 0) { CreateAndPush(fe.LoopControlVariable); Declare(_cur, fe.Locals); }
                        Visit(fe.LoopControlVariable);
                        Visit(fe.Body);
                        Pop(inner);
                    });
                    Pop(old);
                    return;
                case IUsingOperation u:
                    // { resource = ...; try { body } finally { dispose } }
                    CreateAndPush(u);
                    Declare(_cur, u.Locals);
                    VisitChildren(u);
                    Pop(old);
                    return;
                case ILockOperation lk:
                    CreateAndPush(lk);
                    VisitChildren(lk);
                    Pop(old);
                    return;
                case ISwitchOperation sw:
                    CreateAndPush(sw);
                    Declare(_cur, sw.Locals);
                    foreach (var c in sw.Cases) Declare(_cur, c.Locals);
                    VisitChildren(sw);
                    Pop(old);
                    return;
                case ISwitchExpressionOperation se:
                    CreateAndPush(se);
                    foreach (var a in se.Arms) Declare(_cur, a.Locals);
                    VisitChildren(se);
                    Pop(old);
                    return;
                // (an interpolated string built by DefaultInterpolatedStringHandler: lowering holds the handler in a
                // temporary; a params collection's elements do not have one of their own)
                case IInterpolatedStringOperation ist when ist.Parts.Any(p => p is IInterpolationOperation { Alignment: not null } or IInterpolationOperation { FormatString: not null }
                        || p is IInterpolationOperation ip && ip.Expression.Type?.SpecialType != SpecialType.System_String) && ist.Type?.SpecialType == SpecialType.System_String:
                case IObjectCreationOperation { Initializer: not null }:
                case ICollectionExpressionOperation { IsImplicit: false }:
                    // (an initializer fills a temporary: a sequence that declares it)
                    CreateAndPush(op);
                    VisitChildren(op);
                    Pop(old);
                    return;
                case ILocalReferenceOperation l: AddIfCaptured(l.Local); break;
                // (a primary constructor's parameter, in another member, is a field of the object: `this` is captured)
                case IParameterReferenceOperation { Parameter.ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } pc } when !Eq(pc, top):
                    AddIfCaptured(ThisVar);
                    break;
                case IParameterReferenceOperation p: AddIfCaptured(p.Parameter); break;
                case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }:
                    if (!top.IsStatic) AddIfCaptured(ThisVar);
                    break;
                case IInvocationOperation { TargetMethod.MethodKind: MethodKind.LocalFunction } inv:
                    AddIfCaptured(inv.TargetMethod.OriginalDefinition);
                    break;
                case IMethodReferenceOperation { Method.MethodKind: MethodKind.LocalFunction } mr:
                    AddIfCaptured(mr.Method.OriginalDefinition);
                    _converted.Add(mr.Method.OriginalDefinition);
                    break;
            }
            VisitChildren(op);
        }

        void VisitNested(IMethodSymbol sym, SyntaxNode syntax, IBlockOperation? body, bool lambda)
        {
            var fn = new CFn { Sym = sym, Syntax = syntax, IsLambda = lambda, InExpressionTree = _inTree };
            _cur.Functions.Add(fn);
            if (body == null) return;
            var oldFn = _curFn; _curFn = fn;
            var old = _cur;
            CreateAndPush(body);
            if (!_inTree) Declare(_cur, sym.Parameters);
            Declare(_cur, body.Locals);
            VisitChildren(body);
            _cur = old;
            _curFn = oldFn;
        }

        ISymbol? ContainingOf(object v) => v switch
        {
            ILocalSymbol l => l.ContainingSymbol,
            IParameterSymbol p => p.ContainingSymbol,
            IMethodSymbol m => m.ContainingSymbol,
            _ => top,
        };

        void AddIfCaptured(object v)
        {
            if (_curFn == null) return;
            if (v is ILocalSymbol { IsConst: true }) return;
            if (v is IMethodSymbol m && Eq(_curFn.Sym.OriginalDefinition, m)) return;
            var cont = ContainingOf(v);
            if (Eq(cont, _curFn.Sym)) return;
            var scope = _cur;
            var fn = _curFn;
            while (fn != null && !Eq(cont, fn.Sym))
            {
                if (!fn.Captured.Contains(v, VarComparer.Instance)) fn.Captured.Add(v);
                while (scope.Fn == fn) scope = scope.Parent!;
                fn = scope.Fn;
            }
            if (v is IMethodSymbol) return;
            if (_localToScope.TryGetValue(v, out var ds) && !ds.Declared.Contains(v, VarComparer.Instance)) ds.Declared.Add(v);
        }

        static void VisitScopeTree(CScope s, Action<CScope> a)
        {
            a(s);
            foreach (var c in s.Children) VisitScopeTree(c, a);
        }

        static void VisitNestedFunctions(CScope s, Action<CScope, CFn> a)
        {
            foreach (var f in s.Functions) a(s, f);
            foreach (var c in s.Children) VisitNestedFunctions(c, a);
        }

        static bool CheckNestedFunctions(CScope s, Func<CScope, CFn, bool> f)
        {
            foreach (var fn in s.Functions) if (f(s, fn)) return true;
            foreach (var c in s.Children) if (CheckNestedFunctions(c, f)) return true;
            return false;
        }

        bool CanTakeRefParameters(IMethodSymbol f) => !f.IsAsync && !f.IsIterator && !_converted.Contains(f.OriginalDefinition) && !_converted.Contains(f);

        void MakeAndAssignEnvironments(CScope root)
        {
            VisitScopeTree(root, scope =>
            {
                if (scope.Declared.Count == 0) return;
                bool isStruct = true;
                var closures = new List<CFn>();
                bool added;
                do
                {
                    added = false;
                    VisitNestedFunctions(scope, (_, c) =>
                    {
                        if (!closures.Contains(c) && (c.Captured.Any(v => scope.Declared.Contains(v, VarComparer.Instance))
                            || c.Captured.Any(v => v is IMethodSymbol cm && closures.Any(x => Eq(x.Sym.OriginalDefinition, cm)))))
                        {
                            closures.Add(c);
                            added = true;
                            isStruct &= CanTakeRefParameters(c.Sym);
                        }
                    });
                } while (added);
                var env = new CEnv { Scope = scope, IsStruct = isStruct };
                env.Vars.AddRange(scope.Declared);
                scope.Env = env;
                foreach (var c in closures)
                {
                    c.Envs.Add(env);
                    if (env.Vars.Contains(ThisVar)) c.CapturesThis = true;
                }
            });
        }

        static void ComputeLambdaScopesAndFrameCaptures(CScope root)
        {
            VisitNestedFunctions(root, (scope, f) =>
            {
                if (f.Envs.Count == 0) return;
                var captured = new List<CEnv>(f.Envs);
                var cur = scope;
                while (cur != null)
                {
                    var env = cur.Env;
                    if (env != null && captured.Remove(env) && !env.IsStruct) { f.Container = env; break; }
                    cur = cur.Parent;
                }
                var oldEnv = cur?.Env;
                cur = cur?.Parent;
                while (cur != null && captured.Count > 0)
                {
                    var env = cur.Env;
                    if (env != null)
                    {
                        if (!env.IsStruct) { oldEnv!.Parent = env; oldEnv = env; }
                        captured.Remove(env);
                    }
                    cur = cur.Parent;
                }
            });
        }

        void InlineThisOnlyEnvironments(CScope root)
        {
            if (top.IsStatic) return;
            var env = root.Env;
            if (env == null || env.Vars.Count > 1 || !env.Vars.Contains(ThisVar)) return;
            if (env.IsStruct)
            {
                if (!CheckNestedFunctions(root, (_, c) => c.Envs.Contains(env) && c.Container != null)) RemoveEnv();
            }
            else
            {
                RemoveEnv();
                VisitNestedFunctions(root, (_, c) => { if (c.Container == env) c.Container = null; });
                // (environments that captured it capture `this` now)
                VisitScopeTree(root, s => { if (s.Env?.Parent == env) s.Env.Parent = null; });
            }
            void RemoveEnv()
            {
                root.Env = null;
                VisitNestedFunctions(root, (_, c) => c.Envs.Remove(env));
            }
        }

        void MergeEnvironments(CScope root)
        {
            // the functions that capture each scope's environment, directly or through the ones nested in it
            var capturing = new Dictionary<CScope, HashSet<CFn>>();
            var envScope = new Dictionary<CEnv, CScope>();
            var order = new List<CScope>();
            VisitScopeTree(root, scope =>
            {
                if (scope.Env != null) { capturing[scope] = []; envScope[scope.Env] = scope; order.Add(scope); }
                foreach (var c in scope.Functions)
                    foreach (var e in c.Envs)
                        if (envScope.TryGetValue(e, out var es)) capturing[es].Add(c);
            });
            foreach (var scope in order)
            {
                var set = capturing[scope];
                var cs = scope;
                while (cs.Env == null || cs.Env.Parent != null)
                {
                    cs = cs.Parent;
                    if (cs == null) break;
                    if (cs.Env == null || cs.Env.IsStruct) continue;
                    capturing[cs].UnionWith(set);
                }
            }
            foreach (var scope in order)
            {
                var set = capturing[scope];
                if (set.Count == 0) continue;
                var scopeEnv = scope.Env!;
                if (scopeEnv.IsStruct) continue;
                var best = scope;
                var cur = scope;
                while (cur.Parent != null)
                {
                    if (!cur.CanMerge) break;
                    var parent = cur.Parent;
                    var env = parent.Env;
                    if (env == null || env.IsStruct) { cur = parent; continue; }
                    if (!capturing[parent].SetEquals(set)) break;
                    best = parent;
                    cur = parent;
                }
                if (best == scope) continue;
                var target = best.Env!;
                foreach (var v in scopeEnv.Vars) if (!target.Vars.Contains(v, VarComparer.Instance)) target.Vars.Add(v);
                scope.Env = null;
                foreach (var c in set)
                {
                    c.Envs.Remove(scopeEnv);
                    if (!c.Envs.Contains(target)) c.Envs.Add(target);
                    if (c.Container == scopeEnv) c.Container = target;
                }
            }
        }
    }

    /// <summary>The C# compiler's name for a closure (display) class: the environment of its first variable;
    /// full: nested in the method's type with '+'.</summary>
    internal string? RoslynDisplayName(Display d, bool full)
    {
        if (d.RoslynShort != null) return full ? d.RoslynFull : d.RoslynShort;
        if (d.Owner is not FunctionCompiler fc) return null;
        var outer = fc.OuterMethod;
        var names = NamesOf(outer);
        var v = d.Vars.FirstOrDefault(x => x is ILocalSymbol or IParameterSymbol);
        if (names == null || v == null || !names.EnvOfVar.TryGetValue(v.OriginalDefinition, out var env) && !names.EnvOfVar.TryGetValue(v, out env)) return null;
        return full ? ClrPath(outer.ContainingType.OriginalDefinition) + "+" + env.Name : env.Name;
    }

    /// <summary>The lambda's or local function's place in Roslyn's closure conversion, when the analysis knows it.</summary>
    internal CFn? NestedFunctionOf(IMethodSymbol outer, SyntaxNode syntax) => NamesOf(outer) is { } n && n.Functions.TryGetValue(syntax, out var f) ? f : null;

    /// <summary>The type a lambda's or local function's method is a member of, as its frames name it (the outer
    /// method's type, with the singleton's or the environment's class).</summary>
    internal string NestedContainerSuffix(ClosureNames names, CFn f) => f.Kind switch
    {
        ClosureKind.Singleton => names.SingletonName,
        ClosureKind.General => f.Container!.Name,
        _ => "",
    };

    /// <summary>The frame name of a lambda or local function, as the C# compiler names its method.</summary>
    internal string RoslynNestedFrameName(FunctionCompiler owner, SyntaxNode syntax, IMethodSymbol sym, bool localFunction)
    {
        var outer = owner.OuterMethod;
        var names = NamesOf(outer);
        if (names == null || !names.Functions.TryGetValue(syntax, out var f))
            return NestedFrameName(owner, sym, localFunction ? "g__" + sym.Name + "|0_0" : "b__0");
        var sb = new StringBuilder();
        AppendTypeFull(sb, outer.ContainingType);
        var c = NestedContainerSuffix(names, f);
        if (c.Length > 0) sb.Append('.').Append(c);
        sb.Append('.').Append(f.Name);
        sb.Append('(');
        for (int i = 0; i < sym.Parameters.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(ShortTypeName(sym.Parameters[i].Type));
            if (sym.Parameters[i].RefKind != RefKind.None) sb.Append('&');
            sb.Append(' ').Append(sym.Parameters[i].Name);
        }
        // a local function's captured variables come in structs, by reference
        for (int i = 0; i < f.StructEnvs.Count; i++)
        {
            if (i > 0 || sym.Parameters.Length > 0) sb.Append(", ");
            sb.Append(f.StructEnvs[i].Name).Append('&');
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>The name of a state machine's type: `&lt;M&gt;d__{ordinal}` (a lambda's or local function's:
    /// `&lt;{its name}&gt;d`), nested in the type its method is a member of.</summary>
    internal (string shortName, string full)? StateMachineName(FunctionCompiler owner, IMethodSymbol m)
    {
        var def = m.OriginalDefinition;
        if (def.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction)
        {
            var outer = owner.OuterMethod;
            var syntax = def.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (NamesOf(outer) is not { } names || syntax == null || !names.Functions.TryGetValue(syntax, out var f)) return null;
            var c = NestedContainerSuffix(names, f);
            var sn = "<" + f.Name + ">d";
            return (sn, ClrPath(outer.ContainingType.OriginalDefinition) + (c.Length > 0 ? "+" + c : "") + "+" + sn);
        }
        if (def.ContainingType == null) return null;
        var n = "<" + def.Name.Replace('.', '-') + ">d__" + MemberOrdinal(def) + (def.Arity > 0 ? "`" + def.Arity : "");
        return (n, ClrPath(def.ContainingType.OriginalDefinition) + "+" + n);
    }
}
