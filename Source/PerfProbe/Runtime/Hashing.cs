namespace PerfProbe
{
    /// <summary>
    /// 稳定字符串哈希（FNV-1a 32 位）。不用 string.GetHashCode：
    /// 它跨运行不稳定，且 .NET 里对同一字符串的多次调用虽一致，但不保证跨版本。
    /// 这里只需要"同一次会话内同一字符串得到同一键"。
    /// </summary>
    public static class Hashing
    {
        public static int Stable(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return 0;
            }
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619u;
            }
            // 避开 0xFFFFFFFF，那个值被 CostTable 用作 others 槽的哨兵。
            if (h == 0xFFFFFFFFu)
            {
                h = 0xFFFFFFFEu;
            }
            return (int)h;
        }
    }
}
