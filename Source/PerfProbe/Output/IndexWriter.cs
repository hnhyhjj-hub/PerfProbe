using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;

namespace PerfProbe
{
    /// <summary>index.json 里的一条会话记录。</summary>
    public sealed class SessionRecord
    {
        public string File = "";
        public string Summary = "";
        public string Mode = "";
        public string StartedAt = "";
        public double DurationSec;
        public int Lines;
        public double MsPerTickAvg;
        public double P95;
        public int Dropped;
        public bool Fast;
        public bool Superfast;
        public bool Ultrafast;
    }

    /// <summary>
    /// 汇总 index.json。不维护隐藏状态：每次重写前扫描目录，
    /// 从各会话的 summary.md 里抽回关键数字，因此进程重启后历史仍在。
    /// </summary>
    public static class IndexWriter
    {
        public const string FileName = Retention.IndexFileName;

        private static readonly Regex RxMsPerTick =
            new Regex(@"\|\s*\*\*每 tick 均耗时（毫秒）\*\*\s*\|\s*([0-9.]+)\s*\|", RegexOptions.Compiled);

        private static readonly Regex RxP95 =
            new Regex(@"\|\s*tick 耗时 P95（毫秒）\s*\|\s*([0-9.]+)\s*\|", RegexOptions.Compiled);

        private static readonly Regex RxDuration =
            new Regex(@"\|\s*时长（秒）\s*\|\s*([0-9.]+)\s*\|", RegexOptions.Compiled);

        private static readonly Regex RxLines =
            new Regex(@"\|\s*jsonl 行数\s*\|\s*(\d+)\s*\|", RegexOptions.Compiled);

        private static readonly Regex RxDropped =
            new Regex(@"\|\s*写盘丢行数\s*\|\s*(\d+)\s*\|", RegexOptions.Compiled);

        private static readonly Regex RxSpeeds =
            new Regex(@"\|\s*速度档\s*\|\s*(.+?)\s*\|", RegexOptions.Compiled);

        /// <summary>扫描目录重建会话记录，按开始时间倒序。</summary>
        public static List<SessionRecord> Scan(string dir)
        {
            var list = new List<SessionRecord>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return list;
            }
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.summary.md");
            }
            catch (Exception e)
            {
                PLog.Warn("index 扫描失败: " + e.Message);
                return list;
            }

            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                string baseName = Retention.SessionBaseName(name);
                if (baseName == null)
                {
                    continue;
                }
                var rec = new SessionRecord();
                rec.File = baseName + ".jsonl";
                rec.Summary = name;
                rec.Mode = baseName.StartsWith("bench", StringComparison.Ordinal) ? "bench" : "passive";
                try
                {
                    rec.StartedAt = File.GetLastWriteTimeUtc(files[i]).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                }
                catch
                {
                    rec.StartedAt = "";
                }

                string text;
                try
                {
                    text = File.ReadAllText(files[i]);
                }
                catch (Exception e)
                {
                    PLog.Warn("读 summary 失败 " + name + ": " + e.Message);
                    list.Add(rec);
                    continue;
                }

                rec.MsPerTickAvg = MatchNum(RxMsPerTick, text);
                rec.P95 = MatchNum(RxP95, text);
                rec.DurationSec = MatchNum(RxDuration, text);
                rec.Lines = (int)MatchNum(RxLines, text);
                rec.Dropped = (int)MatchNum(RxDropped, text);
                Match m = RxSpeeds.Match(text);
                if (m.Success)
                {
                    // 用词边界匹配：ultrafast / superfast 里都含 "fast"，直接 IndexOf 会误判。
                    string sp = m.Groups[1].Value;
                    rec.Fast = Regex.IsMatch(sp, @"\bfast\b");
                    rec.Superfast = Regex.IsMatch(sp, @"\bsuperfast\b");
                    rec.Ultrafast = Regex.IsMatch(sp, @"\bultrafast\b");
                }
                list.Add(rec);
            }

            list.Sort(delegate (SessionRecord a, SessionRecord b)
            {
                return string.Compare(b.StartedAt, a.StartedAt, StringComparison.Ordinal);
            });
            return list;
        }

        private static double MatchNum(Regex rx, string text)
        {
            Match m = rx.Match(text);
            if (!m.Success)
            {
                return 0.0;
            }
            double v;
            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0.0;
        }

        /// <summary>写 index.json。records 已含本次会话。</summary>
        public static bool Write(string dir, List<SessionRecord> records, int keep, List<string> deleted)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return false;
            }
            var sb = new StringBuilder(8192);
            sb.Append("{\n");
            sb.Append("  \"game\": \"").Append(Json.Esc(GameVersion())).Append("\",\n");
            sb.Append("  \"probe\": \"").Append(Json.Esc(PerfProbeMod.Version)).Append("\",\n");
            sb.Append("  \"generatedAt\": \"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append("\",\n");
            sb.Append("  \"schema\": \"").Append(Retention.SchemaFileName).Append("\",\n");
            sb.Append("  \"sessions\": [");

            if (records != null && records.Count > 0)
            {
                sb.Append('\n');
                for (int i = 0; i < records.Count; i++)
                {
                    SessionRecord r = records[i];
                    sb.Append("    {\"file\":\"").Append(Json.Esc(r.File))
                      .Append("\",\"summary\":\"").Append(Json.Esc(r.Summary))
                      .Append("\",\"mode\":\"").Append(Json.Esc(r.Mode))
                      .Append("\",\"startedAt\":\"").Append(Json.Esc(r.StartedAt))
                      .Append("\",\"durationSec\":").Append(Json.Num(r.DurationSec))
                      .Append(",\"lines\":").Append(r.Lines.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"msPerTickAvg\":").Append(Json.Num(r.MsPerTickAvg))
                      .Append(",\"msPerTickP95\":").Append(Json.Num(r.P95))
                      .Append(",\"dropped\":").Append(r.Dropped.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"speeds\":[");
                    bool first = true;
                    if (r.Fast)
                    {
                        sb.Append("\"fast\"");
                        first = false;
                    }
                    if (r.Superfast)
                    {
                        if (!first)
                        {
                            sb.Append(',');
                        }
                        sb.Append("\"superfast\"");
                        first = false;
                    }
                    if (r.Ultrafast)
                    {
                        if (!first)
                        {
                            sb.Append(',');
                        }
                        sb.Append("\"ultrafast\"");
                    }
                    sb.Append("]}");
                    if (i < records.Count - 1)
                    {
                        sb.Append(',');
                    }
                    sb.Append('\n');
                }
            }

            sb.Append("  ],\n");
            sb.Append("  \"retention\": {\"keep\": ").Append(keep.ToString(CultureInfo.InvariantCulture))
              .Append(", \"deletedThisRun\": [");
            if (deleted != null && deleted.Count > 0)
            {
                for (int i = 0; i < deleted.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append('"').Append(Json.Esc(deleted[i])).Append('"');
                }
            }
            sb.Append("]}\n}\n");

            try
            {
                string path = Path.Combine(dir, FileName);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e)
            {
                PLog.Warn("写 index.json 失败: " + e.Message);
                return false;
            }
        }

        private static string GameVersion()
        {
            try
            {
                return VersionControl.CurrentVersionStringWithRev;
            }
            catch
            {
                return "?";
            }
        }
    }
}
