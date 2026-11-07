using System;
using System.IO;

namespace PerfProbe
{
    /// <summary>
    /// 把模组自带的 AI 分析指南复制到输出目录。
    ///
    /// 为什么需要这个：玩家拿数据去问 AI 时，AI 只看得到数据目录里的文件。
    /// 若指南只躺在 mod 安装路径（workshop 深层目录）里，AI 根本不知道它存在，
    /// 也就不会去读「怎么判读」那一套规则，只能凭字段名猜 —— 这是最容易得出
    /// 错误结论的场景（例如把 cat=mod 的汇总值当明细相加）。
    ///
    /// 复制而非重写：指南内容会随版本更新，用文件复制保证只有一份真实来源
    /// （docs/AI-ANALYSIS-GUIDE.md），不会出现代码里藏一份、文档里一份的分叉。
    /// 代价是发布包里必须带 docs/，这是刻意的。
    /// </summary>
    public static class GuideWriter
    {
        /// <summary>数据目录里的文件名。保留 .md 后缀，AI 与人类都按文档处理。</summary>
        public const string FileName = "AI-ANALYSIS-GUIDE.md";

        private const string RelPath = "docs/AI-ANALYSIS-GUIDE.md";

        /// <summary>
        /// 把指南复制到 dir。文件不存在、内容一致、或读不到模组目录时静默跳过 ——
        /// 指南是辅助材料，缺了不该影响采集主流程。
        /// </summary>
        public static void Write(string dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }
            try
            {
                string root = ProbePaths.ModRootDir;
                if (string.IsNullOrEmpty(root))
                {
                    return;
                }
                string src = Path.Combine(root, RelPath);
                if (!File.Exists(src))
                {
                    // 开发目录没带 docs 时属正常（例如只复制了运行必需件），不打扰玩家。
                    return;
                }

                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string dst = Path.Combine(dir, FileName);

                // 内容一致就不重写，避免每次开新会话都动一次文件时间戳。
                if (File.Exists(dst))
                {
                    string a = File.ReadAllText(src);
                    string b = File.ReadAllText(dst);
                    if (string.Equals(a, b, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                File.Copy(src, dst, true);
            }
            catch (Exception e)
            {
                PLog.Warn("复制 AI 分析指南失败: " + e.Message);
            }
        }
    }
}
