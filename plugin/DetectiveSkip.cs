// 一键完成推理：在推理界面按热键（默认 F9），插件替你做“线索配对 -> 选推论 -> 得出结论”，
// 全程调用游戏自己的 CtrlDetective 方法（CompositeClue / ClickOption），旗标、奖励、结论过场都和正常答对一样。
//
// 游戏的推理规则（来自 CtrlDetective）：
//   线索：flag[线索ID] > 0 表示已拥有
//   推论 X 的获得方式：① 两条来源线索配对（ClueSources）  ② 某一条解锁条件（InferenceConditions，"A:B" 形式）
//                     里的推论全部被选中（flag == 2）
//   结论：带 ConclusionImage 的推论；关闭界面时 CloseCinematic 播放结论过场
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Heluo;
using Heluo.Data;
using Heluo.UI;
using UnityEngine;

namespace GuLongOpenMod
{
    internal static class DetectiveSkip
    {
        internal static ConfigEntry<KeyboardShortcut> Hotkey;
        internal static ConfigEntry<bool> AutoSolve;
        internal static ConfigEntry<bool> GrantMissingClues;
        internal static ConfigEntry<bool> AutoClose;

        internal static void Bind(ConfigFile cfg)
        {
            Hotkey = cfg.Bind("推理", "一键完成热键", new KeyboardShortcut(KeyCode.F9),
                "在推理界面里按下，自动完成配对、推论和结论");
            AutoSolve = cfg.Bind("推理", "打开推理界面时自动完成", false,
                "开启后不用按热键，一打开推理界面就直接完成");
            GrantMissingClues = cfg.Bind("推理", "补齐缺少的线索", true,
                "还没收集齐线索时，直接补上需要的线索；关闭则只用已有线索，不够就无法完成");
            AutoClose = cfg.Bind("推理", "完成后自动关闭界面", true,
                "完成后自动关闭推理界面，接着播放结论剧情");
        }

        static UIDetective OpenUI()
        {
            var ui = Game.UI.Get<UIDetective>();
            return ui != null && ui.gameObject.activeInHierarchy ? ui : null;
        }

        internal static void Tick()
        {
            if (Hotkey == null || !Hotkey.Value.IsDown()) return;
            var ui = OpenUI();
            if (ui != null) Solve(ui);
        }

        internal static IEnumerator SolveNextFrame(UIDetective ui)
        {
            yield return null;
            yield return null;
            if (ui != null && ui.gameObject.activeInHierarchy) Solve(ui);
        }

        internal static void Solve(UIDetective ui)
        {
            try
            {
                var ctrl = Traverse.Create(ui).Field("controller").GetValue<CtrlDetective>();
                if (ctrl == null) { Plugin.Log.LogWarning("[推理] 没有找到推理控制器"); return; }
                var trc = Traverse.Create(ctrl);
                var conclusions = trc.Field("stageConclusions").GetValue<List<string>>();
                if (conclusions == null || conclusions.Count == 0) { Plugin.Log.LogWarning("[推理] 本关没有结论"); return; }

                string target = PickConclusion(conclusions, ctrl.conclusionFlag);
                Plugin.Log.LogInfo($"[推理] 目标结论 {target} ({Game.Data.Get<InferenceItem>(target)?.Title})，结论旗标 {ctrl.conclusionFlag}");

                if (!Obtain(target, ctrl, new HashSet<string>(), 0))
                {
                    Plugin.Log.LogWarning("[推理] 推不出结论（线索不够？可以打开“补齐缺少的线索”）");
                    return;
                }
                if (Game.GameData.Flag[target] != 2) ctrl.ClickOption(target);   // 选中结论
                ctrl.UpdateInference();
                ui.CloseOptionUI();
                Plugin.Log.LogInfo("[推理] 已完成");

                if (AutoClose.Value) ui.Close();   // 走游戏原本的关闭流程，会播放结论过场
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[推理] 一键完成出错: " + ex);
            }
        }

        // 正确结论：Flag 等于关卡的结论旗标的那个；没有就选带过场的；再没有就第一个
        static string PickConclusion(List<string> conclusions, string conclusionFlag)
        {
            if (!string.IsNullOrEmpty(conclusionFlag))
                foreach (var id in conclusions)
                    if (Game.Data.Get<InferenceItem>(id)?.Flag == conclusionFlag) return id;
            foreach (var id in conclusions)
                if (!string.IsNullOrEmpty(Game.Data.Get<InferenceItem>(id)?.Cinematic)) return id;
            return conclusions[0];
        }

        // 让推论 id 变成“已拥有”（flag > 0）。按游戏规则：线索配对，或者满足某条解锁条件
        static bool Obtain(string id, CtrlDetective ctrl, HashSet<string> visiting, int depth)
        {
            var flag = Game.GameData.Flag;
            if (flag[id] > 0) return true;
            if (depth > 20 || !visiting.Add(id)) return false;
            var inf = Game.Data.Get<InferenceItem>(id);
            if (inf == null) return false;

            // ① 两条线索配对
            if (inf.ClueSources != null && inf.ClueSources.Count >= 2)
            {
                string c1 = inf.ClueSources[0], c2 = inf.ClueSources[1];
                if (HaveClue(c1) && HaveClue(c2))
                {
                    ctrl.CompositeClue(c1, c2);
                    if (flag[id] > 0) { Plugin.Log.LogInfo($"[推理] 配对 {c1} + {c2} -> {inf.Title}"); return true; }
                }
            }

            // ② 解锁条件：选中条件里的全部推论
            if (inf.InferenceConditions != null)
            {
                foreach (var cond in inf.InferenceConditions)
                {
                    var parts = cond.Split(':');
                    bool ok = true;
                    foreach (var p in parts)
                        if (!Obtain(p.Trim(), ctrl, visiting, depth + 1)) { ok = false; break; }
                    if (!ok) continue;
                    foreach (var p in parts)
                        if (flag[p.Trim()] != 2) ctrl.ClickOption(p.Trim());
                    if (flag[id] > 0) { Plugin.Log.LogInfo($"[推理] 选中 {cond} -> {inf.Title}"); return true; }
                }
            }
            return flag[id] > 0;
        }

        static bool HaveClue(string clueId)
        {
            var flag = Game.GameData.Flag;
            if (flag[clueId] > 0) return true;
            if (!GrantMissingClues.Value) return false;
            flag[clueId] = 1;
            Plugin.Log.LogInfo($"[推理] 补上线索 {clueId} ({Game.Data.Get<ClueItem>(clueId)?.Title})");
            return true;
        }
    }

    [HarmonyPatch(typeof(UIDetective), "Load")]
    static class Patch_DetectiveLoad
    {
        static void Postfix(UIDetective __instance)
        {
            if (DetectiveSkip.AutoSolve != null && DetectiveSkip.AutoSolve.Value && Plugin.Instance != null)
                Plugin.Instance.StartCoroutine(DetectiveSkip.SolveNextFrame(__instance));
        }
    }
}
