namespace Il2Wasm.Runtime
{
    /// <summary>What Mono decides at run time by reflection (MakeGenericType) and il2wasm decides when it compiles an
    /// instantiation: which closed generic type serves a T. The tests fold to constants, so only the side taken is
    /// compiled; New creates `new Open&lt;T&gt;()` for an open generic class named by its metadata name.</summary>
    internal static class Generics
    {
        /// <summary>T implements the generic interface (metadata name, e.g. "System.IEquatable`1") of itself.</summary>
        [Intrinsic] public static extern bool Implements<T>(string genericInterfaceOfT);
        [Intrinsic] public static extern bool IsNullable<T>();
        [Intrinsic] public static extern bool IsEnum<T>();
        /// <summary>new Open&lt;T&gt;() (a parameterless constructor).</summary>
        [Intrinsic] public static extern object New<T>(string openGenericClass);
        /// <summary>new Open&lt;U&gt;() for T = Nullable&lt;U&gt;.</summary>
        [Intrinsic] public static extern object NewOfNullableArg<T>(string openGenericClass);
        /// <summary>new Open&lt;T1, T2&gt;().</summary>
        [Intrinsic] public static extern object New<T1, T2>(string openGenericClass);

        /// <summary>The compiler's generic equality for a struct without Equals(T) (records, tuples, anonymous types:
        /// EqualityComparer&lt;T&gt;.Default's ObjectEqualityComparer): Equals(object) on the value.</summary>
        public static bool SlowEquals<T>(T a, T b) => a!.Equals((object?)b);

    }
}
