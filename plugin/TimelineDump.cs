// 调试用：把招式演出（Character/Timeline/xxx.playable）的结构导出成文本，
// 用来分析古龙的招式 Timeline 由哪些分组/轨道/片段组成，为移植前传招式演出做准备。
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using Heluo;
using UnityEngine;
using UnityEngine.Timeline;

namespace GuLongOpenMod
{
    internal static class TimelineDump
    {
        internal static ConfigEntry<string> Names;
        internal static ConfigEntry<KeyboardShortcut> Hotkey;

        internal static void Bind(ConfigFile cfg)
        {
            Names = cfg.Bind("武学测试", "导出演出名", "gl_skill_empty_103",
                "按热键把这些招式演出的结构导出到 BepInEx\\timeline_dump\\，多个用逗号分隔（SkillItem 表的 AnimationName 列）");
            Hotkey = cfg.Bind("武学测试", "导出演出热键", new KeyboardShortcut(KeyCode.F12), "");
        }

        internal static void Tick()
        {
            if (Hotkey == null || !Hotkey.Value.IsDown()) return;
            string dir = Path.Combine(Paths.BepInExRootPath, "timeline_dump");
            Directory.CreateDirectory(dir);
            foreach (var raw in Names.Value.Split(','))
            {
                string name = raw.Trim();
                if (name.Length == 0) continue;
                try
                {
                    var tl = Game.Resource.Load<TimelineAsset>(string.Format(GameConfig.SkillTimelinePath, name));
                    if (tl == null) { Plugin.Log.LogWarning("[导出演出] 找不到 " + name); continue; }
                    var sb = new StringBuilder();
                    sb.AppendLine($"Timeline {name}  duration={tl.duration:F3}  fps={tl.editorSettings.fps}");
                    if (tl.markerTrack != null)
                        foreach (var m in tl.markerTrack.GetMarkers())
                            sb.AppendLine($"  [marker] {m.GetType().Name} t={m.time:F3}");
                    foreach (var t in tl.GetRootTracks()) DumpTrack(sb, t, 1);
                    string file = Path.Combine(dir, name + ".txt");
                    File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                    Plugin.Log.LogInfo("[导出演出] 已导出 " + file);
                }
                catch (Exception ex) { Plugin.Log.LogError("[导出演出] " + name + " 出错: " + ex); }
            }
        }

        static void DumpTrack(StringBuilder sb, TrackAsset t, int depth)
        {
            string pad = new string(' ', depth * 2);
            sb.AppendLine($"{pad}TRACK {t.GetType().FullName} \"{t.name}\" muted={t.muted} start={t.start:F3} end={t.end:F3}");
            DumpFields(sb, t, pad + "    ", typeof(TrackAsset));
            foreach (var c in t.GetClips())
            {
                sb.AppendLine($"{pad}  CLIP \"{c.displayName}\" start={c.start:F3} dur={c.duration:F3} clipIn={c.clipIn:F3} speed={c.timeScale:F2} asset={c.asset?.GetType().FullName}");
                if (c.asset is AnimationPlayableAsset ap && ap.clip != null)
                    sb.AppendLine($"{pad}      anim clip=\"{ap.clip.name}\" len={ap.clip.length:F3} fps={ap.clip.frameRate} pos={ap.position} rot={ap.rotation.eulerAngles}");
                else if (c.asset != null)
                    DumpFields(sb, c.asset, pad + "      ", typeof(ScriptableObject));
            }
            foreach (var m in t.GetMarkers())
                sb.AppendLine($"{pad}  MARKER {m.GetType().Name} t={m.time:F3}");
            foreach (var child in t.GetChildTracks()) DumpTrack(sb, child, depth + 1);
        }

        // 用反射列出自定义 PlayableAsset / Track 的字段（只到给定基类为止）
        static void DumpFields(StringBuilder sb, object obj, string pad, Type stopAt)
        {
            for (var type = obj.GetType(); type != null && type != stopAt && type != typeof(object); type = type.BaseType)
            {
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.Name.Contains("k__BackingField") && f.FieldType.IsGenericType) continue;
                    object v;
                    try { v = f.GetValue(obj); } catch { continue; }
                    sb.AppendLine($"{pad}{type.Name}.{f.Name} : {f.FieldType.Name} = {Format(v)}");
                }
            }
        }

        static string Format(object v)
        {
            if (v == null) return "null";
            if (v is UnityEngine.Object uo) return uo == null ? "null(destroyed)" : $"<{uo.GetType().Name} \"{uo.name}\">";
            if (v is string s) return "\"" + s + "\"";
            if (v is IEnumerable e && !(v is string))
            {
                var sb = new StringBuilder("[");
                int n = 0;
                foreach (var x in e) { if (n++ > 0) sb.Append(", "); if (n > 20) { sb.Append("..."); break; } sb.Append(Format(x)); }
                return sb.Append("]").ToString();
            }
            return v.ToString();
        }
    }
}
