// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Runtime.CompilerServices;

namespace System.IO
{
    public abstract partial class Stream
    {
        [Intrinsic]
                private bool HasOverriddenBeginEndRead() => false;

        [Intrinsic]
                private bool HasOverriddenBeginEndWrite() => false;
    }
}
