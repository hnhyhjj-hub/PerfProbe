using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace PerfProbe
{
    /// <summary>
    /// 加载期建的三张只读索引：assembly 到 mod、def 到 mod、type 到 mod。
    /// tick 热路径只查字典，不做反射。
    /// </summary>
    public static class AttribIndex
    {
        private static readonly Dictionary<string, string> _asmToMod =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> _defToMod =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<Type, string> _typeToMod =
            new Dictionary<Type, string>();

        private static bool _built;
        private static int _defCount;
        private static int _typeCount;
        private static int _asmCount;

        public static bool Built => _built;

        public static int DefCount => _defCount;

        public static int TypeCount => _typeCount;

        public static int AsmCount => _asmCount;

        public static void Build()
        {
            if (_built)
            {
                return;
            }
            _asmToMod.Clear();
            _defToMod.Clear();
            _typeToMod.Clear();

            List<ModContentPack> mods = null;
            try
            {
                mods = LoadedModManager.RunningModsListForReading;
            }
            catch (Exception e)
            {
                PLog.Err("读取 RunningModsListForReading 失败: " + e);
                return;
            }
            if (mods == null)
            {
                return;
            }

            for (int i = 0; i < mods.Count; i++)
            {
                ModContentPack mcp = mods[i];
                if (mcp == null)
                {
                    continue;
                }
                string modName = SafeModName(mcp);

                // 1) assembly 到 mod
                try
                {
                    ModAssemblyHandler ah = mcp.assemblies;
                    if (ah != null && ah.loadedAssemblies != null)
                    {
                        for (int j = 0; j < ah.loadedAssemblies.Count; j++)
                        {
                            Assembly asm = ah.loadedAssemblies[j];
                            if (asm == null)
                            {
                                continue;
                            }
                            string an = SafeAsmName(asm);
                            if (an != null && !_asmToMod.ContainsKey(an))
                            {
                                _asmToMod[an] = modName;
                                _asmCount++;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    PLog.Warn("索引 assembly 失败(" + modName + "): " + e.Message);
                }

                // 2) def 到 mod
                try
                {
                    IEnumerable<Def> defs = mcp.AllDefs;
                    if (defs != null)
                    {
                        foreach (Def d in defs)
                        {
                            if (d == null || string.IsNullOrEmpty(d.defName))
                            {
                                continue;
                            }
                            if (!_defToMod.ContainsKey(d.defName))
                            {
                                _defToMod[d.defName] = modName;
                                _defCount++;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    PLog.Warn("索引 def 失败(" + modName + "): " + e.Message);
                }

                // 3) type 到 mod
                try
                {
                    ModAssemblyHandler ah2 = mcp.assemblies;
                    if (ah2 != null && ah2.loadedAssemblies != null)
                    {
                        for (int j = 0; j < ah2.loadedAssemblies.Count; j++)
                        {
                            Assembly asm = ah2.loadedAssemblies[j];
                            if (asm == null)
                            {
                                continue;
                            }
                            Type[] types;
                            try
                            {
                                types = asm.GetTypes();
                            }
                            catch (ReflectionTypeLoadException rtle)
                            {
                                types = rtle.Types;
                            }
                            catch
                            {
                                continue;
                            }
                            if (types == null)
                            {
                                continue;
                            }
                            for (int k = 0; k < types.Length; k++)
                            {
                                Type t = types[k];
                                if (t == null)
                                {
                                    continue;
                                }
                                if (!_typeToMod.ContainsKey(t))
                                {
                                    _typeToMod[t] = modName;
                                    _typeCount++;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    PLog.Warn("索引 type 失败(" + modName + "): " + e.Message);
                }
            }

            _built = true;
            PLog.Msg("归因索引已建立: " + _asmCount + " 个程序集 / " + _defCount + " 个 def / " + _typeCount + " 个类型");
        }

        private static string SafeModName(ModContentPack mcp)
        {
            try
            {
                string n = mcp.Name;
                if (!string.IsNullOrEmpty(n))
                {
                    return n;
                }
            }
            catch
            {
            }
            try
            {
                string p = mcp.PackageId;
                if (!string.IsNullOrEmpty(p))
                {
                    return p;
                }
            }
            catch
            {
            }
            return "Unknown";
        }

        private static string SafeAsmName(Assembly asm)
        {
            try
            {
                AssemblyName an = asm.GetName();
                return an == null ? null : an.Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>按 defName 查所属模组。查不到返回 null。</summary>
        public static string ModOfDef(string defName)
        {
            if (string.IsNullOrEmpty(defName))
            {
                return null;
            }
            string v;
            return _defToMod.TryGetValue(defName, out v) ? v : null;
        }

        /// <summary>按类型查所属模组。查不到返回 null。</summary>
        public static string ModOfType(Type t)
        {
            if (t == null)
            {
                return null;
            }
            string v;
            if (_typeToMod.TryGetValue(t, out v))
            {
                return v;
            }
            // 基类兜底：newslot 子类未登记时向上找一层，置信度由调用方降级。
            Type b = t.BaseType;
            int guard = 0;
            while (b != null && guard < 8)
            {
                if (_typeToMod.TryGetValue(b, out v))
                {
                    return v;
                }
                b = b.BaseType;
                guard++;
            }
            return null;
        }

        /// <summary>按程序集名查所属模组。查不到返回 null。</summary>
        public static string ModOfAssembly(string asmName)
        {
            if (string.IsNullOrEmpty(asmName))
            {
                return null;
            }
            string v;
            return _asmToMod.TryGetValue(asmName, out v) ? v : null;
        }

        /// <summary>按程序集名查模组，用于 CostRow 的 asm 列展示。</summary>
        public static string AsmNameOf(Type t)
        {
            if (t == null)
            {
                return "?";
            }
            try
            {
                Assembly a = t.Assembly;
                if (a == null)
                {
                    return "?";
                }
                AssemblyName an = a.GetName();
                return an == null ? "?" : (an.Name ?? "?");
            }
            catch
            {
                return "?";
            }
        }

        public static void Reset()
        {
            _built = false;
            _asmToMod.Clear();
            _defToMod.Clear();
            _typeToMod.Clear();
            _asmCount = 0;
            _defCount = 0;
            _typeCount = 0;
        }
    }
}
