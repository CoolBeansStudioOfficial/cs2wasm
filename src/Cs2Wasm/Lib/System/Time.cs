#pragma warning disable CS0436, CS0660, CS0661
// TimeSpan and DateTime, with .NET's tick arithmetic (100 ns ticks from 0001-01-01) and the invariant culture's
// formats. The clock comes from the engine (cs2wasm.now_ms and utc_offset_minutes).
using System.Text;
using Cs2Wasm.Runtime;

namespace System
{
    public readonly struct TimeSpan : IComparable, IComparable<TimeSpan>, IEquatable<TimeSpan>, IFormattable
    {
        public const long TicksPerMillisecond = 10000;
        public const long TicksPerSecond = TicksPerMillisecond * 1000;
        public const long TicksPerMinute = TicksPerSecond * 60;
        public const long TicksPerHour = TicksPerMinute * 60;
        public const long TicksPerDay = TicksPerHour * 24;

        readonly long _ticks;
        public TimeSpan(long ticks) { _ticks = ticks; }
        public TimeSpan(int hours, int minutes, int seconds) : this(0, hours, minutes, seconds, 0) { }
        public TimeSpan(int days, int hours, int minutes, int seconds) : this(days, hours, minutes, seconds, 0) { }
        public TimeSpan(int days, int hours, int minutes, int seconds, int milliseconds)
        {
            long ms = ((long)days * 3600 * 24 + (long)hours * 3600 + (long)minutes * 60 + seconds) * 1000 + milliseconds;
            _ticks = ms * TicksPerMillisecond;
        }

        public static readonly TimeSpan Zero = new TimeSpan(0);
        public static readonly TimeSpan MaxValue = new TimeSpan(long.MaxValue);
        public static readonly TimeSpan MinValue = new TimeSpan(long.MinValue);

        public long Ticks => _ticks;
        public int Days => (int)(_ticks / TicksPerDay);
        public int Hours => (int)(_ticks / TicksPerHour % 24);
        public int Minutes => (int)(_ticks / TicksPerMinute % 60);
        public int Seconds => (int)(_ticks / TicksPerSecond % 60);
        public int Milliseconds => (int)(_ticks / TicksPerMillisecond % 1000);
        public double TotalDays => (double)_ticks / TicksPerDay;
        public double TotalHours => (double)_ticks / TicksPerHour;
        public double TotalMinutes => (double)_ticks / TicksPerMinute;
        public double TotalSeconds => (double)_ticks / TicksPerSecond;
        public double TotalMilliseconds => (double)_ticks / TicksPerMillisecond;

        static TimeSpan Interval(double value, double scale)
        {
            if (double.IsNaN(value)) throw new ArgumentException("TimeSpan does not accept floating point Not-a-Number values.");
            double ticks = value * scale;
            if (ticks > long.MaxValue || ticks < long.MinValue) throw new OverflowException("TimeSpan overflowed because the duration is too long.");
            return new TimeSpan((long)ticks);
        }
        public static TimeSpan FromDays(double value) => Interval(value, TicksPerDay);
        public static TimeSpan FromHours(double value) => Interval(value, TicksPerHour);
        public static TimeSpan FromMinutes(double value) => Interval(value, TicksPerMinute);
        public static TimeSpan FromSeconds(double value) => Interval(value, TicksPerSecond);
        public static TimeSpan FromMilliseconds(double value) => Interval(value, TicksPerMillisecond);
        public static TimeSpan FromTicks(long value) => new TimeSpan(value);
        // integer overloads (.NET 9): exact, overflow checked
        static TimeSpan Exact(long value, long scale)
        {
            long t;
            try { t = checked(value * scale); } catch (OverflowException) { throw new ArgumentOutOfRangeException(null, "The TimeSpan could not be parsed because at least one of the numeric components is out of range or contains too many digits."); }
            return new TimeSpan(t);
        }
        public static TimeSpan FromDays(int days) => Exact(days, TicksPerDay);
        public static TimeSpan FromHours(int hours) => Exact(hours, TicksPerHour);
        public static TimeSpan FromMinutes(long minutes) => Exact(minutes, TicksPerMinute);
        public static TimeSpan FromSeconds(long seconds) => Exact(seconds, TicksPerSecond);
        public static TimeSpan FromMilliseconds(long milliseconds, long microseconds = 0) => new TimeSpan(Exact(milliseconds, TicksPerMillisecond).Ticks + microseconds * 10);
        public static TimeSpan FromMicroseconds(long microseconds) => Exact(microseconds, 10);

        public TimeSpan Add(TimeSpan ts) => new TimeSpan(checked(_ticks + ts._ticks));
        public TimeSpan Subtract(TimeSpan ts) => new TimeSpan(checked(_ticks - ts._ticks));
        public TimeSpan Negate() => new TimeSpan(-_ticks);
        public TimeSpan Duration() => new TimeSpan(_ticks >= 0 ? _ticks : -_ticks);
        public TimeSpan Multiply(double factor) => Interval(_ticks, factor);
        public TimeSpan Divide(double divisor) => Interval(_ticks, 1.0 / divisor);

        public static TimeSpan operator +(TimeSpan t1, TimeSpan t2) => t1.Add(t2);
        public static TimeSpan operator -(TimeSpan t1, TimeSpan t2) => t1.Subtract(t2);
        public static TimeSpan operator -(TimeSpan t) => t.Negate();
        public static TimeSpan operator +(TimeSpan t) => t;
        public static TimeSpan operator *(TimeSpan t, double factor) => t.Multiply(factor);
        public static TimeSpan operator *(double factor, TimeSpan t) => t.Multiply(factor);
        public static TimeSpan operator /(TimeSpan t, double divisor) => t.Divide(divisor);
        public static double operator /(TimeSpan t1, TimeSpan t2) => t1._ticks / (double)t2._ticks;
        public static bool operator ==(TimeSpan t1, TimeSpan t2) => t1._ticks == t2._ticks;
        public static bool operator !=(TimeSpan t1, TimeSpan t2) => t1._ticks != t2._ticks;
        public static bool operator <(TimeSpan t1, TimeSpan t2) => t1._ticks < t2._ticks;
        public static bool operator <=(TimeSpan t1, TimeSpan t2) => t1._ticks <= t2._ticks;
        public static bool operator >(TimeSpan t1, TimeSpan t2) => t1._ticks > t2._ticks;
        public static bool operator >=(TimeSpan t1, TimeSpan t2) => t1._ticks >= t2._ticks;

        public static int Compare(TimeSpan t1, TimeSpan t2) => t1._ticks.CompareTo(t2._ticks);
        public int CompareTo(TimeSpan value) => _ticks.CompareTo(value._ticks);
        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is TimeSpan t) return CompareTo(t);
            throw new ArgumentException("Object must be of type TimeSpan.");
        }
        public bool Equals(TimeSpan obj) => _ticks == obj._ticks;
        public override bool Equals(object value) => value is TimeSpan t && _ticks == t._ticks;
        public override int GetHashCode() => _ticks.GetHashCode();

        /// <summary>The constant ("c") format: [-][d.]hh:mm:ss[.fffffff].</summary>
        public override string ToString() => ToString(null, null);
        public string ToString(string format) => ToString(format, null);
        public string ToString(string format, IFormatProvider formatProvider)
        {
            if (format != null && format.Length > 0 && format != "c" && format != "t" && format != "T" && format != "g" && format != "G")
                return Custom(format);
            long t = _ticks;
            var sb = new StringBuilder();
            ulong u = t < 0 ? (ulong)(-(t + 1)) + 1 : (ulong)t;
            if (t < 0) sb.Append('-');
            ulong days = u / (ulong)TicksPerDay, rem = u % (ulong)TicksPerDay;
            int frac = (int)(rem % (ulong)TicksPerSecond);
            long secs = (long)(rem / (ulong)TicksPerSecond);
            bool general = format == "g" || format == "G";
            if (days > 0 || format == "G") { sb.Append(days.ToString()); sb.Append(general ? ':' : '.'); }
            int h = (int)(secs / 3600), m = (int)(secs / 60 % 60), s = (int)(secs % 60);
            if (format == "g") sb.Append(h.ToString()); else Two(sb, h);
            sb.Append(':'); Two(sb, m); sb.Append(':'); Two(sb, s);
            if (format == "G") { sb.Append('.'); sb.Append(frac.ToString().PadLeft(7, '0')); }
            else if (frac != 0)
            {
                sb.Append('.');
                var f = frac.ToString().PadLeft(7, '0');
                if (format == "g") f = f.TrimEnd('0');
                sb.Append(f);
            }
            return sb.ToString();
        }

        string Custom(string format)
        {
            long t = _ticks < 0 ? -_ticks : _ticks;
            var sb = new StringBuilder();
            for (int i = 0; i < format.Length; i++)
            {
                char c = format[i];
                int n = 1;
                while (i + n < format.Length && format[i + n] == c) n++;
                switch (c)
                {
                    case 'd': sb.Append((t / TicksPerDay).ToString().PadLeft(n, '0')); break;
                    case 'h': sb.Append((t / TicksPerHour % 24).ToString().PadLeft(n, '0')); break;
                    case 'm': sb.Append((t / TicksPerMinute % 60).ToString().PadLeft(n, '0')); break;
                    case 's': sb.Append((t / TicksPerSecond % 60).ToString().PadLeft(n, '0')); break;
                    case 'f': sb.Append((t % TicksPerSecond).ToString().PadLeft(7, '0').Substring(0, n)); break;
                    case '\\': if (i + 1 < format.Length) { sb.Append(format[i + 1]); i++; } n = 1; break;
                    case '\'':
                    case '"':
                        int end = format.IndexOf(c, i + 1);
                        if (end < 0) throw new FormatException("Input string was not in a correct format.");
                        sb.Append(format, i + 1, end - i - 1);
                        i = end; n = 1; break;
                    default: throw new FormatException("Input string was not in a correct format.");
                }
                if (c != '\\' && c != '\'' && c != '"') i += n - 1;
            }
            return sb.ToString();
        }

        static void Two(StringBuilder sb, int v) { if (v < 10) sb.Append('0'); sb.Append(v.ToString()); }
    }

    public enum DateTimeKind { Unspecified, Utc, Local }
    public enum DayOfWeek { Sunday, Monday, Tuesday, Wednesday, Thursday, Friday, Saturday }

    public readonly struct DateTime : IComparable, IComparable<DateTime>, IEquatable<DateTime>, IFormattable
    {
        const long TicksPerDay = TimeSpan.TicksPerDay;
        const int DaysPer400Years = 146097, DaysPer100Years = 36524, DaysPer4Years = 1461;
        const long MaxTicks = 3652059L * TicksPerDay - 1;
        const long UnixEpochTicks = 719162L * TicksPerDay;
        static readonly int[] s_daysToMonth365 = { 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334, 365 };
        static readonly int[] s_daysToMonth366 = { 0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335, 366 };
        static readonly string[] s_dayNames = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        static readonly string[] s_monthNames = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

        readonly long _ticks;
        readonly DateTimeKind _kind;

        public static readonly DateTime MinValue = new DateTime(0);
        public static readonly DateTime MaxValue = new DateTime(MaxTicks);
        public static readonly DateTime UnixEpoch = new DateTime(UnixEpochTicks, DateTimeKind.Utc);

        public DateTime(long ticks) : this(ticks, DateTimeKind.Unspecified) { }
        public DateTime(long ticks, DateTimeKind kind)
        {
            if ((ulong)ticks > (ulong)MaxTicks) throw new ArgumentOutOfRangeException("ticks", "Ticks must be between DateTime.MinValue.Ticks and DateTime.MaxValue.Ticks.");
            _ticks = ticks; _kind = kind;
        }
        public DateTime(int year, int month, int day) : this(DateToTicks(year, month, day)) { }
        public DateTime(int year, int month, int day, int hour, int minute, int second) : this(DateToTicks(year, month, day) + TimeToTicks(hour, minute, second)) { }
        public DateTime(int year, int month, int day, int hour, int minute, int second, DateTimeKind kind) : this(DateToTicks(year, month, day) + TimeToTicks(hour, minute, second), kind) { }
        public DateTime(int year, int month, int day, int hour, int minute, int second, int millisecond) : this(DateToTicks(year, month, day) + TimeToTicks(hour, minute, second) + MsTicks(millisecond)) { }
        public DateTime(int year, int month, int day, int hour, int minute, int second, int millisecond, DateTimeKind kind) : this(DateToTicks(year, month, day) + TimeToTicks(hour, minute, second) + MsTicks(millisecond), kind) { }

        static long MsTicks(int ms)
        {
            if ((uint)ms >= 1000) throw new ArgumentOutOfRangeException("millisecond", "Valid values are between 0 and 999, inclusive.");
            return ms * TimeSpan.TicksPerMillisecond;
        }

        static long DateToTicks(int year, int month, int day)
        {
            if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1)
                throw new ArgumentOutOfRangeException(null, "Year, Month, and Day parameters describe an un-representable DateTime.");
            var days = IsLeapYear(year) ? s_daysToMonth366 : s_daysToMonth365;
            if (day > days[month] - days[month - 1]) throw new ArgumentOutOfRangeException(null, "Year, Month, and Day parameters describe an un-representable DateTime.");
            int y = year - 1;
            int n = y * 365 + y / 4 - y / 100 + y / 400 + days[month - 1] + day - 1;
            return n * TicksPerDay;
        }

        static long TimeToTicks(int hour, int minute, int second)
        {
            if ((uint)hour >= 24 || (uint)minute >= 60 || (uint)second >= 60)
                throw new ArgumentOutOfRangeException(null, "Hour, Minute, and Second parameters describe an un-representable DateTime.");
            return ((long)hour * 3600 + minute * 60 + second) * TimeSpan.TicksPerSecond;
        }

        public static bool IsLeapYear(int year)
        {
            if (year < 1 || year > 9999) throw new ArgumentOutOfRangeException("year", "Year must be between 1 and 9999.");
            return (year & 3) == 0 && ((year & 15) == 0 || year % 25 != 0);
        }
        public static int DaysInMonth(int year, int month)
        {
            if (month < 1 || month > 12) throw new ArgumentOutOfRangeException("month", "Month must be between one and twelve.");
            var days = IsLeapYear(year) ? s_daysToMonth366 : s_daysToMonth365;
            return days[month] - days[month - 1];
        }

        // year, month, day from the day number (as .NET's GetDate)
        void GetDate(out int year, out int month, out int day)
        {
            int n = (int)(_ticks / TicksPerDay);
            int y400 = n / DaysPer400Years; n -= y400 * DaysPer400Years;
            int y100 = n / DaysPer100Years; if (y100 == 4) y100 = 3; n -= y100 * DaysPer100Years;
            int y4 = n / DaysPer4Years; n -= y4 * DaysPer4Years;
            int y1 = n / 365; if (y1 == 4) y1 = 3; n -= y1 * 365;
            year = y400 * 400 + y100 * 100 + y4 * 4 + y1 + 1;
            var days = y1 == 3 && (y4 != 24 || y100 == 3) ? s_daysToMonth366 : s_daysToMonth365;
            int m = (n >> 5) + 1;
            while (n >= days[m]) m++;
            month = m;
            day = n - days[m - 1] + 1;
        }

        public long Ticks => _ticks;
        public DateTimeKind Kind => _kind;
        public int Year { get { GetDate(out var y, out _, out _); return y; } }
        public int Month { get { GetDate(out _, out var m, out _); return m; } }
        public int Day { get { GetDate(out _, out _, out var d); return d; } }
        public int Hour => (int)(_ticks / TimeSpan.TicksPerHour % 24);
        public int Minute => (int)(_ticks / TimeSpan.TicksPerMinute % 60);
        public int Second => (int)(_ticks / TimeSpan.TicksPerSecond % 60);
        public int Millisecond => (int)(_ticks / TimeSpan.TicksPerMillisecond % 1000);
        public int Microsecond => (int)(_ticks / 10 % 1000);
        public int Nanosecond => (int)(_ticks % 10) * 100;
        public DayOfWeek DayOfWeek => (DayOfWeek)((_ticks / TicksPerDay + 1) % 7);
        public int DayOfYear
        {
            get
            {
                GetDate(out var y, out var m, out var d);
                return (IsLeapYear(y) ? s_daysToMonth366 : s_daysToMonth365)[m - 1] + d;
            }
        }
        public DateTime Date => new DateTime(_ticks - _ticks % TicksPerDay, _kind);
        public TimeSpan TimeOfDay => new TimeSpan(_ticks % TicksPerDay);

        public static DateTime UtcNow => new DateTime(UnixEpochTicks + (long)(Engine.NowMs() * TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
        public static DateTime Now
        {
            get
            {
                double ms = Engine.NowMs();
                long offset = Engine.UtcOffsetMinutes(ms) * TimeSpan.TicksPerMinute;
                return new DateTime(UnixEpochTicks + (long)(ms * TimeSpan.TicksPerMillisecond) + offset, DateTimeKind.Local);
            }
        }
        public static DateTime Today => Now.Date;

        public DateTime ToUniversalTime()
        {
            if (_kind == DateTimeKind.Utc) return this;
            double ms = (_ticks - UnixEpochTicks) / (double)TimeSpan.TicksPerMillisecond;
            return new DateTime(_ticks - Engine.UtcOffsetMinutes(ms) * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        }
        public DateTime ToLocalTime()
        {
            if (_kind == DateTimeKind.Local) return this;
            double ms = (_ticks - UnixEpochTicks) / (double)TimeSpan.TicksPerMillisecond;
            return new DateTime(_ticks + Engine.UtcOffsetMinutes(ms) * TimeSpan.TicksPerMinute, DateTimeKind.Local);
        }
        public static DateTime SpecifyKind(DateTime value, DateTimeKind kind) => new DateTime(value._ticks, kind);

        public DateTime Add(TimeSpan value) => AddTicks(value.Ticks);
        public DateTime AddTicks(long value)
        {
            long t = _ticks + value;
            if ((ulong)t > (ulong)MaxTicks) throw new ArgumentOutOfRangeException("value", "The added or subtracted value results in an un-representable DateTime.");
            return new DateTime(t, _kind);
        }
        DateTime AddUnits(double value, long scale)
        {
            double ticks = value * scale;
            if (ticks > MaxTicks || ticks < -MaxTicks) throw new ArgumentOutOfRangeException("value", "Value to add was out of range.");
            return AddTicks((long)Math.Round(ticks));
        }
        public DateTime AddDays(double value) => AddUnits(value, TicksPerDay);
        public DateTime AddHours(double value) => AddUnits(value, TimeSpan.TicksPerHour);
        public DateTime AddMinutes(double value) => AddUnits(value, TimeSpan.TicksPerMinute);
        public DateTime AddSeconds(double value) => AddUnits(value, TimeSpan.TicksPerSecond);
        public DateTime AddMilliseconds(double value) => AddUnits(value, TimeSpan.TicksPerMillisecond);
        public DateTime AddMonths(int months)
        {
            if (months < -120000 || months > 120000) throw new ArgumentOutOfRangeException("months", "Months value must be between +/- 120000.");
            GetDate(out int y, out int m, out int d);
            int i = m - 1 + months;
            if (i >= 0) { m = i % 12 + 1; y += i / 12; }
            else { m = 12 + (i + 1) % 12; y += (i - 11) / 12; }
            if (y < 1 || y > 9999) throw new ArgumentOutOfRangeException("months", "The added or subtracted value results in an un-representable DateTime.");
            int dim = DaysInMonth(y, m);
            if (d > dim) d = dim;
            return new DateTime(DateToTicks(y, m, d) + _ticks % TicksPerDay, _kind);
        }
        public DateTime AddYears(int value)
        {
            if (value < -10000 || value > 10000) throw new ArgumentOutOfRangeException("years", "Years value must be between +/-10000.");
            return AddMonths(value * 12);
        }
        public TimeSpan Subtract(DateTime value) => new TimeSpan(_ticks - value._ticks);
        public DateTime Subtract(TimeSpan value) => AddTicks(-value.Ticks);

        public static DateTime operator +(DateTime d, TimeSpan t) => d.AddTicks(t.Ticks);
        public static DateTime operator -(DateTime d, TimeSpan t) => d.AddTicks(-t.Ticks);
        public static TimeSpan operator -(DateTime d1, DateTime d2) => new TimeSpan(d1._ticks - d2._ticks);
        public static bool operator ==(DateTime d1, DateTime d2) => d1._ticks == d2._ticks;
        public static bool operator !=(DateTime d1, DateTime d2) => d1._ticks != d2._ticks;
        public static bool operator <(DateTime t1, DateTime t2) => t1._ticks < t2._ticks;
        public static bool operator <=(DateTime t1, DateTime t2) => t1._ticks <= t2._ticks;
        public static bool operator >(DateTime t1, DateTime t2) => t1._ticks > t2._ticks;
        public static bool operator >=(DateTime t1, DateTime t2) => t1._ticks >= t2._ticks;

        public static int Compare(DateTime t1, DateTime t2) => t1._ticks.CompareTo(t2._ticks);
        public int CompareTo(DateTime value) => _ticks.CompareTo(value._ticks);
        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is DateTime d) return CompareTo(d);
            throw new ArgumentException("Object must be of type DateTime.");
        }
        public bool Equals(DateTime value) => _ticks == value._ticks;
        public override bool Equals(object value) => value is DateTime d && _ticks == d._ticks;
        public static bool Equals(DateTime t1, DateTime t2) => t1._ticks == t2._ticks;
        public override int GetHashCode() => _ticks.GetHashCode();

        public long ToBinary() => _ticks | ((long)_kind << 62);
        public static DateTime FromBinary(long dateData) => new DateTime(dateData & 0x3FFFFFFFFFFFFFFF, (DateTimeKind)((ulong)dateData >> 62));
        public static DateTime FromFileTimeUtc(long fileTime) => new DateTime(fileTime + 504911232000000000L, DateTimeKind.Utc);
        public long ToFileTimeUtc() => ToUniversalTime()._ticks - 504911232000000000L;

        // ---------------- formatting (invariant culture) ----------------
        public override string ToString() => Format("G");
        public string ToString(string format) => Format(format);
        public string ToString(IFormatProvider provider) => Format("G");
        public string ToString(string format, IFormatProvider provider) => Format(format);
        public string ToShortDateString() => Format("d");
        public string ToLongDateString() => Format("D");
        public string ToShortTimeString() => Format("t");
        public string ToLongTimeString() => Format("T");

        string Format(string format)
        {
            if (format == null || format.Length == 0) format = "G";
            if (format.Length == 1)
            {
                switch (format[0])
                {
                    case 'd': format = "MM/dd/yyyy"; break;
                    case 'D': format = "dddd, dd MMMM yyyy"; break;
                    case 'f': format = "dddd, dd MMMM yyyy HH:mm"; break;
                    case 'F': format = "dddd, dd MMMM yyyy HH:mm:ss"; break;
                    case 'g': format = "MM/dd/yyyy HH:mm"; break;
                    case 'G': format = "MM/dd/yyyy HH:mm:ss"; break;
                    case 'm': case 'M': format = "MMMM dd"; break;
                    case 'o': case 'O': return RoundTrip();
                    case 'r': case 'R': return ToUniversalTimeForR().Custom("ddd, dd MMM yyyy HH':'mm':'ss 'GMT'");
                    case 's': format = "yyyy'-'MM'-'dd'T'HH':'mm':'ss"; break;
                    case 't': format = "HH:mm"; break;
                    case 'T': format = "HH:mm:ss"; break;
                    case 'u': return ToUniversalTimeForR().Custom("yyyy'-'MM'-'dd HH':'mm':'ss'Z'");
                    case 'U': return ToUniversalTime().Custom("dddd, dd MMMM yyyy HH:mm:ss");
                    case 'y': case 'Y': format = "yyyy MMMM"; break;
                    default: throw new FormatException("Input string was not in a correct format.");
                }
            }
            return Custom(format);
        }

        DateTime ToUniversalTimeForR() => _kind == DateTimeKind.Local ? ToUniversalTime() : this;

        string RoundTrip()
        {
            var s = Custom("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff");
            if (_kind == DateTimeKind.Utc) return s + "Z";
            if (_kind == DateTimeKind.Local)
            {
                double ms = (_ticks - UnixEpochTicks) / (double)TimeSpan.TicksPerMillisecond;
                int off = Engine.UtcOffsetMinutes(ms - Engine.UtcOffsetMinutes(ms) * 60000.0);
                var sb = new StringBuilder(s);
                sb.Append(off < 0 ? '-' : '+');
                if (off < 0) off = -off;
                Pad(sb, off / 60, 2); sb.Append(':'); Pad(sb, off % 60, 2);
                return sb.ToString();
            }
            return s;
        }

        static void Pad(StringBuilder sb, long v, int n) => sb.Append(v.ToString().PadLeft(n, '0'));

        string Custom(string format)
        {
            GetDate(out int year, out int month, out int day);
            var sb = new StringBuilder();
            for (int i = 0; i < format.Length; i++)
            {
                char c = format[i];
                int n = 1;
                while (i + n < format.Length && format[i + n] == c) n++;
                switch (c)
                {
                    case 'y':
                        if (n <= 2) Pad(sb, year % 100, n); else Pad(sb, year, n);
                        break;
                    case 'M':
                        if (n <= 2) Pad(sb, month, n); else sb.Append(n == 3 ? s_monthNames[month - 1].Substring(0, 3) : s_monthNames[month - 1]);
                        break;
                    case 'd':
                        if (n <= 2) Pad(sb, day, n);
                        else { var name = s_dayNames[(int)DayOfWeek]; sb.Append(n == 3 ? name.Substring(0, 3) : name); }
                        break;
                    case 'H': Pad(sb, Hour, n > 2 ? 2 : n); break;
                    case 'h': { int h = Hour % 12; if (h == 0) h = 12; Pad(sb, h, n > 2 ? 2 : n); break; }
                    case 'm': Pad(sb, Minute, n > 2 ? 2 : n); break;
                    case 's': Pad(sb, Second, n > 2 ? 2 : n); break;
                    case 'f':
                    case 'F':
                        {
                            if (n > 7) throw new FormatException("Input string was not in a correct format.");
                            var f = (_ticks % TimeSpan.TicksPerSecond).ToString().PadLeft(7, '0').Substring(0, n);
                            if (c == 'F') { f = f.TrimEnd('0'); if (f.Length == 0 && sb.Length > 0 && sb[sb.Length - 1] == '.') sb.Length--; }
                            sb.Append(f);
                            break;
                        }
                    case 't': sb.Append(n == 1 ? (Hour < 12 ? "A" : "P") : (Hour < 12 ? "AM" : "PM")); break;
                    case 'K':
                        if (_kind == DateTimeKind.Utc) sb.Append('Z');
                        else if (_kind == DateTimeKind.Local) { var rt = RoundTrip(); sb.Append(rt.Substring(rt.Length - 6)); }
                        n = 1; break;
                    case 'z':
                        {
                            int off = _kind == DateTimeKind.Utc ? 0 : Engine.UtcOffsetMinutes((_ticks - UnixEpochTicks) / (double)TimeSpan.TicksPerMillisecond);
                            sb.Append(off < 0 ? '-' : '+');
                            if (off < 0) off = -off;
                            if (n == 1) sb.Append((off / 60).ToString());
                            else if (n == 2) Pad(sb, off / 60, 2);
                            else { Pad(sb, off / 60, 2); sb.Append(':'); Pad(sb, off % 60, 2); }
                            break;
                        }
                    case ':': sb.Append(':'); n = 1; break;
                    case '/': sb.Append('/'); n = 1; break;
                    case '\\': if (i + 1 < format.Length) { sb.Append(format[i + 1]); i++; } n = 1; continue;
                    case '\'':
                    case '"':
                        {
                            int end = format.IndexOf(c, i + 1);
                            if (end < 0) throw new FormatException("Cannot find a matching quote character for the character '" + c + "'.");
                            sb.Append(format, i + 1, end - i - 1);
                            i = end;
                            continue;
                        }
                    case '%': n = 1; break;
                    default:
                        sb.Append(c, n);
                        break;
                }
                i += n - 1;
            }
            return sb.ToString();
        }
    }
}
