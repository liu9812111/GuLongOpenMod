using System;
using System.Collections.Generic;
using HarmonyLib;
using Heluo.Data;
using Heluo.Resource;
using UniRx.Async;

namespace GuLongOpenMod
{
    // 重要：ResourceManager 的 Load<T>/LoadAsync<T> 是泛型方法，
    // Mono 下 Harmony 补丁会把共享代码里的 T 固定死，导致所有资源加载类型错乱。
    // 所以这里只补丁非泛型方法，用来把 ModProvider 插到 Provider 链的最前面。

    [HarmonyPatch(typeof(ResourceManager), "Reset")]
    static class Patch_Reset
    {
        static void Postfix(ResourceManager __instance) => ModProvider.Inject(__instance);
    }

    // 兜底：如果插件加载时 ResourceManager 已经创建过了，第一次读表时补插
    [HarmonyPatch(typeof(ResourceManager), "LoadBytes")]
    static class Patch_LoadBytes
    {
        static void Prefix(ResourceManager __instance) => ModProvider.Inject(__instance);
    }

    // 游戏有一部分表是异步读的（LoadingData 里的 LoadAsync），同样兜底
    [HarmonyPatch(typeof(ResourceManager), "LoadBytesAsync")]
    static class Patch_LoadBytesAsync
    {
        static void Prefix(ResourceManager __instance) => ModProvider.Inject(__instance);
    }

    // 游戏启动时大部分表由 AsyncDataManager 异步读取，那条路不一定经过资源链（源码里看不到实现）。
    // 被内容包改动的表改成走同步的 DataManager.Load：它用 resource.LoadBytes 读表，一定会经过 ModProvider 合并。
    // 没被改动的表照旧异步读，不影响加载速度。
    [HarmonyPatch(typeof(AsyncDataManager), "LoadAsync", new[] { typeof(Type), typeof(string) })]
    static class Patch_AsyncLoadTable
    {
        static bool Prefix(AsyncDataManager __instance, Type itemType, string path, ref UniTask __result)
        {
            if (!Packs.TouchesTable(itemType)) return true;
            Plugin.Log.LogInfo($"[内容包] {itemType.Name} 改为同步读取（以便合并内容包）");
            __instance.Load(itemType, path);
            __result = default(UniTask);   // 已完成的任务
            return false;
        }
    }

    // 兜底：所有表读完、游戏解析表之间的引用（比如 NpcItem → AvatarItem）之前，
    // 检查被内容包改动的表是不是都经过了合并；没有的话当场同步重读一遍。
    [HarmonyPatch(typeof(DataManager), "OnAfterItemParse")]
    static class Patch_OnAfterItemParse
    {
        static void Prefix(DataManager __instance, IEnumerable<Type> types)
        {
            if (types == null) return;
            foreach (var t in types)
            {
                if (!Packs.TouchesTable(t)) continue;
                string name = Packs.TableName(t);
                bool merged;
                lock (Packs.Merged) merged = Packs.Merged.Contains(name);
                if (merged) continue;
                Plugin.Log.LogWarning($"[内容包] {name} 读取时没有经过合并，现在重新读取");
                try { __instance.Load(t, Packs.CurrentTablePath()); }
                catch (Exception e) { Plugin.Log.LogError($"[内容包] 重读 {name} 失败：{e.Message}"); }
            }
        }
    }
}
