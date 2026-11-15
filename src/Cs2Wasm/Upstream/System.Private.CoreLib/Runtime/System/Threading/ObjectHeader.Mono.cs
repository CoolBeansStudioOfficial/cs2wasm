// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono reads its object header's sync word here (thin locks, hash codes). cs2wasm's header is
// [count, type id, hash code] (section 7.3: the hash code comes from a per-instance counter in allocation order, and
// stays with the object when it joins an arena); locks are Monitor's table (Monitor.Mono.cs).

namespace System.Threading
{
    internal static class ObjectHeader
    {
        /// <summary>Every object has its hash code from allocation.</summary>
        public static bool TryGetHashCode(object? o, out int hash)
        {
            hash = o == null ? 0 : Cs2Wasm.Runtime.Rt.HashOf(o);
            return true;
        }
    }
}
