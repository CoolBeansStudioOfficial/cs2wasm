using System;
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var d = new DateTime(2024, 2, 29, 13, 5, 9, 42);
        Console.WriteLine(d);
        Console.WriteLine(d.Year + " " + d.Month + " " + d.Day + " " + d.Hour + " " + d.Minute + " " + d.Second + " " + d.Millisecond);
        Console.WriteLine(d.DayOfWeek + " " + d.DayOfYear + " " + d.Ticks);
        Console.WriteLine(d.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + d.ToString("dddd, MMMM d, yy h:mm tt"));
        Console.WriteLine(d.ToString("o") + " | " + d.ToString("s") + " | " + d.ToString("d") + " | " + d.ToString("D"));
        Console.WriteLine($"{d:MMM dd} {d:HH:mm} {d:'week' ddd}");
        Console.WriteLine(d.AddYears(1) + " | " + d.AddMonths(-13) + " | " + d.AddDays(1.5) + " | " + d.AddHours(-14));
        var e = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        TimeSpan diff = d - e;
        Console.WriteLine(diff + " " + diff.TotalDays + " " + diff.Days + " " + e.ToString("o") + " " + e.Kind);
        Console.WriteLine(DateTime.IsLeapYear(1900) + " " + DateTime.IsLeapYear(2000) + " " + DateTime.DaysInMonth(2023, 2));
        Console.WriteLine((d > e) + " " + d.Date + " " + d.TimeOfDay + " " + DateTime.MinValue + " " + DateTime.MaxValue.Ticks);
        Console.WriteLine(new DateTime(1, 1, 1).DayOfWeek + " " + new DateTime(9999, 12, 31).DayOfYear + " " + DateTime.UnixEpoch.AddSeconds(1e9));
        var ts = new TimeSpan(1, 2, 3, 4, 567);
        Console.WriteLine(ts + " " + ts.TotalHours + " " + (-ts) + " " + TimeSpan.FromMinutes(90) + " " + TimeSpan.FromMilliseconds(1.5));
        Console.WriteLine(ts.ToString("c") + " " + ts.ToString("g") + " " + ts.ToString("G") + " " + ts.ToString(@"hh\:mm") + " " + (ts * 2) + " " + (ts / 4));
        Console.WriteLine(TimeSpan.Zero + " " + new TimeSpan(0, 0, 59).Add(TimeSpan.FromSeconds(2)) + " " + TimeSpan.Compare(ts, TimeSpan.Zero));
        try { new DateTime(2023, 2, 29); } catch (ArgumentOutOfRangeException x) { Console.WriteLine(x.Message); }
        Console.WriteLine((DateTime.UtcNow.Year >= 2024) + " " + (DateTime.Now.Kind));
    }
}
