// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

//
// Safe wrapper for a string and its UTF8 encoding
//
// Authors:
//   Aleksey Kliger <aleksey@xamarin.com>
//   Rodrigo Kumpera <kumpera@xamarin.com>
//
//

using System;
using System.Runtime.CompilerServices;

namespace Mono
{
    internal struct SafeStringMarshal : IDisposable
    {
        private readonly string? str;
        private IntPtr marshaled_string;

                private static IntPtr StringToUtf8_icall(ref string str) => throw Cs2Wasm.Runtime.TypeInfo.NotYet("StringToUtf8_icall");

        public static IntPtr StringToUtf8(string str)
        {
            return StringToUtf8_icall(ref str);
        }

                public static void GFree(IntPtr ptr) { }

        public SafeStringMarshal(string? str)
        {
            this.str = str;
            this.marshaled_string = IntPtr.Zero;
        }

        public IntPtr Value
        {
            get
            {
                if (marshaled_string == IntPtr.Zero && str != null)
                    marshaled_string = StringToUtf8(str);
                return marshaled_string;
            }
        }

        public void Dispose()
        {
            if (marshaled_string != IntPtr.Zero)
            {
                GFree(marshaled_string);
                marshaled_string = IntPtr.Zero;
            }
        }
    }
}
