// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's runtime half of MulticastDelegate. Combining, removing and listing are Delegate's
// (Delegate.Mono.cs), over the invocation list the compiler's invoke thunk walks.

namespace System
{
    public abstract partial class MulticastDelegate
    {
        // (equal: the same type, and the same targets and methods in the same order; Delegate.Mono.cs)
        public sealed override bool Equals([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] object? obj) => base.Equals(obj);
        public sealed override int GetHashCode() => base.GetHashCode();
    }
}
