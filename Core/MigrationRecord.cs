using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkBuddyDataMover.Core
{
    public class PartialItemRecord
    {
        public string Name { get; set; }
        public string JunctionPath { get; set; }
        public string TargetPath { get; set; }
        public string BackupPath { get; set; }
        public long Bytes { get; set; }
        public int Files { get; set; }
    }

    /// <summary>一键回滚所需的完整迁移记录。</summary>
    public class MigrationRecord
    {
        public int Version { get; set; } = 1;

        /// <summary>junction | env | partial</summary>
        public string Mode { get; set; }

        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public string BackupPath { get; set; }

        public string EnvName { get; set; }
        public string EnvOriginalValue { get; set; }
        public bool EnvOriginalExisted { get; set; }

        public bool MergeMode { get; set; }
        public string TimestampLocal { get; set; }

        /// <summary>completed | rolledback</summary>
        public string Status { get; set; } = "completed";

        public long MigratedBytes { get; set; }
        public int MigratedFiles { get; set; }

        public List<PartialItemRecord> Items { get; set; } = new List<PartialItemRecord>();

        /// <summary>这份记录实际保存到的路径（自检用临时路径时，回滚必须写回同一位置）。</summary>
        [JsonIgnore]
        public string SavedPath { get; set; }

        [JsonIgnore]
        public bool IsCompleted => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);

        public string ModeName
        {
            get
            {
                switch ((Mode ?? "").ToLowerInvariant())
                {
                    case "junction": return "目录联接";
                    case "env": return "环境变量";
                    case "partial": return "部分迁移";
                    default: return Mode ?? "未知";
                }
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public void Save(string path = null)
        {
            string target = path ?? SavedPath ?? WorkBuddyPaths.LastRecordPath;
            string dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(target, json, new UTF8Encoding(false));
            SavedPath = target;

            // 追加历史流水，便于排障
            try
            {
                string history = Path.Combine(dir ?? WorkBuddyPaths.RecordDirectory, "history.log");
                File.AppendAllText(history,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{Mode}\t{Status}\t{SourcePath}\t->\t{TargetPath}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        public static MigrationRecord Load(string path = null)
        {
            string target = path ?? WorkBuddyPaths.LastRecordPath;
            if (!File.Exists(target)) return null;

            try
            {
                string json = File.ReadAllText(target, Encoding.UTF8);
                MigrationRecord rec = JsonSerializer.Deserialize<MigrationRecord>(json, JsonOpts);
                if (rec != null) rec.SavedPath = target;
                return rec;
            }
            catch
            {
                return null;
            }
        }

        public void Delete(string path = null)
        {
            try
            {
                string target = path ?? SavedPath ?? WorkBuddyPaths.LastRecordPath;
                if (File.Exists(target)) File.Delete(target);
            }
            catch { }
        }
    }
}
