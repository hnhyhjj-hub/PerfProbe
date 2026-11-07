using System;
using System.IO;
using Verse;

namespace PerfProbe
{
    /// <summary>输出目录解析：默认目录、可写性探测、不可写时回退。</summary>
    public static class ProbePaths
    {
        private static string _defaultDir;

        /// <summary>存档数据目录下的 PerfProbe/。惰性求值，避免加载期访问未初始化的 GenFilePaths。</summary>
        public static string DefaultDir
        {
            get
            {
                if (_defaultDir != null)
                {
                    return _defaultDir;
                }
                try
                {
                    _defaultDir = Path.Combine(GenFilePaths.SaveDataFolderPath, "PerfProbe");
                }
                catch (Exception e)
                {
                    PLog.ErrOnce("无法解析默认输出目录，退到临时目录: " + e.Message, 0x9E3701);
                    _defaultDir = Path.Combine(Path.GetTempPath(), "PerfProbe");
                }
                return _defaultDir;
            }
        }

        /// <summary>实际生效的输出目录（初始化后有效）。</summary>
        public static string ActiveDir { get; private set; } = "";

        /// <summary>
        /// 模组自身根目录（含 About\ 与 docs\）。用于把给 AI 看的指南复制到数据目录，
        /// 让 AI 在一个目录里就能读到「字段口径」与「怎么读」，不必再去找 mod 安装路径。
        /// 取不到时返回空串，调用方按「没有指南」处理，不影响采集。
        /// </summary>
        public static string ModRootDir
        {
            get
            {
                try
                {
                    PerfProbeMod m = PerfProbeMod.Instance;
                    if (m != null && m.Content != null)
                    {
                        return m.Content.RootDir ?? "";
                    }
                }
                catch (Exception e)
                {
                    PLog.Warn("无法解析模组根目录: " + e.Message);
                }
                return "";
            }
        }

        /// <summary>本次是否发生过回退。</summary>
        public static bool FellBack { get; private set; }

        /// <summary>回退原因，供 summary 与设置面板展示。</summary>
        public static string FallbackReason { get; private set; } = "";

        /// <summary>按配置解析并校验输出目录；不可写则回退到默认目录。返回最终生效目录。</summary>
        public static string Resolve(string configured)
        {
            FellBack = false;
            FallbackReason = "";

            string wanted = string.IsNullOrWhiteSpace(configured) ? DefaultDir : configured.Trim();
            string err;
            if (TryMakeWritable(wanted, out err))
            {
                ActiveDir = wanted;
                return ActiveDir;
            }

            FellBack = true;
            FallbackReason = "配置目录不可写（" + err + "）：" + wanted;
            PLog.Warn(FallbackReason + "，回退到默认目录 " + DefaultDir);

            string dflt = DefaultDir;
            if (string.Equals(dflt, wanted, StringComparison.OrdinalIgnoreCase))
            {
                // 默认目录本身不可写：再退一层到临时目录，保证采集仍能产出文件。
                string tmp = Path.Combine(Path.GetTempPath(), "PerfProbe");
                if (TryMakeWritable(tmp, out err))
                {
                    ActiveDir = tmp;
                    FallbackReason += "；默认目录同样不可写，退到临时目录";
                    PLog.Warn("默认目录不可写，退到临时目录 " + tmp);
                    return ActiveDir;
                }
                ActiveDir = "";
                PLog.Err("所有候选输出目录都不可写，本次采集将不落盘: " + err);
                return ActiveDir;
            }

            if (TryMakeWritable(dflt, out err))
            {
                ActiveDir = dflt;
                return ActiveDir;
            }

            string tmp2 = Path.Combine(Path.GetTempPath(), "PerfProbe");
            if (TryMakeWritable(tmp2, out err))
            {
                ActiveDir = tmp2;
                FallbackReason += "；默认目录同样不可写，退到临时目录";
                PLog.Warn("默认目录不可写，退到临时目录 " + tmp2);
                return ActiveDir;
            }

            ActiveDir = "";
            PLog.Err("所有候选输出目录都不可写，本次采集将不落盘: " + err);
            return ActiveDir;
        }

        /// <summary>建目录 + 试写一个探针文件并删除，验证真实可写。</summary>
        public static bool TryMakeWritable(string dir, out string err)
        {
            err = "";
            if (string.IsNullOrWhiteSpace(dir))
            {
                err = "路径为空";
                return false;
            }
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string probe = Path.Combine(dir, ".perfprobe-write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception e)
            {
                err = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }
    }
}
