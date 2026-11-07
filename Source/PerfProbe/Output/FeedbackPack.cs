using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Verse;

namespace PerfProbe
{
    /// <summary>
    /// 反馈包：把排查一次卡顿所需的全部文件打成一个 zip，玩家点一下就能发出来。
    ///
    /// 为什么要有它：让玩家自己去找「存档目录 / Config 目录 / Logs 目录」三个位置、
    /// 挑出正确的会话文件、再把 4.7 MB 的 Player.log 裁到能上传 —— 实测完成率极低，
    /// 而且收到的东西通常缺 mod 列表或只有半截日志。
    ///
    /// 刻意不收集硬件信息（CPU / 显卡 / 内存 / 主板序列号）。那些 API 能读到的东西
    /// 远超排查所需，一旦进了包就等于默认同意上传；缺少 CPU 型号的代价只是无法横向
    /// 比较绝对性能，而 mod 列表已经在包里，归因链条是完整的。
    /// </summary>
    public static class FeedbackPack
    {
        /// <summary>包内文件名前缀。带时间戳，同一目录下多次导出不会互相覆盖。</summary>
        private const string NamePrefix = "PerfProbe-feedback-";

        /// <summary>Player.log 头部保留行数：加载期在这里，mod 初始化顺序与失败都在这段。</summary>
        private const int LogHeadLines = 400;

        /// <summary>裁剪后 Player.log 的字节上限，超了按行截断并注明。</summary>
        private const int LogMaxBytes = 3 * 1024 * 1024;

        /// <summary>日志里遇到这些标记的行一律保留（大小写不敏感）。</summary>
        private static readonly string[] LogKeep =
        {
            "perfprobe", "error", "exception", "warning", "fail", "harmony", "patch"
        };

        /// <summary>
        /// Windows 用户名段：C:\Users\张三\... → C:\Users\<用户名>\...
        /// 这是我们自己 summary 里「输出目录」一行的泄漏点，也覆盖 Player.log 里的路径。
        /// </summary>
        private static readonly Regex UserPathRx =
            new Regex(@"(?i)([A-Z]:[\\/]+Users[\\/]+)([^\\/\r\n""]+)", RegexOptions.Compiled);

        /// <summary>类 Unix 家目录：/home/ubuntu/... 与 /Users/xxx/... 同理。</summary>
        private static readonly Regex UnixHomeRx =
            new Regex(@"(?i)((?:/home/|/Users/))([^/\r\n""]+)", RegexOptions.Compiled);

        /// <summary>尚未结束的会话没有 summary，此时只能给 jsonl 与 probe.log。</summary>
        public static string Create(out string err)
        {
            err = "";
            string dir = ProbePaths.ActiveDir;
            if (string.IsNullOrEmpty(dir))
            {
                err = "输出目录未就绪，请先开始一次采集";
                return "";
            }
            if (!Directory.Exists(dir))
            {
                err = "输出目录不存在：" + dir;
                return "";
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string zipPath = Path.Combine(dir, NamePrefix + stamp + ".zip");

            try
            {
                var items = new List<KeyValuePair<string, string>>();
                CollectItems(dir, items);
                if (items.Count == 0)
                {
                    err = "没有可打包的文件（至少要有一个会话）";
                    return "";
                }

                using (FileStream fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    string readme = BuildReadme(dir, items);
                    WriteText(zip, "README.txt", readme);

                    for (int i = 0; i < items.Count; i++)
                    {
                        string src = items[i].Key;
                        string entryName = items[i].Value;
                        if (IsPlayerLog(entryName))
                        {
                            WriteTrimmedLog(zip, entryName, src);
                        }
                        else
                        {
                            WriteFileSanitized(zip, entryName, src);
                        }
                    }
                }

                PLog.Msg("反馈包已生成: " + zipPath);
                return zipPath;
            }
            catch (Exception e)
            {
                err = e.GetType().Name + ": " + e.Message;
                PLog.Warn("生成反馈包失败: " + err);
                try
                {
                    if (File.Exists(zipPath))
                    {
                        File.Delete(zipPath);
                    }
                }
                catch
                {
                }
                return "";
            }
        }

        /// <summary>
        /// 决定往包里放什么。顺序即包内顺序：先读 README，再按「结论 → 明细 → 环境」。
        /// </summary>
        private static void CollectItems(string dir, List<KeyValuePair<string, string>> items)
        {
            string baseName = LatestSessionBase(dir);

            if (!string.IsNullOrEmpty(baseName))
            {
                AddIfExists(items, Path.Combine(dir, baseName + ".brief.md"), "1-报告/" + baseName + ".brief.md");
                AddIfExists(items, Path.Combine(dir, baseName + ".summary.md"), "1-报告/" + baseName + ".summary.md");
                // jsonl 动辄 3 MB 且绝大多数分析用不上，只在玩家没有 summary 时补位。
                if (!File.Exists(Path.Combine(dir, baseName + ".summary.md")))
                {
                    AddIfExists(items, Path.Combine(dir, baseName + ".jsonl"), "1-报告/" + baseName + ".jsonl");
                }
            }

            AddIfExists(items, Path.Combine(dir, Retention.IndexFileName), "2-索引/index.json");
            AddIfExists(items, Path.Combine(dir, Retention.SchemaFileName), "2-索引/schema.md");
            AddIfExists(items, Path.Combine(dir, "AI-ANALYSIS-GUIDE.md"), "2-索引/AI-ANALYSIS-GUIDE.md");

            AddIfExists(items, Path.Combine(dir, "probe.log"), "3-日志/probe.log");
            string playerLog = Path.Combine(dir, "Player.log");
            if (!File.Exists(playerLog))
            {
                playerLog = SafeConsoleLogPath();
            }
            AddIfExists(items, playerLog, "3-日志/Player.log");

            AddIfExists(items, SafeModsConfigPath(), "4-环境/ModsConfig.xml");
        }

        private static void AddIfExists(List<KeyValuePair<string, string>> items, string src, string entry)
        {
            if (string.IsNullOrEmpty(src))
            {
                return;
            }
            try
            {
                if (File.Exists(src))
                {
                    items.Add(new KeyValuePair<string, string>(src, entry));
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 最新会话的基名。文件名形如 passive-20261102-1948-s03.summary.md，
        /// 时间戳定长，所以字典序排序即时间序，不需要读文件内容。
        /// </summary>
        private static string LatestSessionBase(string dir)
        {
            try
            {
                string[] files = Directory.GetFiles(dir, "*.summary.md");
                if (files == null || files.Length == 0)
                {
                    files = Directory.GetFiles(dir, "*.jsonl");
                }
                if (files == null || files.Length == 0)
                {
                    return "";
                }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                string last = Path.GetFileName(files[files.Length - 1]);
                int dot = last.IndexOf('.');
                return dot < 0 ? "" : last.Substring(0, dot);
            }
            catch
            {
                return "";
            }
        }

        private static bool IsPlayerLog(string entryName)
        {
            return entryName != null && entryName.EndsWith("Player.log", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Player.log 裁剪：保留头部若干行（加载期）+ 所有命中关键字的行。
        /// 实测 4.7 MB 原文件裁完约 400 KB，不影响定位问题。
        /// </summary>
        private static void WriteTrimmedLog(ZipArchive zip, string entryName, string src)
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
            using (Stream es = entry.Open())
            using (var sw = new StreamWriter(es, new UTF8Encoding(false)))
            {
                int kept = 0;
                long bytes = 0;
                bool truncated = false;
                int lineNo = 0;
                foreach (string raw in ReadLines(src))
                {
                    lineNo++;
                    string line = raw ?? "";
                    bool keep = lineNo <= LogHeadLines || MatchesKeep(line);
                    if (!keep)
                    {
                        continue;
                    }
                    bytes += line.Length + 2;
                    if (bytes > LogMaxBytes)
                    {
                        truncated = true;
                        break;
                    }
                    sw.WriteLine(Sanitize(line));
                    kept++;
                }
                if (truncated)
                {
                    sw.WriteLine("[PerfProbe] 日志超过 " + (LogMaxBytes / 1024 / 1024) + " MB，已截断；原始共 "
                                 + lineNo + " 行，本文件保留 " + kept + " 行。");
                }
                sw.WriteLine();
                sw.WriteLine("[PerfProbe] 已裁剪：保留头部 " + LogHeadLines + " 行与所有含 "
                             + string.Join(" / ", LogKeep) + " 的行。原文共 " + lineNo + " 行。");
            }
        }

        private static bool MatchesKeep(string line)
        {
            for (int i = 0; i < LogKeep.Length; i++)
            {
                if (line.IndexOf(LogKeep[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>按行流式读，避免把 4.7 MB 的日志整个读成一个字符串。</summary>
        private static IEnumerable<string> ReadLines(string path)
        {
            FileStream fs = null;
            StreamReader sr = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                sr = new StreamReader(fs, Encoding.UTF8, true);
            }
            catch
            {
                if (sr != null)
                {
                    sr.Dispose();
                }
                else if (fs != null)
                {
                    fs.Dispose();
                }
                yield break;
            }
            using (sr)
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    yield return line;
                }
            }
        }

        /// <summary>文本类文件写包内时做一次脱敏；二进制（不存在）走原样复制。</summary>
        private static void WriteFileSanitized(ZipArchive zip, string entryName, string src)
        {
            string text = null;
            try
            {
                // 这些文件都是 UTF-8 文本，且单个不超过几 MB，整读再脱敏成本可接受。
                if (new FileInfo(src).Length <= 16 * 1024 * 1024)
                {
                    text = File.ReadAllText(src, Encoding.UTF8);
                }
            }
            catch
            {
                text = null;
            }

            if (text == null)
            {
                ZipArchiveEntry raw = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
                using (Stream es = raw.Open())
                using (FileStream fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.CopyTo(es);
                }
                return;
            }

            WriteText(zip, entryName, Sanitize(text));
        }

        private static void WriteText(ZipArchive zip, string entryName, string text)
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
            using (Stream es = entry.Open())
            using (var sw = new StreamWriter(es, new UTF8Encoding(false)))
            {
                sw.Write(text);
            }
        }

        /// <summary>把本机用户名从路径里抹掉。只改路径段，不动其他内容。</summary>
        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }
            try
            {
                s = UserPathRx.Replace(s, "$1<用户名>");
                s = UnixHomeRx.Replace(s, "$1<用户名>");
            }
            catch
            {
            }
            return s;
        }

        /// <summary>包内说明：告诉收到的人这是什么、先看哪个、哪些数字不能相加。</summary>
        private static string BuildReadme(string dir, List<KeyValuePair<string, string>> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PerfProbe 反馈包");
            sb.AppendLine("================");
            sb.AppendLine();
            sb.AppendLine("导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("探针版本：" + PerfProbeMod.Version);
            sb.AppendLine("游戏版本：" + GameVersion());
            sb.AppendLine("导出目录：" + Sanitize(dir));
            sb.AppendLine();
            sb.AppendLine("包内文件（共 " + (items.Count + 1) + " 个）：");
            sb.AppendLine();

            string baseName = LatestSessionBase(dir);
            if (!string.IsNullOrEmpty(baseName))
            {
                sb.AppendLine("  1-报告/" + baseName + ".brief.md      先看这个。约 10 KB，含会话头与主要归因结论");
                if (File.Exists(Path.Combine(dir, baseName + ".summary.md")))
                {
                    sb.AppendLine("  1-报告/" + baseName + ".summary.md    完整报告，含全量附录与逐项明细");
                }
            }
            sb.AppendLine("  2-索引/index.json            所有会话的横向对比（每轮 msPerTick 与 TPS）");
            sb.AppendLine("  2-索引/schema.md             字段口径：每个字段什么意思");
            sb.AppendLine("  2-索引/AI-ANALYSIS-GUIDE.md  判读指南：怎么读、哪些数不能相加、常见误判");
            sb.AppendLine("  3-日志/probe.log             探针自己的日志：挂了多少 patch、有没有失败");
            sb.AppendLine("  3-日志/Player.log            游戏日志（已裁剪，只留加载期与报错）");
            sb.AppendLine("  4-环境/ModsConfig.xml        完整 mod 列表与加载顺序");
            sb.AppendLine();
            sb.AppendLine("怎么用：把整个 zip 或其中任一文件交给 AI，让它先读 2-索引/AI-ANALYSIS-GUIDE.md。");
            sb.AppendLine();
            sb.AppendLine("注意事项：");
            sb.AppendLine("  · 本包不含硬件信息（CPU / 显卡 / 内存）—— 导出时就没有采集。");
            sb.AppendLine("  · 路径里的用户名已替换为 <用户名>，但 mod 列表与日志内容为原文。");
            sb.AppendLine("  · 报告里「引擎自身」一栏是游戏引擎的基础开销，换任何 mod 都去不掉，");
            sb.AppendLine("    不要把它当成某个模组的问题。");
            sb.AppendLine("  · 人物层的 pawn_body / pawn_interval / pawn_other 三项之和才是全部 pawn 时间，");
            sb.AppendLine("    其余 pawn_* 行是 pawn_body 的内部细分，不能重复相加。");
            return sb.ToString();
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

        private static string SafeModsConfigPath()
        {
            try
            {
                return GenFilePaths.ModsConfigFilePath;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Player.log 的实际位置。优先跟着存档数据目录走（-savedatafolder 会整体搬走），
        /// 取不到时退回引擎给的 consoleLogPath。
        /// </summary>
        private static string SafeConsoleLogPath()
        {
            try
            {
                string root = Path.GetDirectoryName(GenFilePaths.SaveDataFolderPath);
                if (!string.IsNullOrEmpty(root))
                {
                    string p = Path.Combine(root, "Logs", "Player.log");
                    if (File.Exists(p))
                    {
                        return p;
                    }
                }
            }
            catch
            {
            }
            try
            {
                return Application.consoleLogPath ?? "";
            }
            catch
            {
                return "";
            }
        }
    }
}
