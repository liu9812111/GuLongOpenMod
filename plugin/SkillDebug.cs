// 测试用：按热键让主角直接学会指定 ID 的招式（不用做秘籍就能在战斗里试新武学）
// using 列表和 GameTweaks.cs 保持一致（LearnedSkill 所在命名空间就在其中）
using System.Collections.Generic;
using BepInEx.Configuration;
using EcsRx.Collections.Entity;
using EcsRx.Entities;
using EcsRx.Extensions;
using EcsRx.Unity.Extensions;
using Heluo;
using Heluo.Actor;
using Heluo.Battle;
using Heluo.Controller;
using Heluo.Data;
using Heluo.Features;
using Heluo.Flow;
using Heluo.FSM;
using Heluo.FSM.Battle;
using Heluo.FSM.Main;
using Heluo.FSM.Player;
using Heluo.Manager;
using Heluo.Resource;
using Heluo.UI;
using Heluo.UI.Controller;
using Heluo.Utility;
using UnityEngine;

namespace GuLongOpenMod
{
    internal static class SkillDebug
    {
        internal static ConfigEntry<string> SkillId;
        internal static ConfigEntry<KeyboardShortcut> Hotkey;
        internal static ConfigEntry<string> TargetNpc;

        internal static void Bind(ConfigFile cfg)
        {
            SkillId = cfg.Bind("武学测试", "学会招式ID", "xk_s0002", "按下热键后主角学会这个 ID 的招式（SkillItem 表第一列），多个用逗号分隔");
            Hotkey = cfg.Bind("武学测试", "热键", new KeyboardShortcut(KeyCode.F10), "在大地图/场景里按，学会后到武学界面装备到招式栏");
            TargetNpc = cfg.Bind("武学测试", "学习角色ID", "", "谁来学：留空 = 主角（队伍第一个成员）；也可以填队友的 NPC ID，例如 na0013");
        }

        internal static void Tick()
        {
            if (Hotkey == null || !Hotkey.Value.IsDown()) return;
            try
            {
                // 招式记在角色（NPC）实体上，不在队伍实体上
                var party = Game.World?.GetSystem<PartyCreationSystem>()?.Player;
                if (party == null || party.Members.Count == 0) { Plugin.Log.LogWarning("[武学测试] 还没进存档"); return; }
                string npcId = string.IsNullOrEmpty(TargetNpc.Value.Trim()) ? party.Members[0] : TargetNpc.Value.Trim();
                IEntity npc = Game.World.GetSystem<NpcDataSystem>()[npcId];
                if (npc == null || !npc.HasComponent<LearnedSkillComponent>())
                { Plugin.Log.LogWarning($"[武学测试] 找不到角色 {npcId} 的武学数据"); return; }
                var learned = npc.GetComponent<LearnedSkillComponent>();
                Plugin.Log.LogInfo($"[武学测试] 学习角色 {npcId}");
                foreach (var raw in SkillId.Value.Split(','))
                {
                    string id = raw.Trim();
                    if (id.Length == 0) continue;
                    Plugin.Log.LogInfo($"[武学测试] 查找招式 {id} ...");
                    var item = Game.Data.Get<SkillItem>(id);
                    if (item == null) { Plugin.Log.LogWarning($"[武学测试] SkillItem 表里没有 {id}（Mod 表放对位置了吗？）"); continue; }
                    bool has = false;
                    foreach (var s in learned.Skills) if (s.Id == id) { has = true; break; }
                    if (has) { Plugin.Log.LogInfo($"[武学测试] 已经会 {item.Name} ({id})"); continue; }
                    Plugin.Log.LogInfo($"[武学测试] 找到 {item.Name}，加入已学列表 ...");
                    learned.Skills.Add(new LearnedSkill { Id = id });
                    Plugin.Log.LogInfo($"[武学测试] 学会 {item.Name} ({id})，动作 {item.AnimationName}");
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[武学测试] 出错: " + ex); }
        }
    }
}
