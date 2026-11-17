// Expression trees with try/catch/finally, compiled and run. The expression interpreter catches with
// `catch (Exception e) when (handlers.HasHandler(frame, e, out var handler, out var unwrapped))` and uses both in the
// handler; this program has no exception filter of its own, so the library's run in one pass.
using System;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
public static class Program
{
    static void T(string name, Func<object?> f)
    {
        try { Console.WriteLine(name + ": " + (f() ?? "null")); } catch (Exception e) { Console.WriteLine(name + " threw " + e.GetType().Name + " " + e.Message); }
    }
    public static string Boom() => throw new InvalidOperationException("boom");
    public static void BoomVoid() => throw new InvalidOperationException("boom");
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var call = Expression.Call(typeof(Program).GetMethod("Boom")!);
        var thrower = Expression.Block(Expression.Throw(Expression.New(typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!, Expression.Constant("thrown"))), Expression.Constant("unreached"));
        var write = Expression.Call(typeof(Console).GetMethod("WriteLine", new[] { typeof(string) })!, Expression.Constant("finally"));
        var ex = Expression.Parameter(typeof(Exception), "ex");
        var text = Expression.Call(ex, typeof(object).GetMethod("ToString")!);
        T("call, catch type", () => Expression.Lambda<Func<string>>(Expression.TryCatch(call, Expression.Catch(typeof(InvalidOperationException), Expression.Constant("caught")))).Compile()());
        T("call, catch Exception", () => Expression.Lambda<Func<string>>(Expression.TryCatch(call, Expression.Catch(typeof(Exception), Expression.Constant("caught")))).Compile()());
        T("void", () => { Expression.Lambda<Action>(Expression.TryCatch(Expression.Call(typeof(Program).GetMethod("BoomVoid")!), Expression.Catch(typeof(Exception), Expression.Empty()))).Compile()(); return "done"; });
        T("catch variable", () => Expression.Lambda<Func<string>>(Expression.TryCatch(call, Expression.Catch(ex, text))).Compile()().Split('\n')[0].Trim());
        T("throw, catch", () => Expression.Lambda<Func<string>>(Expression.TryCatch(thrower, Expression.Catch(typeof(InvalidOperationException), Expression.Constant("caught")))).Compile()());
        T("no throw", () => Expression.Lambda<Func<string>>(Expression.TryCatch(Expression.Constant("fine"), Expression.Catch(typeof(InvalidOperationException), Expression.Constant("caught")))).Compile()());
        T("try-finally", () => Expression.Lambda<Func<string>>(Expression.TryFinally(Expression.Constant("body"), write)).Compile()());
        T("try-finally throw", () => Expression.Lambda<Func<string>>(Expression.TryFinally(thrower, write)).Compile()());
        T("filter true", () => Expression.Lambda<Func<string>>(Expression.TryCatch(call, Expression.Catch(ex, Expression.Constant("filtered"), Expression.TypeIs(ex, typeof(InvalidOperationException))))).Compile()());
        T("filter false", () => Expression.Lambda<Func<string>>(Expression.TryCatch(call, Expression.Catch(ex, Expression.Constant("filtered"), Expression.TypeIs(ex, typeof(ArgumentException))))).Compile()());
        T("try-catch-finally", () => Expression.Lambda<Func<string>>(Expression.TryCatchFinally(thrower, write, Expression.Catch(typeof(InvalidOperationException), Expression.Constant("caught")))).Compile()());
        T("try-catch-finally no throw", () => Expression.Lambda<Func<string>>(Expression.TryCatchFinally(Expression.Constant("fine"), write, Expression.Catch(typeof(InvalidOperationException), Expression.Constant("caught")))).Compile()());
        T("uncaught", () => Expression.Lambda<Func<string>>(Expression.TryCatchFinally(thrower, write, Expression.Catch(typeof(ArgumentException), Expression.Constant("caught")))).Compile()());
        T("two catches", () => Expression.Lambda<Func<string>>(Expression.TryCatch(call, Expression.Catch(typeof(ArgumentException), Expression.Constant("first")), Expression.Catch(typeof(Exception), Expression.Constant("second")))).Compile()());
        T("rethrow", () => Expression.Lambda<Func<string>>(Expression.TryCatch(call, Expression.Catch(typeof(Exception), Expression.Block(Expression.Rethrow(), Expression.Constant("after"))))).Compile()());
        T("nested", () => Expression.Lambda<Func<string>>(Expression.TryCatch(
            Expression.TryCatchFinally(call, write, Expression.Catch(typeof(ArgumentException), Expression.Constant("inner"))),
            Expression.Catch(typeof(InvalidOperationException), Expression.Constant("outer")))).Compile()());
    }
}
