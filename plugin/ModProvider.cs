using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Heluo.Resource;
using UniRx.Async;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace GuLongOpenMod
{
    // 插在游戏资源链最前面的 Provider。不认识的路径一律返回 null，交给游戏原来的 Provider。
    //   - 表格：原表（含游戏自带 Mod\Config 的整表替换）+ 各内容包的增量行
    //   - 内容包 files\ 下的文件：按资源路径直接提供（json/txt 等字节、png 图片）
    //   - BGM 替换、改招演出
    public class ModProvider : ObjectResourceProvider
    {
        static readonly AccessTools.FieldRef<ResourceManager, IChainedResourceProvider> headRef =
            AccessTools.FieldRefAccess<ResourceManager, IChainedResourceProvider>("provider");

        static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        public static void Inject(ResourceManager rm)
        {
            var head = headRef(rm);
            if (head == null || head is ModProvider) return;
            var mine = new ModProvider { Successor = head };
            headRef(rm) = mine;
            Plugin.Log.LogInfo("ModProvider 已插入资源链，原链头: " + head.GetType().Name);
        }

        /// <summary>F5 重新加载时清掉图片缓存。</summary>
        internal static void ClearCache()
        {
            textures.Clear();
            sprites.Clear();
        }

        public override T Load<T>(string path)
        {
            // BGM 替换
            if (typeof(T) == typeof(AudioClip) && BgmReplace.IsBgm(path))
                return BgmReplace.TryGet(path) as T;

            // 改招：修改过的招式演出
            if (SkillMod.TryGet(path, typeof(T), out var tl))
                return tl as T;

            // 内容包里的图片（图标、立绘…）
            if ((typeof(T) == typeof(Sprite) || typeof(T) == typeof(Texture2D)) && Packs.TryGetFile(path, out var file))
            {
                try { return (typeof(T) == typeof(Sprite) ? (UObject)GetSprite(path, file) : GetTexture(path, file)) as T; }
                catch (Exception e) { Plugin.Log.LogError($"[内容包] 图片读取失败 {file}：{e.Message}"); }
            }
            return null;
        }

        // 和游戏自带的 ExternalResourceProvider 用同样的方式生成，保证显示效果一致
        static Texture2D GetTexture(string key, string file)
        {
            if (textures.TryGetValue(key, out var t) && t != null) return t;
            t = new Texture2D(2, 2);
            ImageConversion.LoadImage(t, File.ReadAllBytes(file));
            t.name = Path.GetFileNameWithoutExtension(file);
            textures[key] = t;
            return t;
        }

        static Sprite GetSprite(string key, string file)
        {
            if (sprites.TryGetValue(key, out var s) && s != null) return s;
            var t = GetTexture(key, file);
            s = Sprite.Create(t, new Rect(0f, 0f, t.width, t.height), new Vector2(t.width / 2, t.height / 2));
            s.name = t.name;
            sprites[key] = s;
            return s;
        }

        public override UniTask<T> LoadAsync<T>(string path)
        {
            // 同步返回，避免基类 await 0 让所有异步加载都多等一帧
            return new UniTask<T>(Load<T>(path));
        }

        public override byte[] LoadBytes(string path)
        {
            try
            {
                // 表格：先拿到后面链上的原表，再合并增量行
                if (Packs.TryGetTableDeltas(path, out var deltas))
                {
                    byte[] baseBytes = LoadBytesFromChain(path);
                    if (baseBytes == null || baseBytes.Length == 0)
                    {
                        Plugin.Log.LogWarning($"[内容包] 游戏里没有表 {path}，内容包里的增量行无处合并（新表请放 files\\ 下）");
                        return null;
                    }
                    return TableMerge.Merge(path, baseBytes, deltas);
                }
                // 内容包 files\ 下的整文件（新剧情 json、战斗配置等）
                if (Packs.TryGetFile(path, out var file))
                    return File.ReadAllBytes(file);
            }
            catch (Exception e) { Plugin.Log.LogError($"[内容包] 读取 {path} 出错：{e}"); }
            return null;
        }

        public override UniTask<byte[]> LoadBytesAsync(string path) => new UniTask<byte[]>(LoadBytes(path));

        // 跳过自己，按 ResourceManager.LoadBytes 的规则依次问后面的 Provider
        byte[] LoadBytesFromChain(string path)
        {
            for (var p = Successor; p != null; p = p.Successor)
            {
                var r = p.LoadBytes(path);
                if (r != null && r.Length > 0) return r;
            }
            return null;
        }

        public override bool CheckHasResource(string path)
        {
            if (BgmReplace.IsBgm(path) && BgmReplace.Has(path)) return true;
            if (SkillMod.Has(path)) return true;
            if (Packs.TryGetFile(path, out _, false)) return true;
            return false;
        }
    }
}
