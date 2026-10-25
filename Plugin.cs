using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace PathOfIdleEditor;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("PathOfIdle.exe")]
public sealed class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;

    // 模组设置（BepInEx ConfigurationManager 里可见）：默认关闭，避免正常游玩时刷屏。
    private static ConfigEntry<bool> _verboseLogging = null!;

    internal static bool VerboseEnabled => _verboseLogging?.Value == true;

    // 诊断日志统一入口：只有开关打开时才输出。
    internal static void Verbose(string message)
    {
        if (VerboseEnabled)
            Log.LogInfo(message);
    }

    public override void Load()
    {
        Log = base.Log;

        _verboseLogging = Config.Bind(
            "日志",
            "输出详细日志",
            false,
            "开启后输出词条档位/数值探针、词条写入等诊断信息，用于排查问题；平时保持关闭。");

        // 打开开关时立刻补打一次探针，不必重启游戏。
        _verboseLogging.SettingChanged += (_, _) =>
        {
            if (VerboseEnabled)
                BridgeServer.LogAffixProbe("已开启详细日志");
        };

        new HarmonyLib.Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();
        // 管道监听在线程池中运行；所有游戏对象读写会由 BridgeBehaviour 转回 Unity 主线程。
        BridgeServer.Start();
        ClassInjector.RegisterTypeInIl2Cpp<BridgeBehaviour>();
        AddComponent<BridgeBehaviour>();
        Log.LogInfo("Path of Idle Editor bridge loaded. Start the standalone editor to connect.");
    }
}
