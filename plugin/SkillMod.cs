// 改招：在古龙原版招式演出的基础上改，配置放在 Mod\SkillMod\{演出名}.json。
//   · 文件名和某个原版演出同名（如 gl_skill_empty_107.json）= 直接改这个演出，所有用它的招式都会变
//   · 文件名是新名字（如 xk_long.json）= 生成一个新演出，再把 SkillItem 表里某招的 AnimationName 改成这个名字
//   能做的：从别的招式借特效（add）、删掉原有特效（remove）、调整原有特效（edit），
//           每个特效都能改出现时间、播放速度、大小、位置、角度。
//   改完 json 按 F7 重新读取（招式预览窗口开着的话会自动重播）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Heluo;
using Heluo.Animation;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UObject = UnityEngine.Object;

namespace GuLongOpenMod
{
    public class SkillModDef
    {
        public string @base;                              // 底子演出；不填 = 和文件同名
        public List<FxEdit> add = new List<FxEdit>();     // 从别的演出借来的攻击方特效
        public List<FxEdit> edit = new List<FxEdit>();    // 调整底子里已有的攻击方特效
        public List<string> remove = new List<string>();  // 删掉底子里这些名字的攻击方特效
    }

    public class FxEdit
    {
        public string from;        // add 用：从哪个演出借
        public string fx;          // 特效名（预览窗口里列出的名字）
        public int index;          // 同名特效有多个时取第几个（从 0 开始）
        public float? time;        // 出现时间（秒）；不填 = 原时间
        public float? speed;       // 播放速度倍率；不填 = 原速度
        public float? scale;       // 大小倍率（乘在原来的大小上）
        public float[] move;       // 位置偏移 [左右, 上下, 前后]（米，加在原位置上）
        public float[] rotate;     // 再旋转 [x, y, z]（度）
        public float[] color;      // 颜色倍率 [红, 绿, 蓝] 或 [红, 绿, 蓝, 透明度]，1 = 不变；白/银色特效乘上去就变成这个颜色
        public float? hue;         // 色相旋转（度，-180~180），把彩色特效整体换色：如 红→绿 约 120
        public float? brightness;  // 亮度倍率，1 = 不变
        public Dictionary<string, float[]> props;   // 直接指定材质属性：颜色写 [r,g,b] 或 [r,g,b,a]，数值写 [x]，如 "_RimColor": [1,0.8,0.2]、"_Metallic": [1]
    }

    internal static class SkillMod
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<KeyboardShortcut> ReloadKey;
        const string Tag = "SkillMod";   // 改过的特效资源名带这个前缀，用来识别

        static readonly Dictionary<string, SkillModDef> defs = new Dictionary<string, SkillModDef>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, TimelineAsset> cache = new Dictionary<string, TimelineAsset>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> building = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string prefix, suffix;

        const BindingFlags NP = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly MethodInfo mAddChild = typeof(TrackAsset).GetMethod("AddChild", NP);
        static readonly MethodInfo mRemoveClip = typeof(TrackAsset).GetMethod("RemoveClip", NP);
        static readonly MethodInfo mClearChildren = typeof(TrackAsset).GetMethod("ClearSubTracksInternal", NP);
        static readonly FieldInfo fMarkers = typeof(TrackAsset).GetField("m_Markers", NP);

        static string Dir => Path.Combine(Path.Combine(Paths.GameRootPath, GameConfig.ModPath), "SkillMod");

        // Mod\SkillMod 加上各内容包的 skillmod\，后面的同名文件覆盖前面的
        static IEnumerable<string> Dirs()
        {
            if (Directory.Exists(Dir)) yield return Dir;
            foreach (var d in Packs.SubDirs("skillmod")) yield return d;
        }

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("改招", "启用", true, "按 Mod\\SkillMod\\*.json 和内容包 skillmod\\*.json 修改招式演出");
            ReloadKey = cfg.Bind("改招", "重新读取热键", new KeyboardShortcut(KeyCode.F7), "改完 json 按一下立即生效（预览窗口开着会自动重播）");
            string fmt = GameConfig.SkillTimelinePath;
            int i = fmt.IndexOf("{0}", StringComparison.Ordinal);
            prefix = i >= 0 ? fmt.Substring(0, i) : fmt;
            suffix = i >= 0 ? fmt.Substring(i + 3) : "";
            Reload();
        }

        internal static void Tick()
        {
            if (ReloadKey == null || !ReloadKey.Value.IsDown()) return;
            Reload();
            SkillPreview.Replay();
        }

        internal static void Reload()
        {
            defs.Clear(); cache.Clear();
            foreach (var f in Dirs().SelectMany(d => Directory.GetFiles(d, "*.json")))
            {
                try
                {
                    var d = JsonConvert.DeserializeObject<SkillModDef>(File.ReadAllText(f));
                    if (d != null) defs[Path.GetFileNameWithoutExtension(f)] = d;
                }
                catch (Exception e) { Plugin.Log.LogError($"[改招] {Path.GetFileName(f)} 格式有误：{e.Message}"); }
            }
            Plugin.Log.LogInfo(defs.Count > 0 ? "[改招] 已读取：" + string.Join("，", defs.Keys) : "[改招] Mod\\SkillMod 和内容包里都没有改招配置");
        }

        static string NameOf(string path)
        {
            if (path == null || defs.Count == 0 || Enabled == null || !Enabled.Value) return null;
            string p = path.Replace('\\', '/');
            if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
            string name = p.Substring(prefix.Length, p.Length - prefix.Length - suffix.Length);
            return defs.ContainsKey(name) && !building.Contains(name) ? name : null;
        }

        internal static bool Has(string path) => NameOf(path) != null;

        internal static bool TryGet(string path, Type want, out UObject result)
        {
            result = null;
            if (!want.IsAssignableFrom(typeof(TimelineAsset))) return false;
            string name = NameOf(path);
            if (name == null) return false;
            if (cache.TryGetValue(name, out var tl) && tl != null) { result = tl; return true; }
            building.Add(name);   // 底子和文件同名时，加载底子要绕过自己
            try
            {
                tl = Build(name, defs[name]);
                if (tl != null) { cache[name] = tl; result = tl; }
            }
            catch (Exception e) { Plugin.Log.LogError($"[改招] {name} 生成失败：{e}"); }
            finally { building.Remove(name); }
            return result != null;
        }

        static TimelineAsset Load(string name)
        {
            bool mine = building.Add(name);
            try { return Game.Resource.Load<TimelineAsset>(string.Format(GameConfig.SkillTimelinePath, name)); }
            finally { if (mine) building.Remove(name); }
        }

        static void Keep(UObject o) { if (o != null) o.hideFlags |= HideFlags.DontUnloadUnusedAsset; }

        static TrackAsset CloneInto(TrackAsset src, TrackAsset parent)
        {
            var t = UObject.Instantiate(src);
            t.name = src.name; Keep(t);
            foreach (var c in t.GetClips())
                if (c.asset != null) { var a = UObject.Instantiate(c.asset); a.name = c.asset.name; Keep(a); c.asset = a; }
            var kids = t.GetChildTracks().ToList();
            if (kids.Count > 0) mClearChildren.Invoke(t, null);
            mAddChild.Invoke(parent, new object[] { t });
            foreach (var k in kids) CloneInto(k, t);
            return t;
        }

        static string FxName(TimelineClip c)
        {
            if (c.asset is CharacterControlPlayableAsset cc && cc.prefab != null) return cc.prefab.name;
            return c.displayName;
        }

        static bool Match(TimelineClip c, string fx) =>
            string.Equals(FxName(c), fx, StringComparison.OrdinalIgnoreCase) || string.Equals(c.displayName, fx, StringComparison.OrdinalIgnoreCase);

        // 攻击方组里的特效轨（跳过 [0] 动作、[1] 镜头、音频轨）
        static List<TrackAsset> FxTracks(GroupTrack g)
        {
            var kids = g.GetChildTracks().ToList();
            int skip = kids.Count > 1 && kids[1] is AnimationTrack ? 2 : 1;
            return kids.Skip(skip).Where(t => !(t is AudioTrack) && !(t is ActivationTrack)).ToList();
        }

        static (TrackAsset track, TimelineClip clip) Find(IEnumerable<TrackAsset> tracks, string fx, int index)
        {
            int n = 0;
            foreach (var t in tracks)
                foreach (var c in t.GetClips().OrderBy(x => x.start))
                    if (Match(c, fx) && n++ == index) return (t, c);
            return (null, null);
        }

        static GroupTrack Attacker(TimelineAsset tl) =>
            tl.GetRootTracks().OfType<GroupTrack>().FirstOrDefault(g => g.name == "Attacker" || g.name == "Attacker(Clone)");

        static TimelineAsset Build(string name, SkillModDef d)
        {
            string baseName = string.IsNullOrEmpty(d.@base) ? name : d.@base;
            var baseTl = Load(baseName);
            if (baseTl == null) { Plugin.Log.LogError("[改招] 找不到底子演出 " + baseName); return null; }

            var tl = ScriptableObject.CreateInstance<TimelineAsset>();
            tl.name = name; Keep(tl);
            GroupTrack atk = null;
            foreach (var g in baseTl.GetRootTracks().OfType<GroupTrack>())
            {
                var ng = tl.CreateTrack<GroupTrack>(null, g.name); Keep(ng);
                ng.muted = g.muted;
                foreach (var k in g.GetChildTracks()) CloneInto(k, ng);
                if (g.name == "Attacker") atk = ng;
            }
            CopyMarkers(baseTl, tl);
            if (atk == null) { Plugin.Log.LogError("[改招] 底子演出没有 Attacker 组：" + baseName); return null; }

            int removed = 0, edited = 0, added = 0;
            foreach (var fx in d.remove ?? new List<string>())
                foreach (var t in FxTracks(atk))
                    foreach (var c in t.GetClips().Where(c => Match(c, fx)).ToList())
                    { mRemoveClip.Invoke(t, new object[] { c }); removed++; }

            foreach (var e in d.edit ?? new List<FxEdit>())
            {
                var (t, c) = Find(FxTracks(atk), e.fx, e.index);
                if (c == null) { Plugin.Log.LogWarning($"[改招] {name}：底子里没有特效 {e.fx}（第 {e.index} 个）"); continue; }
                Apply(c, e); edited++;
            }

            foreach (var e in d.add ?? new List<FxEdit>())
            {
                if (string.IsNullOrEmpty(e.from)) { Plugin.Log.LogWarning($"[改招] {name}：add 里的 {e.fx} 没写 from"); continue; }
                var src = Load(e.from);
                var srcAtk = src != null ? Attacker(src) : null;
                if (srcAtk == null) { Plugin.Log.LogWarning($"[改招] {name}：找不到演出 {e.from}"); continue; }
                var (t, c) = Find(FxTracks(srcAtk), e.fx, e.index);
                if (c == null) { Plugin.Log.LogWarning($"[改招] {name}：{e.from} 里没有特效 {e.fx}（第 {e.index} 个）"); continue; }
                int pos = t.GetClips().OrderBy(x => x.start).ToList().IndexOf(c);
                var nt = CloneInto(t, atk);
                nt.name = "借 " + e.fx;
                var clips = nt.GetClips().OrderBy(x => x.start).ToList();
                for (int i = 0; i < clips.Count; i++)
                    if (i != pos) mRemoveClip.Invoke(nt, new object[] { clips[i] });
                Apply(clips[pos], e); added++;
            }

            Plugin.Log.LogInfo($"[改招] 生成 {name}（底子 {baseName}）：借来 {added} 个特效，调整 {edited} 个，删除 {removed} 个");
            return tl;
        }

        static void Apply(TimelineClip c, FxEdit e)
        {
            if (e.speed.HasValue && e.speed.Value > 0)
            {
                double len = c.duration * c.timeScale;      // 片段原始内容长度
                c.timeScale = e.speed.Value;
                c.duration = len / e.speed.Value;
            }
            if (e.time.HasValue) c.start = Math.Max(0, e.time.Value);
            if (c.asset is CharacterControlPlayableAsset cc)
            {
                if (e.scale.HasValue) cc.scale *= e.scale.Value;
                if (e.move != null) cc.position += V(e.move);
                if (e.rotate != null) cc.rotation = cc.rotation * Quaternion.Euler(V(e.rotate));
                // 在资源名里记下要在运行时处理的内容（缩放模式、颜色），游戏复制资源时名字会跟着走
                var parts = new List<string>();
                if (e.scale.HasValue) parts.Add("s");
                if (e.hue.HasValue) parts.Add("h=" + e.hue.Value.ToString(CultureInfo.InvariantCulture));
                if (e.color != null || e.brightness.HasValue)
                {
                    float b = e.brightness ?? 1f;
                    float[] col = e.color ?? new float[] { 1, 1, 1 };
                    float r = col.Length > 0 ? col[0] : 1, g = col.Length > 1 ? col[1] : 1, bl = col.Length > 2 ? col[2] : 1, a = col.Length > 3 ? col[3] : 1;
                    parts.Add("c=" + string.Join(",", new[] { r * b, g * b, bl * b, a }.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))));
                }
                if (e.props != null && e.props.Count > 0)
                    parts.Add("p=" + string.Join("|", e.props.Where(kv => kv.Value != null && kv.Value.Length > 0)
                        .Select(kv => kv.Key.Trim() + ":" + string.Join(",", kv.Value.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))))));
                if (parts.Count > 0) cc.name = Tag + "[" + string.Join(";", parts) + "] " + cc.name;
            }
        }

        static Vector3 V(float[] v) => new Vector3(v.Length > 0 ? v[0] : 0, v.Length > 1 ? v[1] : 0, v.Length > 2 ? v[2] : 0);

        static void CopyMarkers(TimelineAsset from, TimelineAsset to)
        {
            var markers = from.markerTrack?.GetMarkers().ToList();
            if (markers == null || markers.Count == 0 || fMarkers == null) return;
            to.CreateMarkerTrack();
            var list = fMarkers.GetValue(to.markerTrack);
            var add = list.GetType().GetMethod("Add");
            foreach (var m in markers) add.Invoke(list, new object[] { m });
        }

        // 运行时处理：粒子跟随整体缩放、换颜色、改材质属性。特效刚实例化（还没显示）时执行
        [HarmonyPatch(typeof(CharacterControlPlayableBehaviour), "Create")]
        static class Patch_FxRuntime
        {
            static void Postfix(Playable __result, CharacterControlPlayableAsset asset)
            {
                if (asset == null || asset.name == null || !asset.name.StartsWith(Tag) || __result.IsNull()) return;
                var m = Regex.Match(asset.name, @"^" + Tag + @"\[([^\]]*)\]");
                if (!m.Success) return;
                var sp = (ScriptPlayable<CharacterControlPlayableBehaviour>)__result;
                var go = Traverse.Create(sp.GetBehaviour()).Field("instance").GetValue<GameObject>();
                if (go == null) return;
                try { Process(go, FxLook.Parse(m.Groups[1].Value)); }
                catch (Exception e) { Plugin.Log.LogWarning("[改招] 特效处理出错：" + e); }
            }
        }

        static readonly string[] ColorProps = { "_TintColor", "_Color", "_MainColor", "_BaseColor", "_EmissionColor", "_RimColor" };
        static readonly string[] ReportFloats = { "_Metallic", "_Glossiness", "_Smoothness", "_RimPower", "_rimpower", "_Cutoff", "_Alpha", "_Intensity" };
        static readonly HashSet<string> reported = new HashSet<string>();

        static string Fmt(Color c) => $"({c.r:0.##},{c.g:0.##},{c.b:0.##},{c.a:0.##})";

        static void Process(GameObject go, FxLook look)
        {
            var report = new System.Text.StringBuilder();
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                if (look.scale) main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                if (!look.recolor) continue;
                main.startColor = look.Fix(main.startColor);
                var col = ps.colorOverLifetime;
                if (col.enabled) col.color = look.Fix(col.color);
                report.AppendLine($"    粒子 {ps.name}");
            }
            if (!look.recolor && look.props.Count == 0) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.materials;      // 给这个特效实例单独复制材质，不影响别的招式
                foreach (var mat in mats)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var p in ColorProps.Concat(look.props.Keys).Distinct())
                        if (mat.HasProperty(p) && IsColorLike(mat, p)) sb.Append($" {p}={Fmt(mat.GetColor(p))}");
                    foreach (var p in ReportFloats)
                        if (mat.HasProperty(p)) sb.Append($" {p}={mat.GetFloat(p):0.##}");
                    if (mat.mainTexture != null) sb.Append($" 贴图={mat.mainTexture.name}");
                    report.AppendLine($"    {r.name}  shader={mat.shader.name} 原值:{sb}");
                    look.ApplyMaterial(mat);
                }
                if (look.recolor)
                {
                    if (r is TrailRenderer tr) tr.colorGradient = look.Fix(tr.colorGradient);
                    else if (r is LineRenderer lr) lr.colorGradient = look.Fix(lr.colorGradient);
                }
            }
            // 特效自己的动画（Animator / 曲线脚本）会每帧重写颜色，挂个组件在动画之后再处理一次
            go.AddComponent<FxTint>().Setup(look);
            if (reported.Add(go.name))
                Plugin.Log.LogInfo($"[改招] 调色 {go.name}：\n{report}");
        }

        // 颜色属性才用 GetColor 读，数值属性（如 _Metallic）用 props 指定一个数
        static bool IsColorLike(Material mat, string p)
        {
            int i = mat.shader.FindPropertyIndex(p);
            if (i < 0) return false;
            var t = mat.shader.GetPropertyType(i);
            return t == UnityEngine.Rendering.ShaderPropertyType.Color || t == UnityEngine.Rendering.ShaderPropertyType.Vector;
        }

        internal class FxLook
        {
            public bool scale, tint;
            public float hue;
            public Color mul = Color.white;
            public Dictionary<string, float[]> props = new Dictionary<string, float[]>();
            public bool recolor => tint || Math.Abs(hue) > 0.01f;

            static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

            public static FxLook Parse(string tag)
            {
                var l = new FxLook();
                foreach (var p in tag.Split(';'))
                {
                    if (p == "s") l.scale = true;
                    else if (p.StartsWith("h=")) l.hue = F(p.Substring(2));
                    else if (p.StartsWith("c="))
                    {
                        var v = p.Substring(2).Split(',').Select(F).ToArray();
                        if (v.Length >= 4) { l.mul = new Color(v[0], v[1], v[2], v[3]); l.tint = true; }
                    }
                    else if (p.StartsWith("p="))
                        foreach (var kv in p.Substring(2).Split('|'))
                        {
                            int i = kv.IndexOf(':');
                            if (i > 0) l.props[kv.Substring(0, i)] = kv.Substring(i + 1).Split(',').Select(F).ToArray();
                        }
                }
                return l;
            }

            public Color Fix(Color c)
            {
                if (Math.Abs(hue) > 0.01f)
                {
                    Color.RGBToHSV(c, out float h, out float sat, out float val);
                    float a = c.a;
                    c = Color.HSVToRGB(Mathf.Repeat(h + hue / 360f, 1f), sat, val, true); c.a = a;
                }
                return tint ? c * mul : c;
            }

            public Gradient Fix(Gradient g)
            {
                if (g == null) return null;
                var n = new Gradient { mode = g.mode };
                n.SetKeys(g.colorKeys.Select(k => new GradientColorKey(Fix(k.color), k.time)).ToArray(),
                          g.alphaKeys.Select(k => new GradientAlphaKey(Mathf.Clamp01(k.alpha * (tint ? mul.a : 1f)), k.time)).ToArray());
                return n;
            }

            public ParticleSystem.MinMaxGradient Fix(ParticleSystem.MinMaxGradient g)
            {
                switch (g.mode)
                {
                    case ParticleSystemGradientMode.Color: return new ParticleSystem.MinMaxGradient(Fix(g.color));
                    case ParticleSystemGradientMode.TwoColors: return new ParticleSystem.MinMaxGradient(Fix(g.colorMin), Fix(g.colorMax));
                    case ParticleSystemGradientMode.Gradient: return new ParticleSystem.MinMaxGradient(Fix(g.gradient));
                    case ParticleSystemGradientMode.TwoGradients: return new ParticleSystem.MinMaxGradient(Fix(g.gradientMin), Fix(g.gradientMax));
                    case ParticleSystemGradientMode.RandomColor:
                        var r = new ParticleSystem.MinMaxGradient(Fix(g.gradient)); r.mode = ParticleSystemGradientMode.RandomColor; return r;
                }
                return g;
            }

            // 某个颜色属性的新值：先按 props 直接指定（只给 3 个数时保留原透明度，淡入淡出不受影响），再乘颜色/转色相
            public Color Adjust(string prop, Color cur)
            {
                if (props.TryGetValue(prop, out var v) && v.Length >= 3)
                    cur = new Color(v[0], v[1], v[2], v.Length >= 4 ? v[3] : cur.a);
                return ColorProps.Contains(prop) ? Fix(cur) : cur;
            }

            public IEnumerable<string> ColorKeys => ColorProps.Concat(props.Where(kv => kv.Value.Length >= 3).Select(kv => kv.Key)).Distinct();

            public void ApplyMaterial(Material mat)
            {
                foreach (var kv in props)
                    if (kv.Value.Length == 1 && mat.HasProperty(kv.Key)) mat.SetFloat(kv.Key, kv.Value[0]);
                foreach (var p in ColorKeys)
                    if (mat.HasProperty(p) && IsColorLike(mat, p) && (recolor || props.ContainsKey(p)))
                        mat.SetColor(p, Adjust(p, mat.GetColor(p)));
            }
        }
    }

    // 每帧在动画之后检查：颜色属性被特效自己的动画改回去了（值和上次写入的不同），就再处理一次
    public class FxTint : MonoBehaviour
    {
        SkillMod.FxLook look;
        string[] keys;
        Renderer[] rs;
        MaterialPropertyBlock mpb;
        readonly Dictionary<string, Color> last = new Dictionary<string, Color>();

        internal void Setup(SkillMod.FxLook l)
        {
            look = l;
            keys = l.ColorKeys.Where(p => l.recolor || l.props.ContainsKey(p)).ToArray();
            rs = GetComponentsInChildren<Renderer>(true);
            mpb = new MaterialPropertyBlock();
            for (int i = 0; i < rs.Length; i++)
            {
                var mats = rs[i].sharedMaterials;
                for (int j = 0; j < mats.Length; j++)
                    if (mats[j] != null)
                        foreach (var p in keys)
                            if (mats[j].HasProperty(p)) last[i + "/" + j + p] = mats[j].GetColor(p);
            }
        }

        void LateUpdate()
        {
            if (rs == null || keys.Length == 0) return;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null || !r.enabled) continue;
                var mats = r.sharedMaterials;
                for (int j = 0; j < mats.Length; j++)
                {
                    var mat = mats[j];
                    if (mat == null) continue;
                    foreach (var p in keys)
                    {
                        if (!mat.HasProperty(p)) continue;
                        string k = i + "/" + j + p;
                        var c = mat.GetColor(p);
                        if (last.TryGetValue(k, out var lc) && lc == c) continue;
                        c = look.Adjust(p, c);
                        mat.SetColor(p, c); last[k] = c;
                    }
                }
                // Animator 动画材质颜色走的是 PropertyBlock，每帧都会重新写入
                r.GetPropertyBlock(mpb);
                if (mpb.isEmpty) continue;
                bool changed = false;
                foreach (var p in keys)
                {
                    Vector4 v = mpb.GetVector(p);
                    if (v == Vector4.zero) continue;
                    string k = i + "/pb" + p;
                    var c = (Color)v;
                    if (last.TryGetValue(k, out var lc) && lc == c) continue;
                    c = look.Adjust(p, c);
                    mpb.SetColor(p, c); last[k] = c; changed = true;
                }
                if (changed) r.SetPropertyBlock(mpb);
            }
        }
    }
}
