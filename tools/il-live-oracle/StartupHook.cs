// The .NET oracle for live objects per type at exit (docs/MEMORY-IR.md 9.2), as a startup hook:
//   DOTNET_STARTUP_HOOKS=<path>/il-live-oracle.dll IL2WASM_LIVE_OUT=live.json <program>
// The hook runs the program's entry point itself (at ProcessExit the runtime no longer delivers its events), then:
// GC.Collect, WaitForPendingFinalizers, GC.Collect, and the runtime's heap walk: an EventPipe session of the runtime's
// provider with the GCHeapCollect keyword forces a collection that reports every live object (GCBulkNode) and its
// type (GCBulkType): the events dotnet-gcdump reads. The session is CoreLib's own in-process one (EventPipeInternal,
// what EventListener uses, by reflection: EventListener drops the events' arrays), so no package is needed.
// Written: {"nodes": n, "types": {"<type name>": count}}. IL2WASM_LIVE_DEBUG=1: what it found, on stderr.
// Run it on the homelab only (never .NET cases on the desk).
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

internal static class StartupHook
{
    public static void Initialize()
    {
        var file = Environment.GetEnvironmentVariable("IL2WASM_LIVE_OUT");
        if (string.IsNullOrEmpty(file)) return;
        var entry = Assembly.GetEntryAssembly()?.EntryPoint;
        if (entry == null) { Console.Error.WriteLine("il-live-oracle: no entry point"); return; }
        int code = 0;
        try
        {
            var ps = entry.GetParameters();
            object? r = entry.Invoke(null, ps.Length == 0 ? null : [Environment.GetCommandLineArgs().Skip(1).ToArray()]);
            if (r is int i) code = i;
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            Console.Error.WriteLine("Unhandled exception. " + e.InnerException);
            code = e.InnerException.HResult;
        }
        Console.Out.Flush();
        try { HeapWalk.Write(file); }
        catch (Exception e) { Console.Error.WriteLine("il-live-oracle: " + e); }
        Environment.Exit(code);
    }
}

internal static unsafe class HeapWalk
{
    const ulong KwGC = 0x1, KwType = 0x80000, KwHeapDump = 0x100000, KwHeapCollect = 0x800000;
    const uint BulkTypeEvent = 15, BulkNodeEvent = 18;
    static readonly bool s_debug = Environment.GetEnvironmentVariable("IL2WASM_LIVE_DEBUG") == "1";

    public static void Write(string file)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var corelib = typeof(object).Assembly;
        var ep = corelib.GetType("System.Diagnostics.Tracing.EventPipeInternal", true)!;
        var cfgT = corelib.GetType("System.Diagnostics.Tracing.EventPipeProviderConfiguration", true)!;
        var fmtT = corelib.GetType("System.Diagnostics.Tracing.EventPipeSerializationFormat", true)!;
        var dataT = corelib.GetType("System.Diagnostics.Tracing.EventPipeEventInstanceData", true)!;
        const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        if (s_debug) foreach (var m in ep.GetMethods(all)) Console.Error.WriteLine("il-live-oracle: " + m);
        var enable = ep.GetMethods(all).First(m => m.Name == "Enable" && m.GetParameters() is { Length: 4 } p && p[3].ParameterType.IsArray);
        var next = ep.GetMethods(all).First(m => m.Name == "GetNextEvent");
        var disable = ep.GetMethods(all).First(m => m.Name == "Disable");
        var cfg = Array.CreateInstance(cfgT, 1);
        cfg.SetValue(Activator.CreateInstance(cfgT, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            ["Microsoft-Windows-DotNETRuntime", KwGC | KwType | KwHeapDump | KwHeapCollect, 5u, null], null), 0);
        var fmt = Enum.ToObject(fmtT, 1); // NetTrace
        ulong session = (ulong)enable.Invoke(null, [null, fmt, 1024u, cfg])!;
        if (s_debug) Console.Error.WriteLine($"il-live-oracle: session {session}");
        int size = Marshal.SizeOf(dataT);
        int oId = (int)Marshal.OffsetOf(dataT, "EventID"), oPayload = (int)Marshal.OffsetOf(dataT, "Payload"), oLength = (int)Marshal.OffsetOf(dataT, "PayloadLength");
        byte* buf = stackalloc byte[size];
        var countByType = new Dictionary<ulong, long>();
        var names = new Dictionary<ulong, string>();
        long nodes = 0, events = 0, last = Environment.TickCount64, start = last;
        var args = new object?[] { session, Pointer.Box(buf, next.GetParameters()[1].ParameterType) };
        while (Environment.TickCount64 - start < 120_000)
        {
            if (!(bool)next.Invoke(null, args)!)
            {
                if (nodes > 0 && Environment.TickCount64 - last > 2000) break;
                Thread.Sleep(20);
                continue;
            }
            events++;
            last = Environment.TickCount64;
            uint id = *(uint*)(buf + oId);
            byte* p = *(byte**)(buf + oPayload);
            int len = (int)*(uint*)(buf + oLength);
            if (p == null) continue;
            var span = new ReadOnlySpan<byte>(p, len);
            if (id == BulkNodeEvent) nodes += Nodes(span, countByType);
            else if (id == BulkTypeEvent) Types(span, names);
        }
        disable.Invoke(null, [session]);
        if (s_debug) Console.Error.WriteLine($"il-live-oracle: {events} events, {nodes} objects, {names.Count} types named");
        var types = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (tid, n) in countByType)
        {
            var name = names.TryGetValue(tid, out var s) ? s : "type#" + tid.ToString("x");
            types[name] = types.GetValueOrDefault(name) + n;
        }
        var sb = new StringBuilder("{\"version\":1,\"nodes\":").Append(nodes).Append(",\"types\":{");
        bool first = true;
        foreach (var (k, v) in types) { if (!first) sb.Append(','); first = false; sb.Append('"').Append(k.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\":").Append(v); }
        sb.Append("}}");
        File.WriteAllText(file, sb.ToString());
    }

    /// <summary>GCBulkNode: Index u32, Count u32, ClrInstanceID u16, then Count of {Address (a pointer: 4 bytes on
    /// x86), Size u64, TypeID u64, EdgeCount u64}.</summary>
    static long Nodes(ReadOnlySpan<byte> s, Dictionary<ulong, long> counts)
    {
        if (s.Length < 10) return 0;
        int count = BitConverter.ToInt32(s[4..]);
        if (count <= 0) return 0;
        int elem = (s.Length - 10) / count, typeAt = elem - 16;
        for (int i = 0; i < count; i++)
        {
            ulong t = BitConverter.ToUInt64(s.Slice(10 + i * elem + typeAt, 8));
            counts[t] = counts.GetValueOrDefault(t) + 1;
        }
        return count;
    }

    /// <summary>GCBulkType: Count u32, ClrInstanceID u16, then Count of {TypeID u64, ModuleID u64, TypeNameID u32,
    /// Flags u32, CorElementType u8, Name (UTF-16, NUL), TypeParameterCount u32, TypeParameters u64[]}.</summary>
    static void Types(ReadOnlySpan<byte> s, Dictionary<ulong, string> names)
    {
        if (s.Length < 6) return;
        int count = BitConverter.ToInt32(s), at = 6;
        for (int i = 0; i < count && at + 25 <= s.Length; i++)
        {
            ulong id = BitConverter.ToUInt64(s[at..]); at += 8 + 8 + 4 + 4 + 1;
            int end = at;
            while (end + 1 < s.Length && (s[end] != 0 || s[end + 1] != 0)) end += 2;
            names[id] = Encoding.Unicode.GetString(s[at..end]);
            at = end + 2;
            if (at + 4 > s.Length) break;
            int tp = BitConverter.ToInt32(s[at..]); at += 4 + 8 * tp;
        }
    }
}
