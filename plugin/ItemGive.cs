// 测试用：按热键把道具直接放进背包（Game.GameData.Warehouse），不走剧情的奖励界面。
//
// 为什么不用 RewardPack：RewardPack 是剧情流程节点（OpenUIAsyncAction<UIReward>），
// 它先打开 UIReward（Show 时 Game.Input.Push 锁住输入），然后 OpenUI 里第一句就是
// base.Graph.SetVariable(...)。我们单独 new 出来的节点没有 Graph → 空引用异常被 UniTask 吞掉，
// 界面打开了却永远等不到 ShowAllRewards/Close，输入一直被它占着 → 看起来就是“卡死”。
//
// 现在的做法：
//   - 配置里写道具 Id（如 itxk0002，可写 itxk0002*3 表示 3 个）→ 直接 InventoryController.Add；
//   - 写奖励 Id（RewardItem 表，如 itxk0002_get）→ 逐个执行奖励里的节点（和游戏里 RewardProps.GetValue 一样把东西加进背包），
//     跳过需要打开界面的异步节点。
// 完成后用游戏自带的右上角提示（UIMessage，不会锁输入）显示获得了什么。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Heluo;
using Heluo.Data;
using Heluo.Flow;
using Heluo.UI;
using UnityEngine;

namespace GuLongOpenMod
{
    internal static class ItemGive
    {
        internal static ConfigEntry<string> Ids;
        internal static ConfigEntry<KeyboardShortcut> Hotkey;

        internal static void Bind(ConfigFile cfg)
        {
            // 注意：键名改了（旧的「发放奖励ID」不再使用），避免沿用旧配置。
            Ids = cfg.Bind("武学测试", "发放道具ID", "itxk0002",
                "按热键把这些东西放进背包，多个用逗号分隔。\n" +
                "可以写道具 Id（NormalInventoryItem 等表，如 itxk0002；itxk0002*3 表示 3 个），\n" +
                "也可以写奖励 Id（RewardItem 表，如 itxk0002_get）。");
            Hotkey = cfg.Bind("武学测试", "发放道具热键", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl), "");
        }

        internal static void Tick()
        {
            if (Hotkey == null || !Hotkey.Value.IsDown()) return;
            if (Game.GameData == null || Game.GameData.Warehouse == null)
            {
                Plugin.Log.LogWarning("[发放道具] 还没进入存档，背包不存在");
                return;
            }

            var got = new List<string>();
            foreach (var raw in Ids.Value.Split(',', '，'))
            {
                string entry = raw.Trim();
                if (entry.Length == 0) continue;
                try { GiveOne(entry, got); }
                catch (Exception e) { Plugin.Log.LogError("[发放道具] " + entry + " 失败：" + e); }
            }

            if (got.Count > 0) Toast("获得 " + string.Join("、", got));
        }

        static void GiveOne(string entry, List<string> got)
        {
            string id = entry;
            int amount = 1;
            int star = entry.IndexOf('*');
            if (star > 0)
            {
                id = entry.Substring(0, star).Trim();
                if (!int.TryParse(entry.Substring(star + 1).Trim(), out amount) || amount <= 0) amount = 1;
            }

            // 1) 道具 Id：直接加
            var item = Game.Data.Get<InventoryItem>(id);
            if (item != null)
            {
                bool ok = NewWarehouse().Add(id, amount, true);
                Plugin.Log.LogInfo("[发放道具] 道具 " + id + "（" + item.Name + "）×" + amount + (ok ? " 已放进背包" : " 添加失败（Add 返回 false）"));
                if (ok) got.Add(item.Name + "×" + amount);
                return;
            }

            // 2) 奖励 Id：执行奖励里的每个节点
            var reward = Game.Data.Get<RewardItem>(id);
            if (reward == null)
            {
                Plugin.Log.LogWarning("[发放道具] 道具表和奖励表里都没有 " + id + "（内容包加载了吗？看日志里 [内容包] 开头的行）");
                return;
            }
            if (reward.Rewards == null || reward.Rewards.nodes == null)
            {
                Plugin.Log.LogWarning("[发放道具] 奖励 " + id + " 没有内容（Rewards 列解析失败？）");
                return;
            }

            // nodes 是展平后的列表：LogicalNode 和它里面的子节点都在里面，所以跳过 LogicalNode，只执行叶子节点，避免重复发放。
            foreach (var node in reward.Rewards.nodes)
            {
                if (node == null || node is LogicalNode) continue;
                if (node is RunnableAction)
                {
                    Plugin.Log.LogWarning("[发放道具] 奖励 " + id + " 里的 " + node.GetType().Name + " 需要打开界面，已跳过");
                    continue;
                }
                var output = node as IOutput;
                if (output == null) continue;

                // RewardProps/RewardEquips 等的 GetValue 就是往 Warehouse 里 Add
                output.GetValue();

                var act = node as RewardItemAction;
                var data = act != null ? act.GetData() : null;
                if (data != null)
                {
                    var it = Game.Data.Get<InventoryItem>(data.Id);
                    string name = it != null ? it.Name : data.Id;
                    got.Add(name + "×" + data.Count);
                    Plugin.Log.LogInfo("[发放道具] 奖励 " + id + "：" + data.Id + "（" + name + "）×" + data.Count + " 已放进背包");
                }
                else
                {
                    Plugin.Log.LogInfo("[发放道具] 奖励 " + id + "：已执行 " + node.GetType().Name + " " + node);
                }
            }
        }

        static InventoryController NewWarehouse()
        {
            return new InventoryController(Game.Data, Game.GameData.Warehouse, 0, 0, false);
        }

        static void Toast(string text)
        {
            try { Game.UI.Open<UIMessage>().SetContent(text); }
            catch (Exception e) { Plugin.Log.LogWarning("[发放道具] 提示框打开失败（不影响发放）：" + e.Message); }
        }
    }
}
