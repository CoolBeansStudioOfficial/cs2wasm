using System;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

static class Program
{
    static string Hex(byte[] b) => Convert.ToHexString(b);

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        foreach (var s in new[] { "", "abc", new string('a', 1000), "The quick brown fox jumps over the lazy dog" })
        {
            var d = Encoding.UTF8.GetBytes(s);
            Console.WriteLine(Hex(SHA1.HashData(d)) + " " + Hex(SHA256.HashData(d)));
            Console.WriteLine(Hex(SHA384.HashData(d)));
            Console.WriteLine(Hex(SHA512.HashData(d)));
            using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int i = 0; i < d.Length; i += 7) inc.AppendData(d, i, Math.Min(7, d.Length - i));
            using var h = SHA1.Create();
            Console.WriteLine(Hex(inc.GetHashAndReset()) + " " + Hex(h.ComputeHash(d)) + " " + h.HashSize);
        }
        ImmutableArray<int> ia = [1, 2, 3];
        ImmutableList<string> il = ["a", .. new[] { "b", "c" }];
        ImmutableArray<int> empty = [];
        Console.WriteLine(ia.Length + " " + string.Join(",", il) + " " + empty.IsEmpty + " " + (Guid.NewGuid() != Guid.Empty));
    }
}
