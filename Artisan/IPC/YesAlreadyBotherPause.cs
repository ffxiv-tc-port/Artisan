using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;
using System;
using System.Linq;

namespace Artisan.IPC;

/// <summary>請 YesAlready 暫停它自動按某個視窗；只在框架執行緒呼叫。</summary>
internal static class YesAlreadyBotherPause
{
    internal enum Result
    {
        /// <summary>確認不了：YesAlready 載入了但 IPC 出錯。零值刻意給這個。</summary>
        Failed = 0,

        Paused,

        /// <summary>那個功能本來就沒開（YesAlready 回 false），它不會按。</summary>
        NotActive,

        /// <summary>YesAlready 沒有載入，它不會按。</summary>
        NotLoaded,
    }

    private const string InternalName = "YesAlready";

    // 簽章逐字對應 YesAlready 的 PauseBother(string name, int milliseconds) -> bool。
    private static ICallGateSubscriber<string, int, bool>? pauseBother;

    internal static Result Pause(string feature, int milliseconds)
    {
        try
        {
            pauseBother ??= Svc.PluginInterface.GetIpcSubscriber<string, int, bool>("YesAlready.PauseBother");
            return pauseBother.InvokeFunc(feature, milliseconds) ? Result.Paused : Result.NotActive;
        }
        catch (IpcNotReadyError)
        {
            return IsLoaded() ? Result.Failed : Result.NotLoaded;
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[Artisan] YesAlready.PauseBother（{feature}）失敗（{ex.GetType().Name}）：{ex.Message}");
            return Result.Failed;
        }
    }

    private static bool IsLoaded()
    {
        try
        {
            return Svc.PluginInterface.InstalledPlugins.Any(p => p.InternalName == InternalName && p.IsLoaded);
        }
        catch (Exception)
        {
            // 問不到就當成載入了，交給呼叫端走「不按」。
            return true;
        }
    }
}
