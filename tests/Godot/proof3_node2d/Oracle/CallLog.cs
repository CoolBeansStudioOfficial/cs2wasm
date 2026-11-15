// The oracle's call log (proof 3): installed when stock Godot loads the game assembly, it wraps entries of stock
// GodotSharp's engine-callback table (NativeFuncs._unmanagedCallbacks: the method-bind lookup, ptrcall, print) and
// writes one line per engine call, with values formatted by their types in extension_api.json (so the line is the
// same whatever the pointer size). Only in the oracle build: the compiled module's host writes the same lines.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Godot.NativeInterop;

internal static unsafe class CallLog
{
    static delegate* unmanaged<IntPtr, IntPtr, void**, void*, void> s_ptrcall;
    static delegate* unmanaged<godot_string_name*, godot_string_name*, ulong, IntPtr> s_getMethod;
    static delegate* unmanaged<godot_string*, void> s_print;
    static readonly Dictionary<IntPtr, (string cls, string name)> s_binds = [];
    static readonly Dictionary<string, (string[] args, string? ret)> s_api = [];
    static TextWriter? s_out;

    [ModuleInitializer]
    internal static void Install()
    {
        var path = Environment.GetEnvironmentVariable("CS2WASM_CALL_LOG");
        var api = Environment.GetEnvironmentVariable("CS2WASM_GODOT_API");
        if (path == null || api == null) return;
        s_out = new StreamWriter(path) { AutoFlush = true };
        var lookups = new List<(string cls, string name, ulong hash)>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(api)))
            foreach (var c in doc.RootElement.GetProperty("classes").EnumerateArray())
                if (c.TryGetProperty("methods", out var ms))
                    foreach (var m in ms.EnumerateArray())
                    {
                        var args = new List<string>();
                        if (m.TryGetProperty("arguments", out var a))
                            foreach (var x in a.EnumerateArray()) args.Add(TypeOf(x));
                        string? ret = m.TryGetProperty("return_value", out var r) ? TypeOf(r) : null;
                        var cn = c.GetProperty("name").GetString()!;
                        var mn = m.GetProperty("name").GetString()!;
                        s_api[cn + "." + mn] = (args.ToArray(), ret);
                        if (c.GetProperty("api_type").GetString() == "core" && !m.GetProperty("is_virtual").GetBoolean() && m.TryGetProperty("hash", out var h))
                            lookups.Add((cn, mn, h.GetUInt64()));
                    }

        var table = typeof(NativeFuncs).GetField("_unmanagedCallbacks", BindingFlags.NonPublic | BindingFlags.Static)!;
        object box = table.GetValue(null)!;
        IntPtr Swap(string name, IntPtr with)
        {
            var f = box.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            var old = (IntPtr)f.GetValue(box)!;
            f.SetValue(box, with);
            return old;
        }
        s_ptrcall = (delegate* unmanaged<IntPtr, IntPtr, void**, void*, void>)Swap("godotsharp_method_bind_ptrcall",
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void**, void*, void>)&Ptrcall);
        s_getMethod = (delegate* unmanaged<godot_string_name*, godot_string_name*, ulong, IntPtr>)Swap("godotsharp_method_bind_get_method_with_compatibility",
            (IntPtr)(delegate* unmanaged<godot_string_name*, godot_string_name*, ulong, IntPtr>)&GetMethod);
        s_print = (delegate* unmanaged<godot_string*, void>)Swap("godotsharp_print", (IntPtr)(delegate* unmanaged<godot_string*, void>)&Print);
        table.SetValue(null, box);
        // the binds GodotSharp looked up before this assembly loaded (Node2D's are): each is one object per method, so
        // looking them all up again names them
        foreach (var (cn, mn, hash) in lookups)
        {
            godot_string_name c = NewName(cn), m = NewName(mn);
            var p = s_getMethod(&c, &m, hash);
            if (p != IntPtr.Zero) s_binds.TryAdd(p, (cn, mn));
            NativeFuncs.godotsharp_string_name_destroy(ref c);
            NativeFuncs.godotsharp_string_name_destroy(ref m);
        }
    }

    static godot_string_name NewName(string s)
    {
        var gs = Marshaling.ConvertStringToNative(s);
        NativeFuncs.godotsharp_string_name_new_from_string(out godot_string_name n, gs);
        NativeFuncs.godotsharp_string_destroy(ref gs);
        return n;
    }

    static string TypeOf(JsonElement e)
    {
        var t = e.GetProperty("type").GetString()!;
        return e.TryGetProperty("meta", out var meta) ? t + ":" + meta.GetString() : t;
    }

    static string Name(godot_string_name* n)
    {
        NativeFuncs.godotsharp_string_name_as_string(out godot_string s, *n);
        var r = Marshaling.ConvertStringToManaged(s);
        NativeFuncs.godotsharp_string_destroy(ref s);
        return r;
    }

    [UnmanagedCallersOnly]
    static IntPtr GetMethod(godot_string_name* cls, godot_string_name* method, ulong hash)
    {
        var p = s_getMethod(cls, method, hash);
        s_binds[p] = (Name(cls), Name(method));
        return p;
    }

    [UnmanagedCallersOnly]
    static void Print(godot_string* s)
    {
        s_out!.WriteLine("print " + Marshaling.ConvertStringToManaged(*s));
        s_print(s);
    }

    [UnmanagedCallersOnly]
    static void Ptrcall(IntPtr bind, IntPtr instance, void** args, void* ret)
    {
        var (cls, name) = s_binds.TryGetValue(bind, out var b) ? b : ("?", "?");
        var sig = s_api.TryGetValue(cls + "." + name, out var s) ? s : ([], null);
        var line = cls + "." + name + "(";
        for (int i = 0; i < sig.args.Length; i++) line += (i > 0 ? ", " : "") + Format(sig.args[i], args[i]);
        line += ")";
        s_ptrcall(bind, instance, args, ret);
        if (sig.ret != null && ret != null) line += " -> " + Format(sig.ret, ret);
        s_out!.WriteLine(line);
    }

    // a value in ptrcall's encoding: bool a byte, int an int64, float a double, real vectors floats, an object a pointer
    static string Format(string type, void* p)
    {
        string F(float x) => x.ToString("R", CultureInfo.InvariantCulture);
        string D(double x) => x.ToString("R", CultureInfo.InvariantCulture);
        var t = type.Split(':')[0];
        switch (t)
        {
            case "bool": return *(byte*)p != 0 ? "true" : "false";
            case "int": return (*(long*)p).ToString(CultureInfo.InvariantCulture);
            case "float": return D(*(double*)p);
            case "Vector2": return "(" + F(((float*)p)[0]) + ", " + F(((float*)p)[1]) + ")";
            case "Vector2i": return "(" + ((int*)p)[0] + ", " + ((int*)p)[1] + ")";
            case "Color": return "(" + F(((float*)p)[0]) + ", " + F(((float*)p)[1]) + ", " + F(((float*)p)[2]) + ", " + F(((float*)p)[3]) + ")";
            case "String": return "\"" + Marshaling.ConvertStringToManaged(*(godot_string*)p) + "\"";
            case "StringName": return "&\"" + Name((godot_string_name*)p) + "\"";
        }
        if (t.StartsWith("enum::") || t.StartsWith("bitfield::")) return (*(long*)p).ToString(CultureInfo.InvariantCulture);
        // an object: which one differs between runs, so only whether there is one
        return *(IntPtr*)p == IntPtr.Zero ? "null" : "<" + t + ">";
    }
}
