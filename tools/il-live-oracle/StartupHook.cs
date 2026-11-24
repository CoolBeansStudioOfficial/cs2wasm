// The .NET oracle for live objects per type at exit (docs/MEMORY-IR.md 9.2), as a startup hook:
//   DOTNET_STARTUP_HOOKS=<path>/il-live-oracle.dll IL2WASM_LIVE_OUT=live.json <program>
// At exit (ProcessExit): GC.Collect, WaitForPendingFinalizers, GC.Collect, then the runtime's heap walk (the
// GCHeapCollect keyword forces a collection that reports every live object, GCBulkNode, and its type, GCBulkType: the
// events dotnet-gcdump reads), received in-process by an EventListener. Written: {"types": {"<name>": count}}.
// IL2WASM_LIVE_DEBUG=1: the events' payload shapes on stderr. Run it on the homelab only (never .NET cases on the desk).
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Text;

internal static class StartupHook
{
    public static void Initialize()
    {
        var file = Environment.GetEnvironmentVariable("IL2WASM_LIVE_OUT");
        if (string.IsNullOrEmpty(file)) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { HeapWalk.Write(file); }
            catch (Exception e) { Console.Error.WriteLine("il-live-oracle: " + e); }
        };
    }
}

internal sealed class HeapWalk : EventListener
{
    const long KwGC = 0x1, KwType = 0x80000, KwHeapDump = 0x100000, KwHeapCollect = 0x800000;
    static readonly bool s_debug = Environment.GetEnvironmentVariable("IL2WASM_LIVE_DEBUG") == "1";

    readonly ConcurrentDictionary<ulong, long> _countByType = new();
    readonly ConcurrentDictionary<ulong, string> _names = new();
    readonly ConcurrentDictionary<string, int> _seen = new();
    long _lastEvent = Environment.TickCount64;
    int _nodes, _debugShown;
    EventSource? _runtime;
    volatile bool _armed;

    public static void Write(string file)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var w = new HeapWalk();
        w.Arm();
        // (IL2WASM_LIVE_GC=1: a collection of our own as well, in case the keyword alone does not start the walk)
        if (Environment.GetEnvironmentVariable("IL2WASM_LIVE_GC") == "1") { Thread.Sleep(500); GC.Collect(); }
        // (the walk's events arrive on the listener's thread: wait for them to stop coming)
        long start = Environment.TickCount64;
        while (Environment.TickCount64 - start < 60_000)
        {
            Thread.Sleep(100);
            if (w._nodes > 0 && Environment.TickCount64 - Interlocked.Read(ref w._lastEvent) > 2000) break;
        }
        if (s_debug) Console.Error.WriteLine("il-live-oracle: events " + string.Join(", ", w._seen.Select(p => p.Key + "=" + p.Value)) + (w._runtime == null ? "; no runtime source" : ""));
        var types = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (id, n) in w._countByType)
        {
            var name = w._names.TryGetValue(id, out var s) ? s : "type#" + id.ToString("x");
            types[name] = types.GetValueOrDefault(name) + n;
        }
        var sb = new StringBuilder("{\"version\":1,\"nodes\":").Append(w._nodes).Append(",\"types\":{");
        bool first = true;
        foreach (var (k, v) in types) { if (!first) sb.Append(','); first = false; sb.Append('"').Append(Escape(k)).Append("\":").Append(v); }
        sb.Append("}}");
        File.WriteAllText(file, sb.ToString());
    }

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime") _runtime = source;
        if (_armed && source == _runtime) Enable();
    }

    void Arm() { _armed = true; if (_runtime != null) Enable(); }

    void Enable() => EnableEvents(_runtime!, EventLevel.Verbose, (EventKeywords)(KwGC | KwType | KwHeapDump | KwHeapCollect));

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        Interlocked.Exchange(ref _lastEvent, Environment.TickCount64);
        if (s_debug) _seen.AddOrUpdate(e.EventName ?? "#" + e.EventId, 1, (_, n) => n + 1);
        if (s_debug && _debugShown < 40 && e.EventName is "GCBulkNode" or "GCBulkType")
        {
            _debugShown++;
            var parts = new List<string>();
            for (int i = 0; i < (e.PayloadNames?.Count ?? 0); i++) parts.Add($"{e.PayloadNames![i]}:{e.Payload?[i]?.GetType().Name}");
            Console.Error.WriteLine($"il-live-oracle: {e.EventName} #{e.EventId} {string.Join(" ", parts)}");
        }
        if (e.EventName == "GCBulkNode") Nodes(e);
        else if (e.EventName == "GCBulkType") Types(e);
    }

    /// <summary>GCBulkNode: Index u32, Count u32, ClrInstanceID u16, then Count of {Address, Size, TypeID, EdgeCount},
    /// u64 each (decoded into the named fields, or left as the raw bytes of the array).</summary>
    void Nodes(EventWrittenEventArgs e)
    {
        var p = e.Payload!;
        int count = Convert.ToInt32(p[e.PayloadNames!.IndexOf("Count")]);
        int vi = e.PayloadNames.IndexOf("Values");
        if (vi >= 0 && p[vi] is byte[] raw)
        {
            for (int i = 0; i < count && (i + 1) * 32 <= raw.Length; i++) Add(BitConverter.ToUInt64(raw, i * 32 + 16));
        }
        else if (vi >= 0 && p[vi] is System.Collections.IEnumerable items)
        {
            foreach (var it in items)
                if (it is IDictionary<string, object> d && d.TryGetValue("TypeID", out var t)) Add(Convert.ToUInt64(t));
        }
        else if (e.PayloadNames.IndexOf("TypeID") is int ti and >= 0) Add(Convert.ToUInt64(p[ti]));
    }

    void Add(ulong type) { _countByType.AddOrUpdate(type, 1, (_, n) => n + 1); Interlocked.Increment(ref _nodes); }

    /// <summary>GCBulkType: Count u32, ClrInstanceID u16, then Count of {TypeID u64, ModuleID u64, TypeNameID u32,
    /// Flags u32, CorElementType u8, Name (UTF-16, NUL), TypeParameterCount u32, TypeParameters u64[]}.</summary>
    void Types(EventWrittenEventArgs e)
    {
        var p = e.Payload!;
        int count = Convert.ToInt32(p[e.PayloadNames!.IndexOf("Count")]);
        int vi = e.PayloadNames.IndexOf("Values");
        if (vi >= 0 && p[vi] is byte[] raw)
        {
            int at = 0;
            for (int i = 0; i < count && at + 25 <= raw.Length; i++)
            {
                ulong id = BitConverter.ToUInt64(raw, at); at += 8 + 8 + 4 + 4 + 1;
                int end = at;
                while (end + 1 < raw.Length && (raw[end] != 0 || raw[end + 1] != 0)) end += 2;
                _names[id] = Encoding.Unicode.GetString(raw, at, end - at);
                at = end + 2;
                int tp = BitConverter.ToInt32(raw, at); at += 4 + 8 * tp;
            }
        }
        else if (vi >= 0 && p[vi] is System.Collections.IEnumerable items)
        {
            foreach (var it in items)
                if (it is IDictionary<string, object> d && d.TryGetValue("TypeID", out var t) && d.TryGetValue("TypeName", out var n)) _names[Convert.ToUInt64(t)] = n?.ToString() ?? "";
        }
    }
}
