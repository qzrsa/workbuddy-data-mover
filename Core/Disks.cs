using System;
using System.Collections.Generic;
using System.IO;

namespace WorkBuddyDataMover.Core
{
    public class DriveOption
    {
        public string Root { get; set; }
        public string Label { get; set; }
        public DriveType Type { get; set; }
        public string Format { get; set; }
        public long FreeBytes { get; set; }
        public long TotalBytes { get; set; }

        public bool IsNetwork => Type == DriveType.Network;
        public bool SupportsJunction =>
            string.Equals(Format, "NTFS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Format, "ReFS", StringComparison.OrdinalIgnoreCase);

        public override string ToString()
        {
            string kind = Type == DriveType.Removable ? "可移动" : (Type == DriveType.Network ? "网络" : "");
            if (!string.IsNullOrEmpty(kind)) kind = "·" + kind;
            return $"{Root.TrimEnd('\\')}  [{Format}{kind}]  可用 {Fmt.Bytes(FreeBytes)} / 共 {Fmt.Bytes(TotalBytes)}";
        }
    }

    public static class Disks
    {
        public static List<DriveOption> GetCandidateDrives(bool includeUnsupported = false)
        {
            var list = new List<DriveOption>();
            DriveInfo[] all;
            try { all = DriveInfo.GetDrives(); }
            catch { return list; }

            foreach (DriveInfo d in all)
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)
                        continue;

                    string fmt = "";
                    try { fmt = d.DriveFormat ?? ""; } catch { }

                    var opt = new DriveOption
                    {
                        Root = d.RootDirectory.FullName,
                        Label = SafeVolumeLabel(d),
                        Type = d.DriveType,
                        Format = fmt,
                        FreeBytes = SafeFree(d),
                        TotalBytes = SafeTotal(d),
                    };

                    if (!includeUnsupported && !opt.SupportsJunction) continue;

                    list.Add(opt);
                }
                catch { }
            }

            list.Sort((a, b) => string.Compare(a.Root, b.Root, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public static DriveOption GetDriveOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string root;
            try { root = Path.GetPathRoot(LongPath.Unfix(path)); }
            catch { return null; }
            if (string.IsNullOrEmpty(root)) return null;

            try
            {
                var d = new DriveInfo(root);
                if (!d.IsReady) return null;
                string fmt = "";
                try { fmt = d.DriveFormat ?? ""; } catch { }
                return new DriveOption
                {
                    Root = d.RootDirectory.FullName,
                    Label = SafeVolumeLabel(d),
                    Type = d.DriveType,
                    Format = fmt,
                    FreeBytes = SafeFree(d),
                    TotalBytes = SafeTotal(d),
                };
            }
            catch { return null; }
        }

        private static string SafeVolumeLabel(DriveInfo d)
        {
            try { return d.VolumeLabel ?? ""; } catch { return ""; }
        }

        private static long SafeFree(DriveInfo d)
        {
            try { return d.AvailableFreeSpace; } catch { return 0; }
        }

        private static long SafeTotal(DriveInfo d)
        {
            try { return d.TotalSize; } catch { return 0; }
        }
    }
}
