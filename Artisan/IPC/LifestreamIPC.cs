using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;
using System;
using System.Linq;

namespace Artisan.IPC;

/// <summary>透過 Lifestream 的 IPC 跨世界移動；只在框架／繪製執行緒呼叫。</summary>
/// <remarks>
/// 🔴 不用聊天指令送 <c>/li</c>：空參數的 <c>/li</c> 等於傳送回本世界。
/// 這裡在送出前自己擋空世界名，Lifestream 的 <c>ExecuteCommand</c> 提供端也會再擋一次。
/// </remarks>
internal static class LifestreamIPC
{
    internal enum TravelResult
    {
        /// <summary>沒送出：IPC 出錯。零值刻意給這個。</summary>
        Failed = 0,

        Sent,

        /// <summary>沒送出：世界名是空的。</summary>
        NoWorld,

        /// <summary>沒送出：Lifestream 沒有載入。</summary>
        NotLoaded,
    }

    private const string InternalName = "Lifestream";

    // 簽章逐字對應 Lifestream 的 void ExecuteCommand(string)。
    private static ICallGateSubscriber<string, object>? executeCommand;

    /// <summary>請 Lifestream 前往 <paramref name="world"/> 的市場佈告板（等同 <c>/li 世界 mb</c>）。</summary>
    internal static TravelResult TravelToMarketboard(string? world)
    {
        if (string.IsNullOrWhiteSpace(world))
            return TravelResult.NoWorld;
        // 按下去才跑一次 ⇒ 即時讀 IsLoaded，不用材料表那份每秒一次的快取。
        if (!IsLoaded())
            return TravelResult.NotLoaded;

        try
        {
            executeCommand ??= Svc.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
            executeCommand.InvokeAction($"{world.Trim()} mb");
            return TravelResult.Sent;
        }
        catch (IpcNotReadyError)
        {
            return TravelResult.NotLoaded;
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[Artisan] Lifestream.ExecuteCommand（{world} mb）失敗（{ex.GetType().Name}）：{ex.Message}");
            return TravelResult.Failed;
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
            return false;
        }
    }
}
