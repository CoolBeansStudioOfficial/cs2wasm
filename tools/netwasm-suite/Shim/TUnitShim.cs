// il2wasm's stand-in for the part of TUnit's API that NetWasm's tests/libraries suites use (MIT, il2wasm's own code; not
// TUnit's). The suites are compiled against it, not against TUnit, so a case is a plain console program that il2wasm
// can compile: the generated Main (tools/netwasm-suite/Extract) calls each [Test] method in order and prints whether it
// passed. The .NET run and the module run the same IL, so what matters is that an assertion checks what it says, the
// same way on both sides; its failure message names the values it compared.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace TUnit.Core
{
    [AttributeUsage(AttributeTargets.Method)] public sealed class TestAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public sealed class ArgumentsAttribute(params object?[] values) : Attribute { public object?[] Values { get; } = values; }
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly)] public sealed class NotInParallelAttribute : Attribute { public NotInParallelAttribute() { } public NotInParallelAttribute(params string[] keys) { } }
}

namespace TUnit.Core.Helpers { }

namespace TUnit.Assertions
{
    public sealed class AssertionException(string message) : Exception(message);

    public interface IAssertionSource<out T>
    {
        T Value { get; }
        string Expression { get; }
    }

    internal sealed class ValueSource<T>(T value, string expression) : IAssertionSource<T>
    {
        public T Value { get; } = value;
        public string Expression { get; } = expression;
    }

    public sealed class DelegateSource(Func<Task> body)
    {
        public async Task<TException> Throws<TException>() where TException : Exception
        {
            try { await body(); }
            catch (TException e) { return e; }
            catch (Exception e) { throw new AssertionException($"expected {typeof(TException).FullName} but {e.GetType().FullName} was thrown: {e.Message}"); }
            throw new AssertionException($"expected {typeof(TException).FullName} but nothing was thrown");
        }
    }

    public static class Assert
    {
        public static IAssertionSource<T> That<T>(T value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string expression = "") => new ValueSource<T>(value, expression);
        public static DelegateSource That(Action body) => new(() => { body(); return Task.CompletedTask; });
        public static DelegateSource That(Func<Task> body) => new(body);
        public static DelegateSource That<T>(Func<Task<T>> body) => new(async () => { await body(); });
        public static DelegateSource That<T>(Func<T> body) => new(() => { body(); return Task.CompletedTask; });

        public static void Fail(string message) => throw new AssertionException(message);

        public static TException Throws<TException>(Action body) where TException : Exception
        {
            try { body(); }
            catch (TException e) { return e; }
            catch (Exception e) { throw new AssertionException($"expected {typeof(TException).FullName} but {e.GetType().FullName} was thrown: {e.Message}"); }
            throw new AssertionException($"expected {typeof(TException).FullName} but nothing was thrown");
        }

        public static TException Throws<TException>(Func<object?> body) where TException : Exception => Throws<TException>(() => { body(); });

        public static async Task<TException> ThrowsAsync<TException>(Func<Task> body) where TException : Exception
        {
            try { await body(); }
            catch (TException e) { return e; }
            catch (Exception e) { throw new AssertionException($"expected {typeof(TException).FullName} but {e.GetType().FullName} was thrown: {e.Message}"); }
            throw new AssertionException($"expected {typeof(TException).FullName} but nothing was thrown");
        }

        internal static string Show(object? value)
        {
            if (value is null) return "null";
            if (value is string s) return "\"" + s + "\"";
            if (value is IFormattable f) return f.ToString(null, CultureInfo.InvariantCulture);
            if (value is IEnumerable e)
            {
                var b = new StringBuilder("[");
                var n = 0;
                foreach (var x in e) { if (n++ > 0) b.Append(", "); if (n > 32) { b.Append("..."); break; } b.Append(Show(x)); }
                return b.Append(']').ToString();
            }
            return value.ToString() ?? "";
        }

        internal static Task Check(bool ok, string expression, Func<string> failure) =>
            ok ? Task.CompletedTask : throw new AssertionException($"{expression}: {failure()}");
    }

    public static class AssertionExtensions
    {
        public static Task IsEqualTo<T>(this IAssertionSource<T> a, T expected) =>
            Assert.Check(EqualityComparer<T>.Default.Equals(a.Value, expected), a.Expression, () => $"expected {Assert.Show(expected)} but was {Assert.Show(a.Value)}");

        public static Task IsNotEqualTo<T>(this IAssertionSource<T> a, T other) =>
            Assert.Check(!EqualityComparer<T>.Default.Equals(a.Value, other), a.Expression, () => $"expected not {Assert.Show(other)}");

        public static Task IsTrue(this IAssertionSource<bool> a) => Assert.Check(a.Value, a.Expression, () => "expected true but was false");
        public static Task IsFalse(this IAssertionSource<bool> a) => Assert.Check(!a.Value, a.Expression, () => "expected false but was true");
        public static Task IsTrue(this IAssertionSource<bool?> a) => Assert.Check(a.Value == true, a.Expression, () => $"expected true but was {Assert.Show(a.Value)}");
        public static Task IsFalse(this IAssertionSource<bool?> a) => Assert.Check(a.Value == false, a.Expression, () => $"expected false but was {Assert.Show(a.Value)}");

        public static Task IsNull<T>(this IAssertionSource<T> a) => Assert.Check(a.Value is null, a.Expression, () => $"expected null but was {Assert.Show(a.Value)}");
        public static Task IsNotNull<T>(this IAssertionSource<T> a) => Assert.Check(a.Value is not null, a.Expression, () => "expected not null");

        public static Task Contains(this IAssertionSource<string?> a, string expected) =>
            Assert.Check(a.Value is not null && a.Value.Contains(expected, StringComparison.Ordinal), a.Expression, () => $"expected to contain {Assert.Show(expected)} but was {Assert.Show(a.Value)}");

        public static Task Contains<TItem>(this IAssertionSource<IEnumerable<TItem>> a, TItem expected)
        {
            var found = false;
            foreach (var x in a.Value) if (EqualityComparer<TItem>.Default.Equals(x, expected)) { found = true; break; }
            return Assert.Check(found, a.Expression, () => $"expected to contain {Assert.Show(expected)} but was {Assert.Show(a.Value)}");
        }

        public static Task IsLessThan<T>(this IAssertionSource<T> a, T other) where T : IComparable<T> =>
            Assert.Check(a.Value.CompareTo(other) < 0, a.Expression, () => $"expected less than {Assert.Show(other)} but was {Assert.Show(a.Value)}");
        public static Task IsGreaterThanOrEqualTo<T>(this IAssertionSource<T> a, T other) where T : IComparable<T> =>
            Assert.Check(a.Value.CompareTo(other) >= 0, a.Expression, () => $"expected at least {Assert.Show(other)} but was {Assert.Show(a.Value)}");
    }
}
