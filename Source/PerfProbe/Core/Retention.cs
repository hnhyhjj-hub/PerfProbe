using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace PerfProbe
{
    /// <summary>保留策略：只删本模组按固定命名规则生成的文件，index.json 与 schema.md 白名单排除。</summary>
    public static class Retention
    {
        // 一个会话现在有三个文件：jsonl / summary.md / brief.md。
        // 三个都要匹配，否则 brief.md 会既不被计数也不被删除，旧会话永远残留。
        private static readonly Regex NameRx =
            new Regex(@"^(passive|bench)-\d{8}-\d{4}-s\d{2}\.(jsonl|summary\.md|brief\.md)$", RegexOptions.Compiled);

        public const string IndexFileName = "index.json";
        public const string SchemaFileName = "schema.md";

        public static bool IsOurFileName(string name)
        {
            return !string.IsNullOrEmpty(name) && NameRx.IsMatch(name);
        }

        /// <summary>把会话文件名（jsonl / summary.md / brief.md）解析成会话基名，用于成组删除。非本模组命名返回 null。</summary>
        public static string SessionBaseName(string name)
        {
            if (!IsOurFileName(name))
            {
                return null;
            }
            int dot = name.IndexOf('.');
            return dot < 0 ? null : name.Substring(0, dot);
        }

        /// <summary>
        /// 扫描目录，按会话基名分组（一组含 jsonl / summary.md / brief.md 三个文件），
        /// 保留最新 keep 组，其余成组删除。返回被删文件相对名列表。
        /// </summary>
        public static List<string> Enforce(string dir, int keep)
        {
            var deleted = new List<string>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || keep < 1)
            {
                return deleted;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception e)
            {
                PLog.Warn("保留策略无法枚举目录: " + e.Message);
                return deleted;
            }

            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var groupTime = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                string baseName = SessionBaseName(name);
                if (baseName == null)
                {
                    continue;
                }
                List<string> list;
                if (!groups.TryGetValue(baseName, out list))
                {
                    list = new List<string>(2);
                    groups[baseName] = list;
                    try
                    {
                        groupTime[baseName] = File.GetLastWriteTimeUtc(files[i]);
                    }
                    catch
                    {
                        groupTime[baseName] = DateTime.MinValue;
                    }
                }
                list.Add(files[i]);
            }

            if (groups.Count <= keep)
            {
                return deleted;
            }

            var bases = new List<string>(groups.Keys);
            bases.Sort(delegate (string a, string b) { return groupTime[b].CompareTo(groupTime[a]); });

            for (int i = keep; i < bases.Count; i++)
            {
                string baseName = bases[i];
                List<string> list = groups[baseName];
                for (int j = 0; j < list.Count; j++)
                {
                    try
                    {
                        File.Delete(list[j]);
                        deleted.Add(Path.GetFileName(list[j]));
                    }
                    catch (Exception e)
                    {
                        PLog.Warn("删除旧会话文件失败 " + Path.GetFileName(list[j]) + ": " + e.Message);
                    }
                }
            }

            return deleted;
        }

        /// <summary>会话文件名：模式-yyyyMMdd-HHmm-sNN。</summary>
        public static string MakeSessionBaseName(ProbeMode mode, DateTime localTime, int seq)
        {
            return mode.ToTag()
                   + "-" + localTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                   + "-" + localTime.ToString("HHmm", CultureInfo.InvariantCulture)
                   + "-s" + seq.ToString("D2", CultureInfo.InvariantCulture);
        }
    }
}
