using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WorkBuddyDataMover.Core
{
    /// <summary>子目录风险分级。分级依据只有一个：这份数据读不到时，用户要付出多大代价。</summary>
    public enum RiskTier
    {
        Cache = 0,        // 缓存：随时可重建，迁移零风险
        Redownload = 1,   // 可重下：本机运行需要，可重新下载
        Stateful = 2,     // 有状态：含本地状态，盘位必须固定
        Core = 3,         // 核心：凭据、会话库、项目索引 —— 禁止迁移
        Unknown = 4       // 未识别：保守处理，默认不勾选
    }

    public static class WorkBuddyPaths
    {
        public const string EnvPrimary = "WORKBUDDY_CONFIG_DIR";
        public const string EnvSecondary = "CODEBUDDY_CONFIG_DIR";
        public const string DefaultFolderName = ".workbuddy";

        /// <summary>子目录风险分级表，覆盖 60+ 个目录名。</summary>
        private static readonly Dictionary<string, RiskTier> Table =
            new Dictionary<string, RiskTier>(StringComparer.OrdinalIgnoreCase)
            {
                // ---------- 缓存（默认勾选）----------
                { "logs", RiskTier.Cache },
                { "log", RiskTier.Cache },
                { "traces", RiskTier.Cache },
                { "cache", RiskTier.Cache },
                { "Code Cache", RiskTier.Cache },
                { "GPUCache", RiskTier.Cache },
                { "ShaderCache", RiskTier.Cache },
                { "GrShaderCache", RiskTier.Cache },
                { "GraphiteDawnCache", RiskTier.Cache },
                { "DawnCache", RiskTier.Cache },
                { "DawnGraphiteCache", RiskTier.Cache },
                { "DawnWebGPUCache", RiskTier.Cache },
                { "Crashpad", RiskTier.Cache },
                { "Crash Reports", RiskTier.Cache },
                { "changes-detail", RiskTier.Cache },
                { "file-tree-manifests", RiskTier.Cache },
                { "shell-snapshots", RiskTier.Cache },
                { "clipboard-images", RiskTier.Cache },
                { "temp", RiskTier.Cache },
                { "tmp", RiskTier.Cache },
                { "thumbnails", RiskTier.Cache },
                { "image-cache", RiskTier.Cache },
                { "media-cache", RiskTier.Cache },
                { "CacheStorage", RiskTier.Cache },
                { "Service Worker", RiskTier.Cache },
                { "blob_storage", RiskTier.Cache },
                { "Session Storage", RiskTier.Cache },
                { "Network Persistent State", RiskTier.Cache },

                // ---------- 可重下（默认勾选）----------
                { "binaries", RiskTier.Redownload },
                { "node", RiskTier.Redownload },
                { "python", RiskTier.Redownload },
                { "git", RiskTier.Redownload },
                { "bun", RiskTier.Redownload },
                { "deno", RiskTier.Redownload },
                { "plugins", RiskTier.Redownload },
                { "connectors-marketplace", RiskTier.Redownload },
                { "skills-marketplace", RiskTier.Redownload },
                { "marketplace", RiskTier.Redownload },
                { "extensions", RiskTier.Redownload },
                { "models", RiskTier.Redownload },
                { "updates", RiskTier.Redownload },
                { "installers", RiskTier.Redownload },
                { "vendor", RiskTier.Redownload },
                { "runtime", RiskTier.Redownload },
                { "toolchains", RiskTier.Redownload },

                // ---------- 有状态（默认不勾）----------
                { "app", RiskTier.Stateful },
                { "workspace", RiskTier.Stateful },
                { "workspaces", RiskTier.Stateful },
                { "file-history", RiskTier.Stateful },
                { "blobs", RiskTier.Stateful },
                { "IndexedDB", RiskTier.Stateful },
                { "Local Storage", RiskTier.Stateful },
                { "profile", RiskTier.Stateful },
                { "profiles", RiskTier.Stateful },
                { "state", RiskTier.Stateful },
                { "snapshots", RiskTier.Stateful },
                { "settings", RiskTier.Stateful },
                { "config", RiskTier.Stateful },
                { "userData", RiskTier.Stateful },

                // ---------- 核心（禁止勾选）----------
                { "projects", RiskTier.Core },
                { "security", RiskTier.Core },
                { "sessions", RiskTier.Core },
                { "credentials", RiskTier.Core },
                { "memory", RiskTier.Core },
                { "tasks", RiskTier.Core },
                { "skills", RiskTier.Core },
                { "agents", RiskTier.Core },
                { "db", RiskTier.Core },
                { "databases", RiskTier.Core },
                { "cloud", RiskTier.Core },
                { "account", RiskTier.Core },
                { "sync", RiskTier.Core },
                { "auth", RiskTier.Core },

                // ---------- 以下为实测补充（2026-11 本机 63 个一级子目录全量对齐）----------
                // 缓存类补充
                { "changes-index", RiskTier.Cache },
                { "artifact-index", RiskTier.Cache },
                { "audit-log", RiskTier.Cache },
                { "diagnostics", RiskTier.Cache },
                { "pending-telemetry", RiskTier.Cache },
                { "knowledge-upload-tmp", RiskTier.Cache },

                // 可重下类补充
                { "connectors-marketplace-sources", RiskTier.Redownload },
                { "appearance-resources", RiskTier.Redownload },
                { "native-runtimes", RiskTier.Redownload },
                { "external-packages", RiskTier.Redownload },
                { "browser-runtime", RiskTier.Redownload },
                { "local-models", RiskTier.Redownload },

                // 有状态类补充（含本地状态，盘位必须固定）
                { "storage", RiskTier.Stateful },
                { "local_storage", RiskTier.Stateful },
                { "assistant-display", RiskTier.Stateful },
                { "agent-cli-prompt-vars", RiskTier.Stateful },
                { "connectors", RiskTier.Stateful },
                { "space-history", RiskTier.Stateful },
                { "extension-registry", RiskTier.Stateful },
                { "cli-env", RiskTier.Stateful },
                { "unified-asset", RiskTier.Stateful },
                { "plugin-account-state", RiskTier.Stateful },
                { "plugin-marketplace-state-new", RiskTier.Stateful },
                { "skill-cloud-sync", RiskTier.Stateful },
                { "claw-state", RiskTier.Stateful },
                { "wbipc", RiskTier.Stateful },
                { "mods-store", RiskTier.Stateful },
                { "plans", RiskTier.Stateful },
                { "teams", RiskTier.Stateful },
                { "editor-sdk-sandbox-ws", RiskTier.Stateful },

                // 核心类补充
                { "experts", RiskTier.Core },
                { ".workbuddy-sqlite-migrations", RiskTier.Core },
            };

        private static readonly System.Text.RegularExpressions.Regex GuidPattern =
            new System.Text.RegularExpressions.Regex(
                @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public static int RiskTableCount => Table.Count;

        public static IReadOnlyDictionary<string, RiskTier> RiskTable => Table;

        /// <summary>
        /// 子目录分级。分级依据只有一个：这份数据读不到时，用户要付出多大代价。
        /// 不确定时一律归入「有状态」或「核心」，绝不猜测性地归为「缓存」。
        /// </summary>
        public static RiskTier Classify(string dirName)
        {
            if (string.IsNullOrWhiteSpace(dirName)) return RiskTier.Unknown;

            string key = dirName.Trim();

            if (Table.TryGetValue(key, out RiskTier tier)) return tier;

            // 账号 / 工作区实例目录：GUID 与 user-<guid>-personal 形式，属核心数据
            if (GuidPattern.IsMatch(key)) return RiskTier.Core;
            if (key.StartsWith("user-", StringComparison.OrdinalIgnoreCase) &&
                key.EndsWith("-personal", StringComparison.OrdinalIgnoreCase))
                return RiskTier.Core;

            // 明确的临时目录后缀
            if (key.EndsWith("-tmp", StringComparison.OrdinalIgnoreCase)) return RiskTier.Cache;

            return RiskTier.Unknown;
        }

        public static string TierName(RiskTier t)
        {
            switch (t)
            {
                case RiskTier.Cache: return "缓存";
                case RiskTier.Redownload: return "可重下";
                case RiskTier.Stateful: return "有状态";
                case RiskTier.Core: return "核心";
                default: return "未识别";
            }
        }

        /// <summary>是否允许用户勾选。</summary>
        public static bool IsSelectable(RiskTier t) => t != RiskTier.Core;

        /// <summary>默认是否勾选。</summary>
        public static bool IsDefaultChecked(RiskTier t) => t == RiskTier.Cache || t == RiskTier.Redownload;

        public static int TierSortKey(RiskTier t) => (int)t;

        // ---------------- 路径解析 ----------------

        /// <summary>
        /// 读取**用户级持久**环境变量（注册表 HKCU\Environment）。
        /// 注意：这里刻意不回退到进程环境块——进程级变量由"谁启动了本工具"决定，
        /// 例如从 WorkBuddy 派生的终端里启动本工具时，进程环境会带上 WORKBUDDY_CONFIG_DIR，
        /// 但那并不是用户持久设置。判断"迁移后产品会不会读到"必须只看用户级设置。
        /// </summary>
        public static string GetUserEnv(string name)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            catch { }

            return null;
        }

        /// <summary>读取当前进程继承到的环境变量（仅用于提示，不能当作持久设置）。</summary>
        public static string GetProcessEnv(string name)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 按产品内部优先级解析配置目录：
        /// WORKBUDDY_CONFIG_DIR || CODEBUDDY_CONFIG_DIR || %USERPROFILE%\.workbuddy
        /// 用户级持久设置优先，其次才是当前进程继承到的值。
        /// </summary>
        public static string ResolveConfigDir(out string resolvedBy)
        {
            string v = GetUserEnv(EnvPrimary);
            if (!string.IsNullOrWhiteSpace(v))
            {
                resolvedBy = EnvPrimary + "（用户级）";
                return LongPath.Unfix(v);
            }

            v = GetProcessEnv(EnvPrimary);
            if (!string.IsNullOrWhiteSpace(v))
            {
                resolvedBy = EnvPrimary + "（当前进程继承）";
                return LongPath.Unfix(v);
            }

            v = GetUserEnv(EnvSecondary);
            if (!string.IsNullOrWhiteSpace(v))
            {
                resolvedBy = EnvSecondary + "（用户级）";
                return LongPath.Unfix(v);
            }

            v = GetProcessEnv(EnvSecondary);
            if (!string.IsNullOrWhiteSpace(v))
            {
                resolvedBy = EnvSecondary + "（当前进程继承）";
                return LongPath.Unfix(v);
            }

            resolvedBy = "默认路径";
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultFolderName);
        }

        public static string ResolveConfigDir()
        {
            return ResolveConfigDir(out _);
        }

        /// <summary>
        /// 产品的默认数据目录 %USERPROFILE%\.workbuddy——不看任何环境变量。
        /// 「搬回原处」要还原到的就是这里，所以必须与环境变量解析严格分开。
        /// </summary>
        public static string DefaultConfigDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    DefaultFolderName);
            }
        }

        public static string LastRecordPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WorkBuddyDataMover");
                return Path.Combine(dir, "last-migration.json");
            }
        }

        public static string RecordDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WorkBuddyDataMover");
            }
        }

        /// <summary>是否是 .moved- / .bak 形式的备份目录（本工具自己生成的）。</summary>
        public static bool IsBackupName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return true;
            int idx = name.LastIndexOf(".moved-", StringComparison.OrdinalIgnoreCase);
            return idx > 0;
        }
    }
}
