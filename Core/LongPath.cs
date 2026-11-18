using System;
using System.IO;

namespace WorkBuddyDataMover.Core
{
    /// <summary>
    /// 长路径处理：所有文件操作都自带 \\?\ / \\?\UNC\ 前缀，
    /// 不依赖系统 LongPathsEnabled 注册表项。
    /// </summary>
    public static class LongPath
    {
        public const string Prefix = @"\\?\";
        public const string UncPrefix = @"\\?\UNC\";

        /// <summary>把任意路径转成扩展长度路径（\\?\ 前缀）。</summary>
        public static string Fix(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            if (path.StartsWith(UncPrefix, StringComparison.Ordinal)) return path;
            if (path.StartsWith(Prefix, StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;

            string full;
            try { full = Path.GetFullPath(path); }
            catch { return path; }

            if (full.StartsWith(UncPrefix, StringComparison.Ordinal)) return full;
            if (full.StartsWith(Prefix, StringComparison.Ordinal)) return full;

            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return UncPrefix + full.Substring(2);

            return Prefix + full;
        }

        /// <summary>去掉扩展长度前缀，还原成普通可显示路径。</summary>
        public static string Unfix(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(UncPrefix, StringComparison.Ordinal))
                return @"\\" + path.Substring(UncPrefix.Length);
            if (path.StartsWith(Prefix, StringComparison.Ordinal))
                return path.Substring(Prefix.Length);
            if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
                return path.Substring(2);
            return path;
        }

        /// <summary>规范化路径用于比较：去前缀、去尾部分隔符。</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string p;
            try { p = Path.GetFullPath(Unfix(path)); }
            catch { p = Unfix(path); }
            return p.TrimEnd('\\', '/');
        }

        /// <summary>child 是否等于 parent 或位于 parent 之下。</summary>
        public static bool IsSameOrUnder(string parent, string child)
        {
            string p = Normalize(parent);
            string c = Normalize(child);
            if (p.Length == 0 || c.Length == 0) return false;
            if (string.Equals(p, c, StringComparison.OrdinalIgnoreCase)) return true;
            return c.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static bool PathEquals(string a, string b)
            => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>把相对路径拼到根路径上，并加长路径前缀。</summary>
        public static string Combine(string root, string relative)
            => Fix(Path.Combine(Unfix(root), relative));

        /// <summary>安全判断路径是否存在（含悬空联接，不跟随重解析点）。</summary>
        public static bool Exists(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                File.GetAttributes(Fix(path));
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return false; }
            catch (Exception) { return false; }
        }

        public static bool IsDirectory(string path)
        {
            try
            {
                return (File.GetAttributes(Fix(path)) & FileAttributes.Directory) != 0;
            }
            catch { return false; }
        }

        public static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(Fix(path)) & FileAttributes.ReparsePoint) != 0;
            }
            catch { return false; }
        }
    }
}
