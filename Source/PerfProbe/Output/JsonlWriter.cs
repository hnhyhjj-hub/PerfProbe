using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PerfProbe
{
    /// <summary>
    /// jsonl 落盘器：主线程只做入队，写盘在独立后台线程。
    /// 队列有界，满了就丢行并计数（宁可丢样本，也不让主线程被磁盘拖住）。
    /// </summary>
    public sealed class JsonlWriter
    {
        private const int MaxQueue = 8192;
        private const int FlushEveryLines = 16;

        private readonly object _lock = new object();
        private readonly Queue<string> _queue = new Queue<string>(256);
        private readonly List<string> _drain = new List<string>(256);
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        private Thread _thread;
        private volatile bool _stop;
        private StreamWriter _sw;
        private string _path = "";
        private long _dropped;
        private long _written;

        public string Path => _path;

        public long Dropped => Interlocked.Read(ref _dropped);

        public long Written => Interlocked.Read(ref _written);

        public bool IsOpen => _sw != null;

        /// <summary>打开目标文件并启动后台线程。失败返回 false，err 给原因。</summary>
        public bool Open(string dir, string fileName, out string err)
        {
            err = "";
            Close();
            if (string.IsNullOrEmpty(dir))
            {
                err = "输出目录为空";
                return false;
            }
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                _path = System.IO.Path.Combine(dir, fileName);
                var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 16384);
                _sw = new StreamWriter(fs, new UTF8Encoding(false));
                _sw.AutoFlush = false;
            }
            catch (Exception e)
            {
                err = e.GetType().Name + ": " + e.Message;
                _sw = null;
                return false;
            }

            _stop = false;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "PerfProbe.JsonlWriter";
            try
            {
                _thread.Start();
            }
            catch (Exception e)
            {
                err = "启动写盘线程失败: " + e.Message;
                _sw = null;
                return false;
            }
            return true;
        }

        /// <summary>主线程调用。队列满时丢弃并计数，不阻塞。</summary>
        public void Enqueue(string line)
        {
            if (line == null || _sw == null)
            {
                return;
            }
            bool wasEmpty;
            lock (_lock)
            {
                if (_queue.Count >= MaxQueue)
                {
                    Interlocked.Increment(ref _dropped);
                    return;
                }
                wasEmpty = _queue.Count == 0;
                _queue.Enqueue(line);
            }
            if (wasEmpty)
            {
                try { _signal.Set(); } catch { }
            }
        }

        private void Loop()
        {
            while (true)
            {
                try { _signal.WaitOne(500); } catch { }

                _drain.Clear();
                lock (_lock)
                {
                    while (_queue.Count > 0)
                    {
                        _drain.Add(_queue.Dequeue());
                    }
                }

                if (_drain.Count > 0)
                {
                    WriteBatch(_drain);
                }

                if (_stop)
                {
                    // 停止信号到达后再排空一次，确保已入队的行不丢。
                    _drain.Clear();
                    lock (_lock)
                    {
                        while (_queue.Count > 0)
                        {
                            _drain.Add(_queue.Dequeue());
                        }
                    }
                    if (_drain.Count > 0)
                    {
                        WriteBatch(_drain);
                    }
                    Flush();
                    return;
                }
            }
        }

        private void WriteBatch(List<string> lines)
        {
            StreamWriter sw = _sw;
            if (sw == null)
            {
                return;
            }
            try
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    sw.Write(lines[i]);
                    sw.Write('\n');
                }
                Interlocked.Add(ref _written, lines.Count);
                if (lines.Count >= FlushEveryLines)
                {
                    sw.Flush();
                }
            }
            catch (Exception e)
            {
                PLog.ErrOnce("写 jsonl 失败: " + e.Message, 0x9E3702);
            }
        }

        private void Flush()
        {
            try
            {
                _sw?.Flush();
            }
            catch
            {
            }
        }

        /// <summary>停止线程并关闭文件。可重复调用。</summary>
        public void Close()
        {
            _stop = true;
            try { _signal.Set(); } catch { }

            Thread t = _thread;
            _thread = null;
            if (t != null)
            {
                try
                {
                    if (!t.Join(3000))
                    {
                        PLog.Warn("写盘线程 3 秒未退出，强制关闭文件");
                    }
                }
                catch
                {
                }
            }

            try
            {
                _sw?.Flush();
                _sw?.Dispose();
            }
            catch
            {
            }
            _sw = null;

            lock (_lock)
            {
                _queue.Clear();
            }
        }
    }
}
