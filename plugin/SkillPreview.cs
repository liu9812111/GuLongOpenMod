// 招式动作预览：列出 SkillItem 表里所有招式，选一个就在角色身上播放它的演出（动作 + 特效 + 音效），
// 可以调速度、循环、同时让另一个角色播受击方，用来挑选要拿来改的原版招式。
//   热键 F6 打开/关闭窗口；PageUp / PageDown 上一个 / 下一个。
//   做法：直接拿游戏的招式 Timeline，自己建一个 PlayableDirector 播放，
//         按游戏 BattleTimeline 的规则把 Attacker 组绑到攻击方、Defender 组绑到受击方，不改原资源。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using EcsRx.Unity.Extensions;
using Heluo;
using Heluo.Features;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UObject = UnityEngine.Object;

namespace GuLongOpenMod
{
    internal static class SkillPreview
    {
        internal static ConfigEntry<KeyboardShortcut> ToggleKey, PrevKey, NextKey;

        class Entry { public string Anim; public string Names; public string Ids; }

        static List<Entry> all;
        static List<Entry> filtered = new List<Entry>();
        static string search = "", lastSearch = null;
        static int index = -1;

        static bool show;
        static Rect win = new Rect(20, 20, 480, 680);
        static Vector2 listScroll, infoScroll;

        static List<Animator> targets = new List<Animator>();
        static int atkIdx, defIdx = -1;      // defIdx = -1：不播受击方
        static float speed = 1f;
        static bool loop = true, useCamera;

        static PlayableDirector director;
        static string current;
        static string info = "";

        internal static void Bind(ConfigFile cfg)
        {
            ToggleKey = cfg.Bind("招式预览", "打开窗口热键", new KeyboardShortcut(KeyCode.F6), "打开/关闭招式预览窗口（大地图、场景、战斗里都能用，战斗里看得最准）");
            PrevKey = cfg.Bind("招式预览", "上一个", new KeyboardShortcut(KeyCode.PageUp), "");
            NextKey = cfg.Bind("招式预览", "下一个", new KeyboardShortcut(KeyCode.PageDown), "");
        }

        // ───────── 招式列表：读游戏内存里的 SkillItem 表（原版 + 所有内容包），按动作编号去重 ─────────
        static void LoadList()
        {
            all = new List<Entry>();
            IDictionary<int, Heluo.Data.SkillItem> table = null;
            try { table = Game.Data?.Get<Heluo.Data.SkillItem>(); }
            catch (Exception e) { Plugin.Log.LogWarning("[招式预览] 读取招式表失败：" + e.Message); }
            if (table == null || table.Count == 0)
            {
                all = null;   // 下次打开窗口再试
                Plugin.Log.LogWarning("[招式预览] 招式表还没读好（进入存档后再打开），可以在搜索框直接输入动作编号播放");
                return;
            }

            var map = new Dictionary<string, (List<string> names, List<string> ids)>(StringComparer.OrdinalIgnoreCase);
            foreach (var sk in table.Values.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
            {
                string anim = (sk.AnimationName ?? "").Trim();
                if (anim.Length == 0) continue;
                if (!map.TryGetValue(anim, out var v)) { v = (new List<string>(), new List<string>()); map[anim] = v; }
                string nm = (sk.Name ?? "").Trim();
                if (nm.Length > 0 && !v.names.Contains(nm)) v.names.Add(nm);
                v.ids.Add(sk.Id);
            }
            foreach (var a in map.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                all.Add(new Entry { Anim = a, Names = string.Join("、", map[a].names.Take(4)) + (map[a].names.Count > 4 ? " 等" : ""), Ids = string.Join(",", map[a].ids.Take(6)) });
            Plugin.Log.LogInfo($"[招式预览] 招式表 {table.Count} 条，{all.Count} 个动作编号");
        }

        // F5 重新加载内容包后调用：下次打开（或现在开着就立即）重建列表
        internal static void InvalidateList()
        {
            all = null;
            if (show) { LoadList(); if (all != null) Filter(); }
        }

        static void Filter()
        {
            lastSearch = search;
            string s = search.Trim();
            var src = all ?? new List<Entry>();
            filtered = s.Length == 0 ? src.ToList()
                : src.Where(e => e.Anim.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 || e.Names.Contains(s)
                                 || e.Ids.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            index = -1;
        }

        // ───────── 角色：场景里所有带 Bip001 骨骼的 Animator ─────────
        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var r = FindDeep(t.GetChild(i), name); if (r) return r; }
            return null;
        }

        static void RefreshTargets()
        {
            targets = UObject.FindObjectsOfType<Animator>()
                .Where(a => a.isActiveAndEnabled && FindDeep(a.transform, "Bip001") != null)
                .OrderBy(a => a.name).ToList();
            Animator player = null;
            try
            {
                var go = Game.World?.GetSystem<PartyCreationSystem>()?.PlayerEntity?.GetGameObject();
                if (go != null) player = go.GetComponentInChildren<Animator>();
            }
            catch { }
            atkIdx = player != null && targets.Contains(player) ? targets.IndexOf(player) : 0;
            if (defIdx >= targets.Count) defIdx = -1;
        }

        static Animator At(int i) => i >= 0 && i < targets.Count ? targets[i] : null;
        static string Label(int i) { var a = At(i); return a == null ? "（无）" : a.name; }

        // ───────── 播放 ─────────
        // 改招配置重新读取后，重播当前招式
        internal static void Replay()
        {
            if (show && !string.IsNullOrEmpty(current)) Play(current);
        }

        static void Play(string anim)
        {
            Stop();
            current = anim;
            var atk = At(atkIdx);
            if (atk == null) { RefreshTargets(); atk = At(atkIdx); }
            if (atk == null) { info = "场景里没找到角色（带 Bip001 骨骼的模型），点“刷新角色”再试"; return; }

            var tl = Game.Resource.Load<TimelineAsset>(string.Format(GameConfig.SkillTimelinePath, anim));
            if (tl == null) { info = "找不到演出：" + anim; return; }

            var go = new GameObject("SkillPreviewDirector");
            director = go.AddComponent<PlayableDirector>();
            director.playOnAwake = false;
            director.timeUpdateMode = DirectorUpdateMode.GameTime;
            director.extrapolationMode = loop ? DirectorWrapMode.Loop : DirectorWrapMode.None;
            director.playableAsset = tl;

            var def = At(defIdx);
            foreach (var g in tl.GetRootTracks().OfType<GroupTrack>())
            {
                Animator who = g.name == "Attacker" ? atk : g.name == "Defender" ? def : null;
                BindGroup(g, who, g.name == "Attacker" && useCamera);
            }
            director.RebuildGraph();
            director.Play();
            ApplySpeed();
            info = Describe(anim, tl);
        }

        // 和 BattleTimeline 的绑定规则一致：[0] 动作，[1] 如果是动作轨则是镜头，之后的音频轨第一条=音效、第二条=语音，其余=特效（绑角色）
        static void BindGroup(GroupTrack g, Animator who, bool camera)
        {
            var kids = g.GetChildTracks().ToList();
            if (kids.Count == 0) return;
            GameObject go = who ? who.gameObject : null;
            director.SetGenericBinding(kids[0], who);
            int skip = 1;
            if (kids.Count > 1 && kids[1] is AnimationTrack)
            {
                skip = 2;
                Animator camAnim = null;
                if (camera && Camera.main != null)
                {
                    camAnim = Camera.main.GetComponent<Animator>();
                    if (camAnim == null) camAnim = Camera.main.gameObject.AddComponent<Animator>();
                }
                director.SetGenericBinding(kids[1], camAnim);
            }
            int audio = 0;
            foreach (var t in kids.Skip(skip))
            {
                if (t is AudioTrack)
                {
                    AudioSource src = null;
                    if (go != null)
                    {
                        if (audio == 0) src = go.GetComponent<AudioSource>();
                        else { var v = go.transform.Find("Character_Vocal"); src = v ? v.GetComponent<AudioSource>() : null; }
                    }
                    director.SetGenericBinding(t, src);
                    audio++;
                }
                else director.SetGenericBinding(t, go);
            }
        }

        static void ApplySpeed()
        {
            if (director == null || !director.playableGraph.IsValid() || director.playableGraph.GetRootPlayableCount() == 0) return;
            director.playableGraph.GetRootPlayable(0).SetSpeed(speed);
        }

        static void Stop()
        {
            if (director != null)
            {
                director.Stop();
                UObject.Destroy(director.gameObject);
            }
            director = null;
        }

        static void Step(int d)
        {
            if (filtered.Count == 0) return;
            index = (index + d + filtered.Count) % filtered.Count;
            listScroll.y = Math.Max(0, index * 24 - 120);
            Play(filtered[index].Anim);
        }

        // ───────── 演出内容说明（方便挑片段、对命中时间） ─────────
        static bool IsHit(TimelineClip c)
        {
            string n = c.displayName.ToLower();
            return n.Contains("hurt") || n.Contains("block") || n.Contains("burst");
        }

        static string Describe(string anim, TimelineAsset tl)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{anim}  总长 {tl.duration:F2}s");
            foreach (var g in tl.GetRootTracks().OfType<GroupTrack>())
            {
                var kids = g.GetChildTracks().ToList();
                if (g.name == "Attacker")
                {
                    sb.AppendLine("【攻击方】");
                    foreach (var t in kids)
                        foreach (var c in t.GetClips().OrderBy(x => x.start))
                        {
                            string what = t is AnimationTrack ? "动作" : t is AudioTrack ? "声音" : "特效";
                            string clip = c.asset is AnimationPlayableAsset ap && ap.clip != null ? ap.clip.name
                                        : c.asset is AudioPlayableAsset au && au.clip != null ? au.clip.name : c.displayName;
                            if (c.asset is Heluo.Animation.CharacterControlPlayableAsset cc && cc.prefab != null) clip = cc.prefab.name;
                            sb.AppendLine($"  {what} {clip}  {c.start:F2}~{c.end:F2}s" + (Math.Abs(c.timeScale - 1) > 0.01 ? $" x{c.timeScale:F2}" : ""));
                            if (c.asset is Heluo.Animation.CharacterControlPlayableAsset fx)
                                sb.AppendLine($"      挂点 {(string.IsNullOrEmpty(fx.parentPath) ? "角色" : fx.parentPath)}  位置 {fx.position:F2}  角度 {fx.rotation.eulerAngles:F0}  大小 {fx.scale:F2}");
                        }
                }
                else if (g.name == "Defender" && kids.FirstOrDefault() is AnimationTrack da)
                {
                    var hits = da.GetClips().Where(IsHit).OrderBy(c => c.start).Select(c => c.start.ToString("F2")).ToList();
                    sb.AppendLine($"【受击方】命中 {hits.Count} 次：{string.Join("、", hits)} s");
                }
            }
            return sb.ToString();
        }

        // ───────── 每帧 / 界面 ─────────
        internal static void Tick()
        {
            if (ToggleKey == null) return;
            if (ToggleKey.Value.IsDown())
            {
                show = !show;
                if (show)
                {
                    if (all == null) { LoadList(); if (all != null) Filter(); }
                    RefreshTargets();
                }
                else Stop();
            }
            if (!show) return;
            if (PrevKey.Value.IsDown()) Step(-1);
            if (NextKey.Value.IsDown()) Step(1);
            if (director != null && (director.state != PlayState.Playing || At(atkIdx) == null)) Stop();   // 播完或角色没了
        }

        internal static void OnGUI()
        {
            if (!show) return;
            win = GUILayout.Window(0x51C1, win, Draw, "招式预览（F6 关闭）");
        }

        static void Draw(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("攻击方", GUILayout.Width(50));
            if (GUILayout.Button("<", GUILayout.Width(24)) && targets.Count > 0) atkIdx = (atkIdx - 1 + targets.Count) % targets.Count;
            GUILayout.Label(Label(atkIdx), GUILayout.Width(150));
            if (GUILayout.Button(">", GUILayout.Width(24)) && targets.Count > 0) atkIdx = (atkIdx + 1) % targets.Count;
            if (GUILayout.Button("刷新角色")) RefreshTargets();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("受击方", GUILayout.Width(50));
            if (GUILayout.Button("<", GUILayout.Width(24))) defIdx = defIdx <= -1 ? targets.Count - 1 : defIdx - 1;
            GUILayout.Label(Label(defIdx), GUILayout.Width(150));
            if (GUILayout.Button(">", GUILayout.Width(24))) defIdx = defIdx >= targets.Count - 1 ? -1 : defIdx + 1;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"速度 {speed:F2}", GUILayout.Width(70));
            float ns = GUILayout.HorizontalSlider(speed, 0.1f, 2f, GUILayout.Width(150));
            if (Math.Abs(ns - speed) > 0.001f) { speed = (float)Math.Round(ns, 2); ApplySpeed(); }
            if (GUILayout.Button("1x", GUILayout.Width(30))) { speed = 1f; ApplySpeed(); }
            bool nl = GUILayout.Toggle(loop, "循环");
            if (nl != loop) { loop = nl; if (director) director.extrapolationMode = loop ? DirectorWrapMode.Loop : DirectorWrapMode.None; }
            useCamera = GUILayout.Toggle(useCamera, "镜头");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("搜索", GUILayout.Width(36));
            search = GUILayout.TextField(search, GUILayout.Width(200));
            if (search != lastSearch) Filter();
            if (GUILayout.Button("按编号播放")) Play(search.Trim());
            GUILayout.EndHorizontal();

            listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(330));
            for (int i = 0; i < filtered.Count; i++)
            {
                var e = filtered[i];
                var old = GUI.color;
                if (i == index) GUI.color = Color.yellow;
                if (GUILayout.Button($"{e.Anim}   {e.Names}", GUI.skin.label)) { index = i; Play(e.Anim); }
                GUI.color = old;
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("上一个")) Step(-1);
            if (GUILayout.Button(director ? "停止" : "重播"))
            {
                if (director) Stop();
                else if (index >= 0 && index < filtered.Count) Play(filtered[index].Anim);
            }
            if (GUILayout.Button("下一个")) Step(1);
            if (GUILayout.Button("说明写入日志")) Plugin.Log.LogInfo("[招式预览]\n" + info);
            GUILayout.EndHorizontal();

            if (director) GUILayout.Label($"播放中 {director.time:F2} / {director.duration:F2}s");
            infoScroll = GUILayout.BeginScrollView(infoScroll, GUILayout.Height(170));
            GUILayout.Label(info);
            GUILayout.EndScrollView();

            GUI.DragWindow();
        }
    }
}
