// 主角改名：剧情对话里的“辰雨”是写死在对话文本里的，改 AvatarItem 表只能改掉名字框/界面上的名字。
// 这里在对话显示前（UINpcDialog / UIDialogHistory 的 ParsingToItem）把文本里的原名换成新名字，
// 新名字默认直接读你改过的 AvatarItem 表（主角 Avatar Id 默认 na0000），也可以在 F1 里手动填。
// 另外存档里可能保存了主角的 OverrideAvatar（旧名字），进存档时一并改掉。
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using EcsRx.Entities;
using EcsRx.Extensions;
using HarmonyLib;
using Heluo;
using Heluo.Data;
using Heluo.Features;
using Heluo.UI;

namespace GuLongOpenMod
{
    internal static class PlayerRename
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> OldSurname, OldName, NewSurname, NewName, AvatarId;

        // 主角在不同阶段用到的 NPC ID（参考寻宇插件）
        internal static readonly string[] PlayerNpcIds = { "na0000", "na0000a", "na0000_boss", "nq0131" };

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("主角改名", "启用", true, "把剧情对话里的主角原名换成新名字");
            AvatarId = cfg.Bind("主角改名", "主角AvatarId", "na0000", "新名字默认从 AvatarItem 表的这一行读取（姓 + 名）");
            NewSurname = cfg.Bind("主角改名", "新姓(可选)", "", "留空 = 用 AvatarItem 表里的姓");
            NewName = cfg.Bind("主角改名", "新名(可选)", "", "留空 = 用 AvatarItem 表里的名");
            OldSurname = cfg.Bind("主角改名", "原姓", "辰", "对话文本里要被替换掉的原姓");
            OldName = cfg.Bind("主角改名", "原名", "雨", "对话文本里要被替换掉的原名");
            NewSurname.SettingChanged += (s, e) => { cachedKey = null; ApplyToSave(); };
            NewName.SettingChanged += (s, e) => { cachedKey = null; ApplyToSave(); };
        }

        internal static bool TryGetNewName(out string surname, out string name)
        {
            surname = NewSurname.Value; name = NewName.Value;
            if (string.IsNullOrEmpty(surname) || string.IsNullOrEmpty(name))
            {
                AvatarItem a = null;
                try { a = Game.Data.Get<AvatarItem>(AvatarId.Value); } catch { }
                if (a != null)
                {
                    if (string.IsNullOrEmpty(surname)) surname = a.Surname;
                    if (string.IsNullOrEmpty(name)) name = a.Name;
                }
            }
            return !string.IsNullOrEmpty(surname) && !string.IsNullOrEmpty(name)
                && !(surname == OldSurname.Value && name == OldName.Value);
        }

        // ── 对话文本替换 ──
        static string cachedKey;
        static Regex stutter, spelled, fullName, surnameTitle;
        static string ns, nn;

        static bool Prepare()
        {
            if (!Enabled.Value || !TryGetNewName(out var s, out var n)) return false;
            string key = OldSurname.Value + "|" + OldName.Value + "|" + s + "|" + n;
            if (key == cachedKey) return true;
            cachedKey = key; ns = s; nn = n;
            string os = Regex.Escape(OldSurname.Value), on = Regex.Escape(OldName.Value);
            // 结巴：辰……辰雨、辰、辰雨、辰……小辰雨
            stutter = new Regex(os + "([…、，,！!—\\-~～.。]+)(小)?" + os + on);
            // 拆开念：辰、雨 / 辰！雨
            spelled = new Regex(os + "([、！!])" + on);
            // 全名及常见变体：辰雨、姓辰名雨、辰什么雨
            fullName = new Regex("姓" + os + "名" + on + "|" + os + "什么" + on + "|" + os + on);
            // 只用姓的称呼：辰公子、辰兄、辰少侠……
            surnameTitle = new Regex(os + "(公子|少侠|大侠|小兄弟|兄弟|兄台|兄|老弟|贤弟|小子|大哥|少爷|姑娘|先生)");
            Plugin.Log.LogInfo($"[改名] 对话中 {OldSurname.Value}{OldName.Value} -> {ns}{nn}");
            return true;
        }

        internal static string Replace(string content)
        {
            if (string.IsNullOrEmpty(content) || !Prepare()) return content;
            if (content.IndexOf(OldSurname.Value, System.StringComparison.Ordinal) < 0) return content;
            content = stutter.Replace(content, m => ns + m.Groups[1].Value + m.Groups[2].Value + ns + nn);
            content = spelled.Replace(content, m => string.Join(m.Groups[1].Value, (ns + nn).ToCharArray()));
            content = fullName.Replace(content, m =>
                m.Value.StartsWith("姓") ? "姓" + ns + "名" + nn :
                m.Value.Contains("什么") ? ns + "什么" + nn : ns + nn);
            content = surnameTitle.Replace(content, m => ns + m.Groups[1].Value);
            return content;
        }

        // ── 存档里的主角名字（OverrideAvatar）──
        internal static void ApplyToEntity(IEntity entity)
        {
            if (!Enabled.Value || entity == null || !TryGetNewName(out var s, out var n)) return;
            var npc = entity.GetComponent<NpcComponent>();
            if (npc == null || System.Array.IndexOf(PlayerNpcIds, npc.Id) < 0) return;
            if (npc.OverrideAvatar != null) { npc.OverrideAvatar.Surname = s; npc.OverrideAvatar.Name = n; }
            if (npc.Item != null && npc.Item.Avatar != null) { npc.Item.Avatar.Surname = s; npc.Item.Avatar.Name = n; }
        }

        internal static void ApplyToSave()
        {
            try
            {
                var sys = Game.World?.GetSystem<NpcDataSystem>();
                if (sys == null) return;
                foreach (var id in PlayerNpcIds) ApplyToEntity(sys[id]);
            }
            catch { /* 还没进存档 */ }
        }
    }

    [HarmonyPatch(typeof(UINpcDialog), "ParsingToItem")]
    static class Patch_Rename_Dialog
    {
        static void Postfix(ref string __result) => __result = PlayerRename.Replace(__result);
    }

    [HarmonyPatch(typeof(UIDialogHistory), "ParsingToItem")]
    static class Patch_Rename_History
    {
        static void Postfix(ref string __result) => __result = PlayerRename.Replace(__result);
    }

    [HarmonyPatch(typeof(NpcDataSystem), "Setup")]
    static class Patch_Rename_NpcSetup
    {
        static void Postfix(IEntity entity) => PlayerRename.ApplyToEntity(entity);
    }
}
