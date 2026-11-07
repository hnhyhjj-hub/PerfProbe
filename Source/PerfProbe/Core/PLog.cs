using System;
using System.IO;
using System.Text;

namespace PerfProbe
{
    /// <summary>统一日志前缀，便于在 Player.log 里 grep [PerfProbe]。</summary>
    internal static class PLog
    {
        private const string Prefix = "[PerfProbe] ";

        /// <summary>
        /// 同时落一份到文件。
        ///
        /// 为什么必须这样：实测本机装了 RimDoctor 与 ilyvion.LoadingProgress 之后，
        /// Verse.Log.Message 走 logCategory.Info，加载期会被压制（INFO 在某一时刻后整段消失），
        /// 而 Log.Warning/Error 仍能出来。于是「归因索引」「patch 挂载完成」这些关键诊断
        /// 在 Player.log 里查不到，只剩一条 WARN，排查时会被误判成「代码没执行」。
        /// 文件对 INFO/WARN/ERROR 一视同仁，不受任何 mod 的日志过滤影响。
        /// 写失败必须静默：日志本身不能影响游戏。
        /// </summary>
        private static readonly object _lock = new object();

        private static string _path;

        private static bool _pathTried;

        private static string Path()
        {
            if (_pathTried)
            {
                return _path;
            }
            _pathTried = true;
            try
            {
                string dir = ProbePaths.ActiveDir;
                if (string.IsNullOrEmpty(dir))
                {
                    dir = ProbePaths.DefaultDir;
                }
                if (string.IsNullOrEmpty(dir))
                {
                    return null;
                }
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "probe.log");
            }
            catch
            {
                _path = null;
            }
            return _path;
        }

        private static void WriteFile(string level, string s)
        {
            try
            {
                string p = Path();
                if (p == null)
                {
                    return;
                }
                lock (_lock)
                {
                    System.IO.File.AppendAllText(p,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + s
                        + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        public static void Msg(string s)
        {
            try { Verse.Log.Message(Prefix + s); } catch { }
            WriteFile("INFO ", s);
        }

        public static void Warn(string s)
        {
            try { Verse.Log.Warning(Prefix + s); } catch { }
            WriteFile("WARN ", s);
        }

        public static void Err(string s)
        {
            try { Verse.Log.Error(Prefix + s); } catch { }
            WriteFile("ERROR", s);
        }

        public static void ErrOnce(string s, int key)
        {
            try { Verse.Log.ErrorOnce(Prefix + s, key); } catch { }
            WriteFile("ERROR", s);
        }

        public static void Try(string what, Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Err(what + " 失败: " + e);
            }
        }
    }
}

