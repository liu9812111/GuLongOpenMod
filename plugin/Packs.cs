// 内容包：游戏目录\Mod\Packs\{包}\ 下每个带 pack.json 的文件夹就是一个包。
//
// 包的结构（详见 docs/内容包格式.md）：
//   pack.json                  包的名字、作者、版本、Id 前缀…
//   tables\{表名}.txt           增量表：只写表头 + 新增/修改的行，加载时合并进原表（简繁通用）
//   tables_tc\{表名}.txt        可选：繁体专用，有它时繁体用它代替 tables\ 里的同名表
//   files\...                  按游戏资源路径放的整个文件，如 files\Image\Icon\Item\xxx.png、files\Config\Cinematic\xxx.json
//   skillmod\{演出名}.json      改招配置（格式同 Mod\SkillMod）
//
// 加载顺序：按 pack.json 的 order 从小到大，相同再按文件夹名。后加载的覆盖先加载的。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using Newtonsoft.Json;

namespace GuLongOpenMod
{
#pragma warning disable CS0649   // 下面的字段由 Newtonsoft.Json 读 pack.json 时通过反射赋值
    internal class PackInfo
    {
        // ── pack.json 里的字段 ──
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("version")] public string Version;
        [JsonProperty("authors")] public string[] Authors;
        [JsonProperty("description")] public string Description;
        [JsonProperty("idPrefix")] public string[] IdPrefix;   // 新增行的 Id 必须以其中之一开头
        [JsonProperty("requires")] public string[] Requires;   // 依赖的其他包 id
        [JsonProperty("order")] public int Order;

        [JsonIgnore] public string Dir;
        public override string ToString() => string.IsNullOrEmpty(Name) ? Id : $"{Name}({Id})";
    }
#pragma warning restore CS0649

    internal static class Packs
    {
        internal static ConfigEntry<string> Disabled;
        internal static ConfigEntry<bool> LogLoads;
        internal static ConfigEntry<string> DevDirs;

        internal static readonly List<PackInfo> Loaded = new List<PackInfo>();

        // 本次扫描后已经合并过的表名（不含扩展名），用来发现没经过合并的表
        internal static readonly HashSet<string> Merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 资源相对路径（小写、/ 分隔）→ 文件完整路径
        static readonly Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, PackInfo> fileOwner = new Dictionary<string, PackInfo>(StringComparer.OrdinalIgnoreCase);
        // 表的相对路径（如 skillitem.txt、gameevent/xxx_task.txt）→ 各包的增量文件（按加载顺序）
        static readonly Dictionary<string, List<KeyValuePair<PackInfo, string>>> tablesSc = new Dictionary<string, List<KeyValuePair<PackInfo, string>>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<KeyValuePair<PackInfo, string>>> tablesTc = new Dictionary<string, List<KeyValuePair<PackInfo, string>>>(StringComparer.OrdinalIgnoreCase);

        internal static string Root => Path.Combine(Paths.GameRootPath, Heluo.GameConfig.ModPath, "Packs");

        // 游戏读表用的两个目录（LanguageSetting：Config/TextFiles/ 和 Config/TextFiles(SC)/）
        const string TcDir = "config/textfiles/";
        const string ScDir = "config/textfiles(sc)/";

        internal static void Bind(ConfigFile cfg)
        {
            Disabled = cfg.Bind("内容包", "禁用的包", "",
                "不想加载的内容包 id（pack.json 里的 id），多个用逗号分隔。改完要重启游戏，或按 F5 重新加载");
            LogLoads = cfg.Bind("内容包", "记录资源读取", false,
                "在日志里打印游戏从内容包里读了哪些文件，排查「放了文件却不生效」用");
            DevDirs = cfg.Bind("内容包", "开发目录", "",
                "做内容时用：额外读取这些目录下的内容包，多个用分号分隔，例如 Z:\\home\\ljy\\Game\\GuLong_handoff\\GuLongOpenMod\\packs\n" +
                "同一个包（id 相同）开发目录里的优先，Mod\\Packs 里的副本会被忽略。包里的文件改完按 F5 / F7 直接生效，不用复制。这一项本身改了之后要按 F5 或重启游戏");
        }

        internal static void Scan()
        {
            Loaded.Clear(); Merged.Clear(); files.Clear(); fileOwner.Clear(); tablesSc.Clear(); tablesTc.Clear();
            var roots = new List<string>();
            foreach (var d in (DevDirs?.Value ?? "").Split(';', '；').Select(x => x.Trim().Trim('"')).Where(x => x.Length > 0))
            {
                if (Directory.Exists(d)) roots.Add(d);
                else Plugin.Log.LogWarning("[内容包] 开发目录不存在：" + d);
            }
            if (Directory.Exists(Root)) roots.Add(Root);
            if (roots.Count == 0)
            {
                Plugin.Log.LogInfo("[内容包] 没有内容包目录：" + Root);
                return;
            }

            var off = new HashSet<string>((Disabled?.Value ?? "").Split(',', '，').Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
            var found = new List<PackInfo>();
            foreach (var dir in roots.SelectMany(r => Directory.GetDirectories(r)))
            {
                string json = Path.Combine(dir, "pack.json");
                if (!File.Exists(json)) { Plugin.Log.LogWarning("[内容包] " + dir + " 里没有 pack.json，不是内容包，已跳过"); continue; }
                try
                {
                    var p = JsonConvert.DeserializeObject<PackInfo>(File.ReadAllText(json));
                    if (p == null) continue;
                    p.Dir = dir;
                    if (string.IsNullOrEmpty(p.Id)) p.Id = Path.GetFileName(dir);
                    if (off.Contains(p.Id)) { Plugin.Log.LogInfo("[内容包] 已禁用：" + p); continue; }
                    var same = found.FirstOrDefault(x => string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase));
                    if (same != null)
                    {
                        if (Path.GetDirectoryName(same.Dir) != Path.GetDirectoryName(dir))
                            Plugin.Log.LogInfo($"[内容包] {p} 使用 {same.Dir}，忽略 {dir}");
                        else
                            Plugin.Log.LogError("[内容包] id 重复：" + p.Id + "（" + Path.GetFileName(dir) + "），已跳过");
                        continue;
                    }
                    found.Add(p);
                }
                catch (Exception e) { Plugin.Log.LogError("[内容包] " + Path.GetFileName(dir) + "\\pack.json 格式有误：" + e.Message); }
            }

            foreach (var p in found.OrderBy(x => x.Order).ThenBy(x => Path.GetFileName(x.Dir), StringComparer.OrdinalIgnoreCase))
            {
                Loaded.Add(p);
                IndexFiles(p, Path.Combine(p.Dir, "files"), files, true);
                IndexTables(p, Path.Combine(p.Dir, "tables"), tablesSc);
                IndexTables(p, Path.Combine(p.Dir, "tables_tc"), tablesTc);
            }

            foreach (var p in Loaded)
                foreach (var r in p.Requires ?? new string[0])
                    if (!Loaded.Any(x => string.Equals(x.Id, r, StringComparison.OrdinalIgnoreCase)))
                        Plugin.Log.LogWarning($"[内容包] {p} 依赖的包 {r} 没有安装或被禁用");

            Plugin.Log.LogInfo($"[内容包] 已加载 {Loaded.Count} 个：" + string.Join("，", Loaded.Select(x => x + " " + x.Version)));
            foreach (var p in Loaded) Plugin.Log.LogInfo($"[内容包]   {p} ← {p.Dir}");
        }

        static void IndexFiles(PackInfo p, string dir, Dictionary<string, string> into, bool track)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = Rel(dir, f);
                if (track && fileOwner.TryGetValue(rel, out var prev))
                    Plugin.Log.LogWarning($"[内容包] {p} 覆盖了 {prev} 的文件 {rel}");
                into[rel] = f;
                if (track) fileOwner[rel] = p;
            }
        }

        static void IndexTables(PackInfo p, string dir, Dictionary<string, List<KeyValuePair<PackInfo, string>>> into)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories))
            {
                string rel = Rel(dir, f);
                if (!into.TryGetValue(rel, out var list)) into[rel] = list = new List<KeyValuePair<PackInfo, string>>();
                list.Add(new KeyValuePair<PackInfo, string>(p, f));
            }
        }

        static string Rel(string dir, string file) =>
            file.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/');

        static string Norm(string path) => (path ?? "").Replace('\\', '/').TrimStart('/');

        // ── 给 ModProvider 用 ──

        /// <summary>游戏要读的是不是某张表、并且有包要往里加东西。</summary>
        internal static bool TryGetTableDeltas(string path, out List<KeyValuePair<PackInfo, string>> deltas)
        {
            deltas = null;
            string p = Norm(path);
            bool sc = p.StartsWith(ScDir, StringComparison.OrdinalIgnoreCase);
            bool tc = !sc && p.StartsWith(TcDir, StringComparison.OrdinalIgnoreCase);
            if (!sc && !tc) return false;
            string rel = p.Substring(sc ? ScDir.Length : TcDir.Length);

            tablesSc.TryGetValue(rel, out var common);
            if (!tc) { deltas = common; return deltas != null; }

            // 繁体：某个包有 tables_tc 就用它的，没有就用它 tables 里的
            tablesTc.TryGetValue(rel, out var tcOnly);
            if (common == null && tcOnly == null) return false;
            deltas = new List<KeyValuePair<PackInfo, string>>();
            foreach (var pack in Loaded)
            {
                var own = tcOnly?.FirstOrDefault(x => x.Key == pack) ?? default;
                if (own.Key == null) own = common?.FirstOrDefault(x => x.Key == pack) ?? default;
                if (own.Key != null) deltas.Add(own);
            }
            return deltas.Count > 0;
        }

        /// <summary>某个资源路径在内容包的 files\ 里有没有对应文件。</summary>
        internal static bool TryGetFile(string path, out string full, bool log = true)
        {
            full = null;
            if (path == null || files.Count == 0) return false;
            if (!files.TryGetValue(Norm(path), out full)) return false;
            if (log && LogLoads != null && LogLoads.Value)
                Plugin.Log.LogInfo($"[内容包] 读取 {path} ← {fileOwner[Norm(path)]}");
            return true;
        }

        /// <summary>所有包里的某个子目录（如 skillmod），按加载顺序。</summary>
        internal static IEnumerable<string> SubDirs(string sub)
        {
            foreach (var p in Loaded)
            {
                string d = Path.Combine(p.Dir, sub);
                if (Directory.Exists(d)) yield return d;
            }
        }

        /// <summary>数据类型 → 表名（不含扩展名）；分布式表返回 null。</summary>
        internal static string TableName(Type itemType)
        {
            if (itemType.GetCustomAttributes(typeof(Heluo.Data.Source.DistributedAttribute), false).Length > 0) return null;
            var fn = itemType.GetCustomAttributes(typeof(Heluo.Data.Source.FileNameAttribute), false).FirstOrDefault() as Heluo.Data.Source.FileNameAttribute;
            return fn != null ? fn.Name : itemType.Name;
        }

        /// <summary>当前语言的表目录（Config/TextFiles(SC)/ 或 Config/TextFiles/）。</summary>
        internal static string CurrentTablePath() =>
            Heluo.Game.Setting != null && Heluo.Game.Setting.CurrentLanguage == Heluo.LanguageState.TC
                ? Heluo.GameConfig.LanguageSetting.TraditionalChinese
                : Heluo.GameConfig.LanguageSetting.SimpleChinese;

        /// <summary>某个数据类型对应的表有没有被内容包改动（和 DataManager 一样：有 FileName 特性用它，否则用类名）。</summary>
        internal static bool TouchesTable(Type itemType)
        {
            if (itemType == null || (tablesSc.Count == 0 && tablesTc.Count == 0)) return false;
            string t = TableName(itemType);
            if (t == null) return false;
            return tablesSc.ContainsKey(t + ".txt") || tablesTc.ContainsKey(t + ".txt");
        }

        /// <summary>所有包里涉及的表名（不含扩展名），F5 重载用。</summary>
        internal static IEnumerable<string> TouchedTables() =>
            tablesSc.Keys.Concat(tablesTc.Keys).Select(k => k.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? k.Substring(0, k.Length - 4) : k)
                .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
