// BGM 替换：游戏通过 ResourceManager.Load<AudioClip>("Resources_Audio/BGM/<名字>") 加载背景音乐，
// ModProvider 在链头拦截，Mod 文件夹里有同名音频就用我们的。
// 音频要异步读取（UnityWebRequest），所以启动时先把 Mod\Resources_Audio\BGM 里的文件全部预加载好。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace GuLongOpenMod
{
    internal static class BgmReplace
    {
        internal const string BgmPrefix = "Resources_Audio/BGM/";
        static readonly string[] Exts = { ".ogg", ".wav", ".mp3" };

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> LogBgm;
        internal static ConfigEntry<string> Mapping;
        internal static ConfigEntry<bool> Rotate;
        internal static ConfigEntry<float> FadeSeconds;

        static string folder;

        // Mod\Resources_Audio\BGM，加上各内容包的 files\Resources_Audio\BGM
        static IEnumerable<string> Folders()
        {
            if (Directory.Exists(folder)) yield return folder;
            foreach (var d in Packs.SubDirs(Path.Combine("files", "Resources_Audio", "BGM"))) yield return d;
        }
        static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string[]> alias = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<AudioClip, string> clipGroup = new Dictionary<AudioClip, string>();   // 我们给出的曲子属于哪首原曲
        static readonly Dictionary<string, AudioClip> lastPicked = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        static readonly System.Random rng = new System.Random();
        static string lastLogged;
        static string currentGroup;   // 游戏当前请求的 BGM（只轮换它，旧音源/短音效不碰）

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("BGM替换", "启用", true,
                "把同名音频放到 Mod\\Resources_Audio\\BGM\\ 下即可替换原版 BGM，支持 .ogg / .wav / .mp3");
            LogBgm = cfg.Bind("BGM替换", "记录BGM名字", true,
                "每次切换 BGM 都在日志里打印原曲名字，用来确定要替换哪一首");
            Mapping = cfg.Bind("BGM替换", "映射", "",
                "可选。原曲名=文件名(不含扩展名)，多首用 | 隔开随机播放，多条映射用逗号分隔。\n" +
                "例如：Bgm003=战斗A|战斗B|战斗C,Bgm009=小镇。\n" +
                "不写映射时按文件名自动匹配：Bgm003.ogg、Bgm003_1.ogg、Bgm003_2.ogg… 都算 Bgm003 的候选，随机挑一首");
            Rotate = cfg.Bind("BGM替换", "同场景内轮换", true,
                "有多首候选时，一首播完换下一首（随机、不连续重复）；关掉则每次进场景随机挑一首单曲循环");
            FadeSeconds = cfg.Bind("BGM替换", "轮换淡入淡出秒数", 2f,
                new ConfigDescription("同场景轮换时，上一首结尾淡出、下一首开头淡入的时长。0 = 直接切换", new AcceptableValueRange<float>(0f, 10f)));
            ParseMapping();
            Mapping.SettingChanged += (s, e) => ParseMapping();

            folder = Path.Combine(Paths.GameRootPath, Heluo.GameConfig.ModPath, "Resources_Audio", "BGM");
        }

        static void ParseMapping()
        {
            alias.Clear();
            foreach (var pair in (Mapping.Value ?? "").Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=');
                if (kv.Length == 2 && kv[0].Trim().Length > 0 && kv[1].Trim().Length > 0)
                    alias[kv[0].Trim()] = Array.ConvertAll(kv[1].Split(new[] { '|', '｜' }, StringSplitOptions.RemoveEmptyEntries), x => x.Trim());
            }
        }

        // 启动时预加载整个文件夹
        internal static IEnumerator PreloadAll()
        {
            var dirs = Folders().ToList();
            if (dirs.Count == 0)
            {
                Plugin.Log.LogInfo("没有 BGM 替换文件夹: " + folder);
                yield break;
            }
            // 内容包在后、Mod\Resources_Audio\BGM 在前：同名文件以先加载的为准，所以倒序让后加载的包优先
            foreach (var file in Enumerable.Reverse(dirs).SelectMany(d => Directory.GetFiles(d)))
            {
                if (Array.IndexOf(Exts, Path.GetExtension(file).ToLowerInvariant()) < 0) continue;
                yield return LoadFile(file);
            }
            Plugin.Log.LogInfo($"BGM 替换：已加载 {clips.Count} 首，目录：{string.Join("；", dirs)}");
        }

        static IEnumerator LoadFile(string file)
        {
            string key = Path.GetFileNameWithoutExtension(file);
            if (clips.ContainsKey(key) || !loading.Add(key)) yield break;

            AudioType type;
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".ogg": type = AudioType.OGGVORBIS; break;
                case ".wav": type = AudioType.WAV; break;
                default: type = AudioType.MPEG; break;
            }
            // Uri 会把中文路径（古龙风云录）正确转义
            using (var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, type))
            {
                var dh = (DownloadHandlerAudioClip)req.downloadHandler;
                dh.streamAudio = false;
                dh.compressed = true;     // 内存里保持压缩，几十首也不会占太多内存
                yield return req.SendWebRequest();
                loading.Remove(key);
                if (req.isNetworkError || req.isHttpError)
                {
                    Plugin.Log.LogError($"BGM 加载失败 {Path.GetFileName(file)}: {req.error}");
                    yield break;
                }
                AudioClip clip = dh.audioClip;
                if (clip == null || clip.length <= 0f)
                {
                    Plugin.Log.LogError($"BGM 解码失败 {Path.GetFileName(file)}（格式不支持？建议转成 .ogg）");
                    yield break;
                }
                clip.name = key;
                UnityEngine.Object.DontDestroyOnLoad(clip);
                clips[key] = clip;
                Plugin.Log.LogInfo($"BGM 已加载: {Path.GetFileName(file)}  {clip.length:F0} 秒");
            }
        }

        static string NameOf(string path) => Path.GetFileNameWithoutExtension(path.Substring(BgmPrefix.Length));

        // 某首原曲的全部候选：映射里写的，或者同名 + 同名_数字 的文件
        static List<AudioClip> Candidates(string name)
        {
            var list = new List<AudioClip>();
            if (alias.TryGetValue(name, out var files))
            {
                foreach (var f in files)
                    if (clips.TryGetValue(f, out var c) && c != null) list.Add(c);
                return list;
            }
            foreach (var kv in clips)
            {
                if (kv.Value == null) continue;
                string k = kv.Key;
                if (k.Equals(name, StringComparison.OrdinalIgnoreCase)) { list.Add(kv.Value); continue; }
                if (k.Length > name.Length + 1 && k.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(k.Substring(name.Length + 1), out _))
                    list.Add(kv.Value);
            }
            return list;
        }

        // 随机挑一首，尽量不和上一首重复
        static AudioClip Pick(string name, List<AudioClip> list)
        {
            lastPicked.TryGetValue(name, out var last);
            AudioClip c;
            if (list.Count == 1) c = list[0];
            else
            {
                do c = list[rng.Next(list.Count)]; while (c == last);
            }
            lastPicked[name] = c;
            clipGroup[c] = name;
            return c;
        }

        internal static bool IsBgm(string path) =>
            path != null && path.StartsWith(BgmPrefix, StringComparison.OrdinalIgnoreCase);

        // ModProvider 调用：有替换就返回，没有返回 null 让游戏读原版
        internal static AudioClip TryGet(string path)
        {
            string original = path.Substring(BgmPrefix.Length);
            if (LogBgm.Value && original != lastLogged)
            {
                lastLogged = original;
                Plugin.Log.LogInfo("[BGM] " + original);
            }
            if (!Enabled.Value) { currentGroup = null; return null; }

            string name = NameOf(path);
            var list = Candidates(name);
            currentGroup = list.Count > 0 ? name : null;
            if (list.Count > 0)
            {
                AudioClip clip = Pick(name, list);
                if (LogBgm.Value) Plugin.Log.LogInfo($"[BGM] {original} -> {clip.name}（候选 {list.Count} 首）");
                return clip;
            }
            // 游戏运行中新放进去的文件：现在开始加载，下次播放这首时生效
            foreach (var ext in Exts)
            foreach (var dir in Folders())
            {
                string file = Path.Combine(dir, name + ext);
                if (File.Exists(file) && Plugin.Instance != null)
                {
                    Plugin.Log.LogInfo($"[BGM] 发现新文件 {name}{ext}，正在加载，下次播放这首时生效");
                    Plugin.Instance.StartCoroutine(LoadFile(file));
                    return null;
                }
            }
            return null;
        }

        // ── 同场景内轮换：盯着正在播我们曲子的 AudioSource，快播完时换下一首 ──
        static readonly List<AudioSource> watched = new List<AudioSource>();
        static readonly Dictionary<AudioSource, float> lastTime = new Dictionary<AudioSource, float>();
        static float nextScan;

        static readonly HashSet<AudioSource> fading = new HashSet<AudioSource>();

        internal static void Tick()
        {
            if (Enabled == null || !Enabled.Value || !Rotate.Value || clipGroup.Count == 0) return;

            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 1f;
                watched.Clear();
                foreach (var src in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                    if (src.clip != null && clipGroup.ContainsKey(src.clip)) watched.Add(src);
            }

            float fade = FadeSeconds.Value;
            foreach (var src in watched)
            {
                if (src == null || src.clip == null || !src.isPlaying || fading.Contains(src)) continue;
                if (!clipGroup.TryGetValue(src.clip, out var name)) continue;
                if (name != currentGroup) continue;           // 不是当前场景的 BGM（上个场景残留的音源、短音效等）
                float t = src.time;
                lastTime.TryGetValue(src, out float prev);
                lastTime[src] = t;
                bool ending = t >= src.clip.length - Mathf.Max(fade, 0.15f);   // 进入结尾淡出区
                bool wrapped = t + 1f < prev;                                    // 没赶上，已经循环回开头
                if (!ending && !wrapped) continue;
                if (Candidates(name).Count < 2) continue;
                Plugin.Instance.StartCoroutine(SwitchTrack(src, name, fadeOut: ending && fade > 0f));
            }
        }

        // 中途被游戏打断（切场景/换曲）：把音量还原，避免停在淡出的半截导致没声音
        static void Restore(AudioSource src, float baseVol)
        {
            if (src != null && src.volume < baseVol) src.volume = baseVol;
            fading.Remove(src);
        }

        // 淡出当前曲 -> 换随机下一首 -> 淡入。期间如果游戏自己换了曲子（切场景），立刻放手不再碰音量
        static IEnumerator SwitchTrack(AudioSource src, string name, bool fadeOut)
        {
            fading.Add(src);
            float baseVol = src.volume;
            float fade = FadeSeconds.Value;
            if (baseVol < 0.05f) fade = 0f;   // 游戏正在淡入/淡出（音量接近 0），不碰音量，直接换曲
            AudioClip from = src.clip;

            if (fadeOut)
            {
                float dur = Mathf.Max(0.05f, Mathf.Min(fade, from.length - src.time));
                for (float e = 0f; e < dur; e += Time.unscaledDeltaTime)
                {
                    if (src == null || src.clip != from) { Restore(src, baseVol); yield break; }
                    src.volume = baseVol * (1f - e / dur);
                    yield return null;
                }
            }
            if (src == null || src.clip != from) { Restore(src, baseVol); yield break; }
            if (fadeOut && fade <= 0f) src.volume = baseVol;

            var list = Candidates(name);
            AudioClip next = list.Count > 0 ? Pick(name, list) : from;
            src.clip = next;
            src.time = 0f;
            src.volume = fade > 0f ? 0f : baseVol;
            src.Play();
            lastTime[src] = 0f;
            if (LogBgm.Value) Plugin.Log.LogInfo($"[BGM] 轮换 {name} -> {next.name}（音源 {src.name}，音量 {baseVol:F2}）");

            if (fade > 0f)
            {
                for (float e = 0f; e < fade; e += Time.unscaledDeltaTime)
                {
                    if (src == null || src.clip != next) { Restore(src, baseVol); yield break; }
                    src.volume = baseVol * (e / fade);
                    yield return null;
                }
            }
            if (src != null && src.clip == next) src.volume = baseVol;
            fading.Remove(src);
        }

        internal static bool Has(string path) => Enabled.Value && Candidates(NameOf(path)).Count > 0;
    }
}
