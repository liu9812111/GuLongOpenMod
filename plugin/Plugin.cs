using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace GuLongOpenMod
{
    [BepInPlugin(Guid, "古龙风云录 开放 Mod", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "gulong.openmod";
        public const string Version = "0.1.2";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        void Awake()
        {
            Log = Logger;
            Instance = this;
            try { Init(); }
            catch (System.Exception ex) { Log.LogError("插件初始化失败: " + ex); }
        }

        void Start()
        {
            if (BgmReplace.Enabled != null) StartCoroutine(BgmReplace.PreloadAll());
        }

        void Update()
        {
            PackReload.Tick();
            BgmReplace.Tick();
            DetectiveSkip.Tick();
            SkillDebug.Tick();
            BattleTest.Tick();
            TimelineDump.Tick();
            SkillPreview.Tick();
            SkillMod.Tick();
            ItemGive.Tick();
        }

        void OnGUI()
        {
            SkillPreview.OnGUI();
        }

        void Init()
        {
            // 内容包最先扫描：后面的改招、BGM 都要用到包的目录
            Packs.Bind(Config);
            Packs.Scan();
            PackReload.Bind(Config);

            Tweaks.Bind(Config);
            BgmReplace.Bind(Config);
            DetectiveSkip.Bind(Config);
            PlayerRename.Bind(Config);
            SkillDebug.Bind(Config);
            BattleTest.Bind(Config);
            TimelineDump.Bind(Config);
            SkillPreview.Bind(Config);
            SkillMod.Bind(Config);
            ItemGive.Bind(Config);

            // 逐个补丁类打补丁：某个功能对应的游戏函数变了只会让那一个功能失效，不影响其他功能
            var harmony = new Harmony(Guid);
            int ok = 0, fail = 0;
            foreach (var t in typeof(Plugin).Assembly.GetTypes())
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try { harmony.CreateClassProcessor(t).Patch(); ok++; }
                catch (System.Exception ex) { fail++; Log.LogError("补丁 " + t.Name + " 失败: " + ex.Message); }
            }
            Log.LogInfo($"补丁完成：成功 {ok} 个，失败 {fail} 个");
            Log.LogInfo($"古龙风云录 开放 Mod {Version} 已加载");
        }
    }
}
