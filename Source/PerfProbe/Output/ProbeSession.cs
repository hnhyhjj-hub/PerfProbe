using System;
using System.IO;

namespace PerfProbe
{
    /// <summary>
    /// 一次采集会话的状态。读档或点开始按钮时新建，结束或停止时关闭。
    /// 一个会话对应三个文件：xxx.jsonl、xxx.summary.md、xxx.brief.md。
    /// brief 是给网页版 / 纯对话 AI 用的精简版，只含前段结论，不含附录全量。
    /// </summary>
    public sealed class ProbeSession
    {
        public ProbeMode Mode;
        public string BaseName = "";
        public string JsonlPath = "";
        public string SummaryPath = "";
        public string BriefPath = "";
        public DateTime StartLocal;
        public DateTime StartUtc;
        public long StartTs;
        public int StartGameTick;
        public int Lines;
        public bool FastSeen;
        public bool SuperfastSeen;
        public bool UltrafastSeen;

        public readonly JsonlWriter Writer = new JsonlWriter();

        public double ElapsedSec
        {
            get
            {
                if (StartTs <= 0)
                {
                    return 0.0;
                }
                return Qpc.ToMs(Qpc.Now() - StartTs) / 1000.0;
            }
        }

        public void RecordSpeed(SpeedKind k)
        {
            if (k == SpeedKind.Fast)
            {
                FastSeen = true;
            }
            else if (k == SpeedKind.Superfast)
            {
                SuperfastSeen = true;
            }
            else if (k == SpeedKind.Ultrafast)
            {
                UltrafastSeen = true;
            }
        }

        /// <summary>打开 jsonl 文件。失败时 Writer 保持关闭，采集仍继续（只是不落盘）。</summary>
        public bool OpenFiles(string dir, out string err)
        {
            err = "";
            JsonlPath = Path.Combine(dir, BaseName + ".jsonl");
            SummaryPath = Path.Combine(dir, BaseName + ".summary.md");
            BriefPath = Path.Combine(dir, BaseName + ".brief.md");
            return Writer.Open(dir, BaseName + ".jsonl", out err);
        }

        public void CloseFiles()
        {
            Writer.Close();
        }
    }
}
