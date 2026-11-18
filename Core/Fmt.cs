using System;
using System.Globalization;

namespace WorkBuddyDataMover.Core
{
    public static class Fmt
    {
        public static string Bytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024.0 && i < units.Length - 1) { v /= 1024.0; i++; }
            if (i == 0) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            return v.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[i];
        }

        public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

        public static string Duration(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒";
            if (t.TotalMinutes < 60) return ((int)t.TotalMinutes) + " 分 " + t.Seconds + " 秒";
            return ((int)t.TotalHours) + " 时 " + t.Minutes + " 分";
        }
    }
}
