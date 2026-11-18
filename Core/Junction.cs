using System;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace WorkBuddyDataMover.Core
{
    /// <summary>
    /// 原生目录联接（Junction）读写。
    /// 直接调用 DeviceIoControl + FSCTL_GET/SET_REPARSE_POINT，
    /// 不依赖 cmd.exe / mklink，因此不受命令行转义、本地化输出、长路径等影响。
    /// </summary>
    public static class Junction
    {
        private const uint ShareAll =
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE;

        /// <summary>
        /// 创建一个目录联接。junctionPath 必须不存在（或不存在实体内容）。
        /// </summary>
        public static void Create(string junctionPath, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(junctionPath)) throw new ArgumentException("联接路径不能为空。", nameof(junctionPath));
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("联接目标不能为空。", nameof(targetPath));

            string jp = LongPath.Fix(junctionPath);

            // 目标规整：去掉 \\?\ 前缀，去掉尾部分隔符
            string tp = LongPath.Unfix(targetPath).TrimEnd('\\', '/');
            if (tp.Length < 2) throw new ArgumentException("联接目标路径无效：" + targetPath, nameof(targetPath));

            if (LongPath.Exists(jp))
                throw new IOException("联接位置已存在，拒绝覆盖：" + LongPath.Unfix(jp));

            string substitute = tp.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\??\UNC\" + tp.Substring(2)
                : @"\??\" + tp;
            string print = tp;

            byte[] subBytes = Encoding.Unicode.GetBytes(substitute);
            byte[] prnBytes = Encoding.Unicode.GetBytes(print);

            int pathBufferLen = subBytes.Length + 2 + prnBytes.Length + 2;
            int reparseDataLen = 8 + pathBufferLen;
            int totalLen = 8 + reparseDataLen;

            byte[] buffer = new byte[totalLen + 8];

            WriteU32(buffer, 0, NativeMethods.IO_REPARSE_TAG_MOUNT_POINT);
            WriteU16(buffer, 4, (ushort)reparseDataLen);
            WriteU16(buffer, 6, 0);
            WriteU16(buffer, 8, 0);                                       // SubstituteNameOffset
            WriteU16(buffer, 10, (ushort)subBytes.Length);                // SubstituteNameLength
            WriteU16(buffer, 12, (ushort)(subBytes.Length + 2));          // PrintNameOffset
            WriteU16(buffer, 14, (ushort)prnBytes.Length);                // PrintNameLength
            Buffer.BlockCopy(subBytes, 0, buffer, 16, subBytes.Length);
            Buffer.BlockCopy(prnBytes, 0, buffer, 18 + subBytes.Length, prnBytes.Length);

            bool created = false;
            try
            {
                if (!NativeMethods.CreateDirectory(jp, IntPtr.Zero))
                {
                    int mkErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (mkErr != 183 /* ERROR_ALREADY_EXISTS */)
                        throw new IOException("创建联接载体目录失败：" + LongPath.Unfix(jp) + "（Win32 错误 " + mkErr + "）");
                }
                created = true;

                using (var handle = NativeMethods.CreateFileW(
                    jp,
                    NativeMethods.GENERIC_WRITE,
                    ShareAll,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT | NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
                    IntPtr.Zero))
                {
                    if (handle.IsInvalid)
                        throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                            "无法以重解析点方式打开：" + LongPath.Unfix(jp));

                    if (!NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_SET_REPARSE_POINT,
                            buffer, totalLen, null, 0, out _, IntPtr.Zero))
                    {
                        throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                            "FSCTL_SET_REPARSE_POINT 失败：" + LongPath.Unfix(jp));
                    }
                }

                // 立即回读并复核，确认真的指向了预期位置
                if (!TryGetTarget(jp, out string readBack))
                    throw new IOException("联接创建后回读失败：" + LongPath.Unfix(jp));

                if (!LongPath.PathEquals(readBack, tp))
                    throw new IOException("联接校验失败：期望指向 " + tp + "，实际指向 " + readBack);
            }
            catch
            {
                if (created)
                {
                    try { NativeMethods.RemoveDirectory(jp); } catch { }
                }
                throw;
            }
        }

        /// <summary>读取目录联接目标；不是联接则返回 false。</summary>
        public static bool TryGetTarget(string path, out string target)
        {
            return TryRead(path,
                NativeMethods.FILE_READ_ATTRIBUTES,
                ShareAll,
                NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT | NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
                out target);
        }

        /// <summary>用指定句柄标志读取重解析点（自检用于穷举不同句柄组合）。</summary>
        public static bool TryRead(string path, uint desiredAccess, uint shareMode, uint flags, out string target)
        {
            target = null;
            if (string.IsNullOrWhiteSpace(path)) return false;

            string p = LongPath.Fix(path);

            using (var handle = NativeMethods.CreateFileW(
                p,
                desiredAccess,
                shareMode,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                flags,
                IntPtr.Zero))
            {
                if (handle.IsInvalid) return false;

                byte[] outBuffer = new byte[16 * 1024];
                if (!NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_GET_REPARSE_POINT,
                        null, 0, outBuffer, outBuffer.Length, out int returned, IntPtr.Zero))
                    return false;

                if (returned < 16) return false;

                uint tag = BitConverter.ToUInt32(outBuffer, 0);
                if (tag != NativeMethods.IO_REPARSE_TAG_MOUNT_POINT) return false;

                int subOffset = BitConverter.ToUInt16(outBuffer, 8);
                int subLength = BitConverter.ToUInt16(outBuffer, 10);
                if (subLength <= 0) return false;

                int start = 16 + subOffset;
                if (start < 0 || start + subLength > outBuffer.Length) return false;

                string raw = Encoding.Unicode.GetString(outBuffer, start, subLength);

                if (raw.StartsWith(@"\??\UNC\", StringComparison.Ordinal))
                    target = @"\\" + raw.Substring(8);
                else if (raw.StartsWith(@"\??\", StringComparison.Ordinal))
                    target = raw.Substring(4);
                else if (raw.StartsWith(@"\GLOBAL??\", StringComparison.Ordinal))
                    target = raw.Substring(10);
                else
                    target = raw;

                return !string.IsNullOrEmpty(target);
            }
        }

        /// <summary>是否为目录联接（重解析点为 mount point）。</summary>
        public static bool IsJunction(string path)
        {
            if (!LongPath.IsReparsePoint(path)) return false;
            return TryGetTarget(path, out _);
        }

        /// <summary>
        /// 移除目录联接。安全保护：目标必须是目录联接，否则直接拒绝；
        /// 走 RemoveDirectory，绝不递归删除。
        /// </summary>
        public static void Delete(string linkPath)
        {
            if (string.IsNullOrWhiteSpace(linkPath)) throw new ArgumentException("联接路径不能为空。", nameof(linkPath));

            string lp = LongPath.Fix(linkPath);

            if (!LongPath.Exists(lp))
                throw new DirectoryNotFoundException("联接位置不存在：" + LongPath.Unfix(lp));

            if (!IsJunction(linkPath))
                throw new InvalidOperationException($"安全保护：{LongPath.Unfix(lp)} 不是目录联接，拒绝删除。");

            if (!NativeMethods.RemoveDirectory(lp))
                throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                    "移除联接失败：" + LongPath.Unfix(lp));
        }

        private static void WriteU32(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF);
            b[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static void WriteU16(byte[] b, int off, ushort v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
        }
    }
}
