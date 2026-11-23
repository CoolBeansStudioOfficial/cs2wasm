// il2wasm: System.Native's environment and command line (SPEC section 8: every module gets the platform .NET's browser
// target gets: environment variables, as the host gives them). The host writes the block ("name=value", each ended by a
// zero byte, UTF-8) and the arguments (a string block); Environment reads the block once, as upstream's Unix code does
// with environ.
using System;
using System.Runtime.InteropServices;
using Il2Wasm.Runtime;

internal static partial class Interop
{
    internal static unsafe partial class Sys
    {
        static class Env
        {
            /// <summary>The environment block into the buffer: its length in bytes (all of it when size is enough).</summary>
            [Import("il2wasm", "env_block")] public static extern int Block(byte* buffer, int size);
        }

        // (the pointer array's first entry is the block itself, freed with it)
        internal static unsafe partial byte** GetEnviron()
        {
            int n = Env.Block(null, 0);
            byte* data = (byte*)NativeMemory.Alloc((nuint)(n + 1));
            if (n > 0) Env.Block(data, n);
            data[n] = 0;
            int count = 0;
            for (int i = 0; i < n; i++) if (data[i] == 0) count++;
            byte** all = (byte**)NativeMemory.Alloc((nuint)((count + 2) * sizeof(byte*)));
            all[0] = data;
            int k = 1;
            for (int i = 0; i < n; )
            {
                all[k++] = data + i;
                while (i < n && data[i] != 0) i++;
                i++;
            }
            all[k] = null;
            return all + 1;
        }

        internal static unsafe partial void FreeEnviron(byte** environ)
        {
            NativeMemory.Free(environ[-1]);
            NativeMemory.Free(environ - 1);
        }
    }
}

namespace Il2Wasm.Runtime
{
    internal static unsafe class CommandLine
    {
        /// <summary>The program's command line (its name first, as Environment.GetCommandLineArgs gives it) as a string
        /// block (count, then each string's length and characters) into the buffer: its length in characters.</summary>
        [Import("il2wasm", "args_block")] static extern int Block(char* buffer, int size);

        public static string[] Args()
        {
            int n = Block(null, 0);
            if (n <= 0) return [""];
            char[] b = new char[n];
            fixed (char* p = b) Block(p, n);
            var r = new string[b[0]];
            int k = 1;
            for (int i = 0; i < r.Length; i++) { int len = b[k++]; r[i] = new string(b, k, len); k += len; }
            return r;
        }
    }
}
