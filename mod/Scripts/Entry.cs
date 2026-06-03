using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using STS2RitsuLib;
using STS2RitsuLib.Telemetry;

namespace STS2Agent.Scripts;

/// <summary>
/// Mod 入口 — 在游戏启动时注册遥测系统和事件监听。
/// </summary>
[ModInitializer(nameof(Init))]
public static class Entry
{
    public const string ModId = "STS2Agent";
    public static readonly Logger Logger = RitsuLibFramework.CreateLogger(ModId);

    public static void Init()
    {
        Logger.Info("STS2 AI Agent initializing...");

        // 初始化 Harmony（未来可能需要 patch 某些游戏逻辑）
        var harmony = new Harmony("sts2.freezetheflame.sts2agent");
        harmony.PatchAll();

        // 注册遥测申请方 + 事件监听
        AgentTelemetry.Register();

        Logger.Info("STS2 AI Agent initialized. Telemetry endpoint: http://localhost:8765");
    }
}
