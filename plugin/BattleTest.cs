// 测试用：按热键直接进入一场战斗（和剧情里的“進入戰鬥”节点 BattleAction 走同一套流程）
// 战斗编号对应 Config/Battle/Area(SC)/<编号>.json；默认 xk_test 是插件配套的木桩测试战（不要用剧情战斗编号）
using System.Collections.Generic;
using BepInEx.Configuration;
using Heluo;
using Heluo.UI;
using UnityEngine;

namespace GuLongOpenMod
{
    internal static class BattleTest
    {
        internal static ConfigEntry<string> BattleId;
        internal static ConfigEntry<KeyboardShortcut> Hotkey;

        internal static void Bind(ConfigFile cfg)
        {
            BattleId = cfg.Bind("武学测试", "测试战斗编号", "xk_test",
                "按热键进入的战斗（Config/Battle/Area(SC)/编号.json）。建议先存档再用");
            Hotkey = cfg.Bind("武学测试", "进入战斗热键", new KeyboardShortcut(KeyCode.F11),
                "在场景或大地图里按，直接进入上面编号的战斗");
        }

        internal static void Tick()
        {
            if (Hotkey == null || !Hotkey.Value.IsDown()) return;
            try
            {
                if (Game.BattleStateMachine != null) { Plugin.Log.LogInfo("[测试战斗] 已经在战斗中"); return; }
                if (Game.World?.GetSystem<Heluo.Features.PartyCreationSystem>()?.PlayerEntity == null)
                { Plugin.Log.LogWarning("[测试战斗] 还没进存档"); return; }

                string id = BattleId.Value.Trim();
                Game.UI.Open<UIBillboard>();
                var black = Game.UI.Open<UIBlackScreen>();
                if (black != null) black.DoFade(false, 0.5f);
                var e = new Heluo.FSM.Main.BattleEventArgs { BattleId = id, ArmyIdList = new List<string>() };
                Game.FSM.SendEvent("BATTLE", e);
                Plugin.Log.LogInfo("[测试战斗] 进入战斗 " + id);
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[测试战斗] 出错: " + ex); }
        }
    }
}
