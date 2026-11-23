using System.Collections.Generic;

namespace System
{
    // il2wasm: in place of TimeZoneInfo.Unix.NonAndroid.cs (SPEC 7.4): no time zone database is shipped, and the
    // local zone takes its UTC offset from the engine's clock import. TimeZoneInfo.Unix.cs (the TZif and POSIX rule
    // parsing) is upstream's.
    public sealed partial class TimeZoneInfo
    {
        private static TimeZoneInfo GetLocalTimeZoneCore()
        {
            int minutes = Il2Wasm.Runtime.Engine.UtcOffsetMinutes(Il2Wasm.Runtime.Engine.NowMs());
            return CreateCustomTimeZone(LocalId, TimeSpan.FromMinutes(minutes), LocalId, LocalId);
        }

        private static TimeZoneInfoResult TryGetTimeZoneFromLocalMachineCore(string id, out TimeZoneInfo? value, out Exception? e)
        {
            value = null;
            e = null;
            return TimeZoneInfoResult.TimeZoneNotFoundException;
        }

        private static IEnumerable<string> GetTimeZoneIds() => Array.Empty<string>();
    }
}
