// F5：不重启游戏，重新读取所有内容包（表格、图片、改招）。做内容时改完文件按一下就能看效果。
//
// 表格是「原地更新」的：新读到的值复制进游戏里已有的数据对象，而不是换成新对象。
// 因为游戏很多地方（已学会的招式、NPC 的头像、背包里的道具）拿着的是旧对象的引用，换对象它们看不到。
// 新增的行直接加进去；从内容包里删掉的行，重读后会恢复成原版的值（原版没有的行会留到重启游戏）。
//
// 已经打开的界面要重新打开才刷新；分布式表（GameEvent 目录下的）要重启游戏。
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Heluo;
using Heluo.Data;
using Heluo.Data.Source;
using UnityEngine;

namespace GuLongOpenMod
{
    internal static class PackReload
    {
        internal static ConfigEntry<KeyboardShortcut> Hotkey;

        internal static void Bind(ConfigFile cfg)
        {
            Hotkey = cfg.Bind("内容包", "重新加载热键", new KeyboardShortcut(KeyCode.F5),
                "改完内容包里的表格/图片/改招 json 后按一下，不用重启游戏");
        }

        internal static void Tick()
        {
            if (Hotkey == null || !Hotkey.Value.IsDown()) return;
            try { Reload(); }
            catch (Exception e) { Plugin.Log.LogError("[内容包] 重新加载失败：" + e); }
        }

        static readonly FieldInfo fMapping = AccessTools.Field(typeof(DataManager), "mapping");
        static readonly MethodInfo mAfterParse = AccessTools.Method(typeof(DataManager), "OnAfterItemParse");

        static void Reload()
        {
            // 重新扫描前后涉及的表都要重读（从包里删掉的行也要恢复成原版）
            var tables = new HashSet<string>(Packs.TouchedTables(), StringComparer.OrdinalIgnoreCase);
            Packs.Scan();
            foreach (var t in Packs.TouchedTables()) tables.Add(t);

            ModProvider.ClearCache();
            SkillMod.Reload();

            var dm = Game.Data as DataManager;
            var mapping = dm != null ? fMapping?.GetValue(dm) as ConcurrentDictionary<Type, IDictionary> : null;
            if (mapping == null) { Plugin.Log.LogWarning("[内容包] 还没读表（在标题画面之前？），表格要等游戏自己读"); return; }
            string path = Packs.CurrentTablePath();

            int ok = 0;
            foreach (var name in tables)
            {
                if (name.Contains("/"))
                {
                    Plugin.Log.LogWarning($"[内容包] {name} 是分布式表，要重启游戏才会生效");
                    continue;
                }
                if (!TableTypes().TryGetValue(name, out var type))
                {
                    Plugin.Log.LogWarning($"[内容包] 找不到表 {name} 对应的数据类型（表名拼错了？）");
                    continue;
                }
                try { ReloadInPlace(dm, mapping, type, path); ok++; }
                catch (Exception e) { Plugin.Log.LogError($"[内容包] 重读 {name} 失败：{e}"); }
            }

            // 表之间的引用（NpcItem → AvatarItem 等）重新解析一遍
            try { mAfterParse?.Invoke(dm, new object[] { AllItemTypes() }); }
            catch (Exception e) { Plugin.Log.LogError("[内容包] 重新解析表间引用失败：" + e); }

            Plugin.Log.LogInfo($"[内容包] 重新加载完成：{Packs.Loaded.Count} 个包，重读 {ok} 张表");
            Toast($"内容包已重新加载（{Packs.Loaded.Count} 个包，{ok} 张表）");
            SkillPreview.InvalidateList();
            SkillPreview.Replay();
        }

        static void ReloadInPlace(DataManager dm, ConcurrentDictionary<Type, IDictionary> mapping, Type type, string path)
        {
            mapping.TryGetValue(type, out var oldDict);
            var old = new Dictionary<object, object>();
            if (oldDict != null) foreach (DictionaryEntry e in oldDict) old[e.Key] = e.Value;

            dm.Load(type, path);   // 读出新的一整张表（经过 ModProvider 合并），放进 mapping[type]

            if (!mapping.TryGetValue(type, out var fresh) || fresh == null || old.Count == 0) return;
            var group = type.GetCustomAttributes(typeof(GroupDescendantAttribute), true).FirstOrDefault() as GroupDescendantAttribute;
            IDictionary groupDict = null;
            if (group != null) mapping.TryGetValue(group.GroupType, out groupDict);

            int updated = 0;
            foreach (var key in fresh.Keys.Cast<object>().ToList())
            {
                if (!old.TryGetValue(key, out var keep) || keep == null) continue;
                CopyFields(fresh[key], keep);
                fresh[key] = keep;
                if (groupDict != null && groupDict.Contains(key)) groupDict[key] = keep;
                updated++;
            }
            Plugin.Log.LogInfo($"[内容包] {type.Name}：原地更新 {updated} 行，新增 {fresh.Count - updated} 行");
        }

        static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();

        // 把 src 的所有实例字段（含自动属性的后备字段）复制到 dst；事件/委托字段不动，保留原来的订阅
        static void CopyFields(object src, object dst)
        {
            if (src == null || dst == null || src.GetType() != dst.GetType()) return;
            var t = src.GetType();
            if (!fieldCache.TryGetValue(t, out var fields))
            {
                var list = new List<FieldInfo>();
                for (var c = t; c != null && c != typeof(object); c = c.BaseType)
                    list.AddRange(c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(f => !typeof(Delegate).IsAssignableFrom(f.FieldType)));
                fieldCache[t] = fields = list.ToArray();
            }
            foreach (var f in fields) f.SetValue(dst, f.GetValue(src));
        }

        static Dictionary<string, Type> tableTypes;

        // 表名 → 数据类型（和 DataManager 的规则一致）
        static Dictionary<string, Type> TableTypes()
        {
            if (tableTypes != null) return tableTypes;
            tableTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in AllItemTypes())
            {
                string name = Packs.TableName(t);
                if (name != null && !tableTypes.ContainsKey(name)) tableTypes[name] = t;
            }
            return tableTypes;
        }

        static Type[] allTypes;
        static Type[] AllItemTypes() => allTypes ?? (allTypes = typeof(Item).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(Item))).ToArray());

        static void Toast(string text)
        {
            try { Game.UI.Open<Heluo.UI.UIMessage>().SetContent(text); } catch { }
        }
    }
}
