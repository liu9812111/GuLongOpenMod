// 游戏功能增强：学技能不吃书、最大上阵人数、技能栏位、互动点常亮、战斗后回满、高亮停留触发区域
// 实现参考了寻宇的 Gulong_plugin 1.8.6（仅个人使用）
using System.Collections.Generic;
using BepInEx.Configuration;
using EcsRx.Collections.Entity;
using EcsRx.Entities;
using EcsRx.Extensions;
using EcsRx.Unity.Extensions;
using HarmonyLib;
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
using HighlightingSystem;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace GuLongOpenMod
{
    internal static class Tweaks
    {
        internal static ConfigEntry<bool> BookNotConsumed;
        internal static ConfigEntry<int> MaxBattleMembers;
        internal static ConfigEntry<int> ExtraSkillSlots;
        internal static ConfigEntry<bool> RestAfterBattle;
        internal static ConfigEntry<bool> TriggerAlwaysShow;
        internal static ConfigEntry<int> TriggerShowDistance;
        internal static ConfigEntry<bool> ShowStayTrigger;

        // 当前帧在常亮范围内的互动点，用来控制只给最近那个显示热键提示
        internal static readonly List<IEntity> NearbyTargets = new List<IEntity>();

        internal static void Bind(ConfigFile cfg)
        {
            BookNotConsumed = cfg.Bind("游戏设定", "学技能不吃书", false, "学技能 / 心法后书不消失");
            ExtraSkillSlots = cfg.Bind("游戏设定", "增加技能栏位数", 0,
                new ConfigDescription("队伍成员额外增加的技能栏位，每人最多 6 个，多了无效", new AcceptableValueRange<int>(0, 6)));
            MaxBattleMembers = cfg.Bind("战斗设定", "最大上阵人数", -1, "修改最大上阵人数，-1 为不修改");
            RestAfterBattle = cfg.Bind("战斗设定", "战斗后自动回满气血内力", false, "战斗结算时队伍成员气血、内力回满");
            TriggerAlwaysShow = cfg.Bind("UI增强", "互动点常亮", false,
                "大地图采集点、奇人、中地图调查点、角色等一直高亮；只有最近的一个显示热键提示（如“E 互动”）");
            TriggerShowDistance = cfg.Bind("UI增强", "互动点常亮距离", 50,
                "距离以外的互动点不亮，减轻卡顿。默认 50，太小可能出问题");
            ShowStayTrigger = cfg.Bind("UI增强", "高亮停留触发区域", false, "捡蛋等需要停留的触发区域显示出来，并标出停留秒数。需要重新进场景");
        }

        internal static string HarvestName(IEntity entity)
        {
            var harvest = entity.GetComponent<HarvestComponent>();
            return harvest == null ? "" : Game.Data.Get<HarvestPointItem>(harvest.Id).Name;
        }

        // 找到互动点头顶的热键提示（“E 互动”那块）
        internal static GameObject GetHotkeyBase(IEntity entity)
        {
            if (entity == null) return null;
            if (entity.HasComponent<TargetBillboardComponent>())
            {
                GameObject obj = entity.GetComponent<TargetBillboardComponent>().Object;
                if (obj != null)
                {
                    Transform t = obj.transform.Find("Target/UITarget/HotkeyBase");
                    return t != null ? t.gameObject : null;
                }
                if (entity.HasComponent<EntityBillboardComponent>())
                {
                    WgPartyBillboard party = entity.GetComponent<EntityBillboardComponent>().Object.GetComponent<WgPartyBillboard>();
                    Transform t = party != null ? party.transform.Find("Characters/HotkeyBase") : null;
                    return t != null ? t.gameObject : null;
                }
            }
            else if (entity.HasComponent<HarvestPointBillboardComponent>())
            {
                GameObject obj = entity.GetComponent<HarvestPointBillboardComponent>().Object;
                if (obj != null)
                {
                    Transform t = obj.transform.Find("Form/WGTargetBillboard/Target/U/HotkeyBase");
                    return t != null ? t.gameObject : null;
                }
            }
            return null;
        }

        internal static void PlayLearnSound(MovableItemUI ui, string heroId)
        {
            var events = Traverse.Create(ui).Property("EventSystem").GetValue<IReusableEventSystem>();
            events.Publish<AudioEventArgs>(e => e.SetSound(GameConfig.SoundUIPath + "UISE_Party_SkillLearn01.wav",
                Game.UI.Canvas.gameObject, 0f, 1f, false, false));
            events.Publish(new HeroUseItemEventArgs { HeroId = heroId });
        }
    }

    // ───────────── 学技能不吃书 ─────────────

    [HarmonyPatch(typeof(MovableItemUI), "UseBookConfirm")]
    static class Tweak_UseBook
    {
        static bool Prefix(MovableItemUI __instance)
        {
            if (!Tweaks.BookNotConsumed.Value) return true;
            var entity = Traverse.Create(__instance).Field("useingEntity").GetValue<IEntity>();
            var item = Traverse.Create(__instance).Field("currentUseItem").GetValue<ILoopedGridItem<InventoryData>>();
            if (entity == null || item == null) return false;

            NormalInventoryItem book = Game.Data.Get<NormalInventoryItem>(item.Value.Id);
            LearnedSkillComponent learned = entity.GetComponent<LearnedSkillComponent>();
            string heroId = entity.GetComponent<NpcComponent>().Id;
            foreach (LearnedSkill s in learned.Skills)
                if (s.Id == book.Skill) { Plugin.Log.LogInfo(heroId + " 已经有技能 " + book.Skill); return false; }

            learned.Skills.Add(new LearnedSkill { Id = book.Skill });
            Tweaks.PlayLearnSound(__instance, heroId);
            return false;   // 跳过原逻辑（原逻辑会扣掉书）
        }
    }

    [HarmonyPatch(typeof(MovableItemUI), "UseHeartFormulaConfirm")]
    static class Tweak_UseHeartFormula
    {
        static bool Prefix(MovableItemUI __instance)
        {
            if (!Tweaks.BookNotConsumed.Value) return true;
            var entity = Traverse.Create(__instance).Field("useingEntity").GetValue<IEntity>();
            var item = Traverse.Create(__instance).Field("currentUseItem").GetValue<ILoopedGridItem<InventoryData>>();
            if (entity == null || item == null) return false;

            NormalInventoryItem book = Game.Data.Get<NormalInventoryItem>(item.Value.Id);
            PerceptionComponent perception = entity.GetComponent<PerceptionComponent>();
            string heroId = entity.GetComponent<NpcComponent>().Id;
            foreach (string g in perception.ReleaseSpecialGroups)
                if (g == book.Perception) { Plugin.Log.LogInfo(heroId + " 已经有心法 " + book.Perception); return false; }

            Game.World.GetSystem<PerceptionSystem>().AddReleaseSpecialGroup(heroId, new[] { book.Perception });
            Tweaks.PlayLearnSound(__instance, heroId);
            return false;
        }
    }

    // ───────────── 最大上阵人数 ─────────────

    [HarmonyPatch(typeof(PlayerConfigure), "SetNumber", new[] { typeof(int) })]
    static class Tweak_MaxBattleMembers
    {
        static void Prefix(ref int max)
        {
            if (Tweaks.MaxBattleMembers.Value > 0) max = Tweaks.MaxBattleMembers.Value;
        }
    }

    // ───────────── 增加技能栏位 ─────────────

    [HarmonyPatch(typeof(CharacterPropertyInfo), "GetTotalInt")]
    static class Tweak_SkillSlots
    {
        const int SkillColumnProperty = 3040;   // CharacterProperty 里技能栏位数对应的值

        static void Postfix(CharacterPropertyInfo __instance, CharacterProperty property, ref int __result)
        {
            if ((int)property != SkillColumnProperty || Tweaks.ExtraSkillSlots.Value <= 0) return;
            IEntity player = Game.World.GetSystem<PartyCreationSystem>().PlayerEntity;
            PartyComponent party = player?.GetComponent<PartyComponent>();
            if (party == null || __instance.Entity == null) return;
            var members = (IList<string>)party.Members;
            if (members.Contains(__instance.Entity.GetComponent<NpcComponent>().Id))
                __result += Tweaks.ExtraSkillSlots.Value;
        }
    }

    // ───────────── 战斗后自动回满气血内力 ─────────────

    [HarmonyPatch(typeof(UIBattleFinish), "Show")]
    static class Tweak_RestAfterBattle
    {
        static void Postfix()
        {
            if (!Tweaks.RestAfterBattle.Value) return;
            PartyCreationSystem party = Game.World.GetSystem<PartyCreationSystem>();
            NpcDataSystem npcs = Game.World.GetSystem<NpcDataSystem>();
            foreach (string id in party.Player.Members)
            {
                AttributesComponent attr = npcs[id].GetComponent<AttributesComponent>();
                attr.HP.Value = attr.Max_HP.Value;
                attr.MP.Value = attr.Max_MP.Value;
            }
        }
    }

    // ───────────── 互动点常亮 ─────────────

    [HarmonyPatch(typeof(WGHarvestPointBillBoard), "SetMouseEnterView")]
    static class Tweak_Harvest_MouseEnter
    {
        static bool Prefix() => !Tweaks.TriggerAlwaysShow.Value;
    }

    [HarmonyPatch(typeof(WGHarvestPointBillBoard), "SetNormalView")]
    static class Tweak_Harvest_NormalView
    {
        static bool Prefix() => !Tweaks.TriggerAlwaysShow.Value;
    }

    [HarmonyPatch(typeof(HarvestPointBillboardViewSystem), "OnPointerExit")]
    static class Tweak_Harvest_PointerExit
    {
        static void Postfix(IEntity entity)
        {
            if (!Tweaks.TriggerAlwaysShow.Value) return;
            GameObject go = entity.GetGameObject();
            if (go != null) go.AddComponentIfNotExist<Highlighter>(false).enabled = true;
        }
    }

    [HarmonyPatch(typeof(TargetSystem), "BeforeProcessing")]
    static class Tweak_Target_Before
    {
        static void Postfix()
        {
            if (Tweaks.TriggerAlwaysShow.Value) Tweaks.NearbyTargets.Clear();
        }
    }

    [HarmonyPatch(typeof(TargetSystem), "Process")]
    static class Tweak_Target_Process
    {
        static bool Prefix(TargetSystem __instance, int entityId)
        {
            if (!Tweaks.TriggerAlwaysShow.Value) return true;
            var tr = Traverse.Create(__instance);
            IEntity entity = tr.Field("entityCollection").GetValue<IEntityCollection>().GetEntity(entityId);
            GameObject go = entity?.GetGameObject();
            if (go == null || !go.activeSelf) return false;
            var provider = tr.Field("provider").GetValue<IPlayerPositionProvider>();
            if (provider == null || provider.Player == null) return false;

            float dist = Vector3.Distance(provider.Player.transform.position, go.transform.position);
            if (dist <= Tweaks.TriggerShowDistance.Value)
            {
                // 范围内：挂上头顶提示并高亮
                if (!entity.HasComponent<TargetBillboardComponent>() && !entity.HasComponent<HarvestComponent>())
                    entity.AddComponent(new TargetBillboardComponent { Data = entity });
                Tweaks.NearbyTargets.Add(entity);

                if (entity.HasComponent<HarvestComponent>() && entity.HasComponent<HarvestPointBillboardComponent>())
                {
                    WGHarvestPointBillBoard bb = entity.GetComponent<HarvestPointBillboardComponent>().Object.GetComponent<WGHarvestPointBillBoard>();
                    if (bb != null)
                    {
                        Traverse.Create(bb).Field("Name").GetValue<UnityEngine.UI.Text>().text = Tweaks.HarvestName(entity);
                        Highlighter h = go.AddComponentIfNotExist<Highlighter>(false);
                        h.enabled = true;
                        h.ConstantOnImmediate(Color.white);
                    }
                }
            }
            else if (entity.HasComponent<TargetBillboardComponent>())
                entity.RemoveComponent<TargetBillboardComponent>();
            else if (entity.HasComponent<HarvestPointBillboardComponent>())
                entity.RemoveComponent<HarvestPointBillboardComponent>();

            // 原逻辑：记录可互动范围内最近的目标
            if (dist > tr.Field("maxInteractiveDistance").GetValue<float>()) return false;
            if (dist > tr.Field("minDistance").GetValue<float>()) return false;
            tr.Field("currentNearest").SetValue(entity);
            tr.Field("minDistance").SetValue(dist);
            return false;
        }
    }

    [HarmonyPatch(typeof(TargetSystem), "AfterProcessing")]
    static class Tweak_Target_After
    {
        static bool Prefix(TargetSystem __instance)
        {
            if (!Tweaks.TriggerAlwaysShow.Value) return true;
            var tr = Traverse.Create(__instance);
            IEntity nearest = tr.Field("currentNearest").GetValue<IEntity>();

            // 只有最近的那个显示热键提示，其余隐藏
            foreach (IEntity e in Tweaks.NearbyTargets)
            {
                if (e == nearest) continue;
                GameObject hk = Tweaks.GetHotkeyBase(e);
                if (hk != null) hk.SetActive(false);
            }
            if (tr.Field("prevNearest").GetValue<IEntity>() != nearest)
            {
                GameObject hk = Tweaks.GetHotkeyBase(nearest);
                if (hk != null) hk.SetActive(true);
                tr.Field("prevNearest").SetValue(nearest);
            }
            return false;
        }
    }

    // ───────────── 高亮停留触发区域（捡蛋等） ─────────────

    static class StayTrigger
    {
        internal static bool IsActiveStayCube(EventCubeItem item)
        {
            bool conditionOk = item.Condition == null || item.Condition.Output == null
                || item.Condition.GetValue<bool>((Dictionary<string, OutputNode>)null);
            return conditionOk && (int)item.TriggerType == 3;   // 3 = 停留触发
        }
    }

    [HarmonyPatch(typeof(EventCubeMouseEventSystem), "Setup")]
    static class Tweak_StayTrigger_Setup
    {
        static void Postfix(IEntity entity)
        {
            if (!Tweaks.ShowStayTrigger.Value || entity == null) return;
            GameObject go = entity.GetGameObject();
            if (go == null) return;
            EventCubeItem item = entity.GetComponent<EventCubeComponent>().Item;
            if (!StayTrigger.IsActiveStayCube(item)) return;

            item.Name = item.Description + "(停留" + item.StayTime + "秒)";
            if (!entity.HasComponent<TargetBillboardComponent>())
                entity.AddComponent(new TargetBillboardComponent { Data = entity });
            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.enabled = true;
            GameObject hk = Tweaks.GetHotkeyBase(entity);
            if (hk != null) hk.SetActive(false);
        }
    }

    [HarmonyPatch(typeof(EventCubeMouseEventSystem), "OnMouseExit")]
    static class Tweak_StayTrigger_MouseExit
    {
        static bool Prefix(EventCubeMouseEventSystem __instance, IEntity entity)
        {
            if (!Tweaks.ShowStayTrigger.Value || entity == null || entity.GetGameObject() == null) return true;
            EventCubeItem item = entity.GetComponent<EventCubeComponent>().Item;
            if (!StayTrigger.IsActiveStayCube(item)) return true;
            Traverse.Create(__instance).Field("cursor").GetValue<GameCursor>().Reset();
            return false;
        }
    }
}
