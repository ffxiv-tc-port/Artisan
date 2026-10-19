using ECommons.DalamudServices;
using ECommons.Reflection;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using System.Numerics;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using static ECommons.GenericHelpers;
using Artisan.IPC;
using System.Collections.Generic;
using System.Linq;
using System;
using ECommons.Automation.LegacyTaskManager;

namespace Artisan.Tasks;

internal unsafe static class TaskInteractWithNearestBell
{
    internal static void EnqueueBell(this TaskManager TM)
    {
        TM.Enqueue(YesAlready.Lock);
        TM.Enqueue(TextAdvanceLock.Lock);
        TM.Enqueue(PlayerWorldHandlers.SelectNearestBell);
        TM.Enqueue(PlayerWorldHandlers.InteractWithTargetedBell);
    }
}

internal static class YesAlready
{
    // 刻意不保留 1.4.0.0 之前 YesAlready 的反射路徑（寫 Service.Configuration.Enabled）：那個成員已經不存在，
    // 只要真的跑到就是 NullReferenceException。艦隊的 YesAlready 遠高於 1.4.0.0，一律走共享資料 StopRequests。
    internal static bool Reenable = false;
    internal static HashSet<string>? Data = null;

    internal static void GetData()
    {
        if (Data != null) return;
        if (Svc.PluginInterface.TryGetData<HashSet<string>>("YesAlready.StopRequests", out var data))
        {
            Data = data;
        }
    }

    internal static void Lock()
    {
        GetData();
        if (Data != null)
        {
            Svc.Log.Information("Disabling Yes Already");
            Data.Add(Svc.PluginInterface.InternalName);
            Reenable = true;
            reAddLogged = false;
        }
    }

    internal static void Unlock()
    {
        if (!Reenable) return;

        GetData();
        if (Data != null)
        {
            Svc.Log.Information("Enabling Yes Already");
            Data.Remove(Svc.PluginInterface.InternalName);
            Reenable = false;
        }
    }

    private static bool reAddLogged;

    /// <summary>持有期間每幀叫：YesAlready 的看門狗逾時會移除持有者，被移除就補回。只在框架執行緒呼叫。</summary>
    internal static void Reassert()
    {
        if (!Reenable || Data == null) return;
        try
        {
            if (!Data.Add(Svc.PluginInterface.InternalName)) return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (reAddLogged) return;
        reAddLogged = true;
        Svc.Log.Information("[Artisan] YesAlready 的暫停名單裡沒有本插件了（逾時移除或被清空），已補回。");
    }

    internal static bool IsEnabled()
    {
        GetData();
        if (Data != null)
        {
            return !Data.Contains(Svc.PluginInterface.InternalName);
        }

        return false;
    }

    internal static bool? WaitForYesAlreadyDisabledTask()
    {
        return !IsEnabled();
    }
}

/// <summary>
/// 補貨期間請 TextAdvance 暫停：Talk 由 <c>RetainerInfo.Tick</c> 自己按，TextAdvance 在傳喚鈴前也會按，
/// 兩邊同一幀各按一次＝第二次打在關閉中的 Talk 上（攔不到的 AVE）。只在框架執行緒呼叫。
/// </summary>
/// <remarks>
/// TextAdvance 重新載入會清空名單 ⇒ 持有期間每幀 <see cref="Reassert"/>。快取的集合參考在它重載後仍有效：
/// 本插件的登記撐著 DataShare 的條目，它重載時 <c>GetOrCreateData</c> 拿到的是同一個集合。
/// </remarks>
internal static class TextAdvanceLock
{
    private static bool held;
    private static bool reAddLogged;
    private static DateTime addedInFrame = DateTime.MaxValue;
    private static HashSet<string>? data;

    private static HashSet<string>? GetData()
    {
        // 快取住：TryGetData 每次成功都會在 DataShare 多登記一次使用者。
        if (data == null && Svc.PluginInterface.TryGetData<HashSet<string>>("TextAdvance.StopRequests", out var d))
            data = d;
        return data;
    }

    internal static void Lock()
    {
        var d = GetData();
        if (d == null) return;
        Svc.Log.Information("Disabling TextAdvance");
        d.Add(Svc.PluginInterface.InternalName);
        addedInFrame = Svc.Framework.LastUpdateUTC;
        held = true;
        reAddLogged = false;
    }

    /// <summary>持有期間每幀叫；名字不在名單上就補回。</summary>
    internal static void Reassert()
    {
        if (!held || data == null) return;
        try
        {
            if (!data.Add(Svc.PluginInterface.InternalName)) return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        addedInFrame = Svc.Framework.LastUpdateUTC;
        if (reAddLogged) return;
        reAddLogged = true;
        Svc.Log.Information("[Artisan] TextAdvance 的暫停名單裡沒有本插件了（它可能重新載入過），已補回。");
    }

    /// <summary>沒持有＝true；持有中則要等名字在名單上滿一整幀（TextAdvance 同一幀稍早可能已經按過）。</summary>
    internal static bool MayClickTalk => !held || Svc.Framework.LastUpdateUTC > addedInFrame;

    internal static void Unlock()
    {
        if (!held) return;
        var d = GetData();
        if (d != null)
        {
            Svc.Log.Information("Enabling TextAdvance");
            d.Remove(Svc.PluginInterface.InternalName);
        }
        held = false;
        addedInFrame = DateTime.MaxValue;
    }
}

internal unsafe static class PlayerWorldHandlers
{
    internal static bool? SelectNearestBell()
    {
        if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell]) return true;
        if (!IsOccupied())
        {
            var x = RetainerInfo.GetReachableRetainerBell();
            if (x != null)
            {
                if (RetainerInfo.GenericThrottle)
                {
                    Svc.Targets.Target = x;
                    Svc.Log.Debug($"Set target to {x}");
                    return true;
                }
            }
        }
        return false;
    }

    internal static bool? InteractWithTargetedBell()
    {
        if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell]) return true;
        var x = Svc.Targets.Target;
        if (x != null && (x.ObjectKind == ObjectKind.Housing || x.ObjectKind == ObjectKind.EventObj) && x.Name.ToString().EqualsIgnoreCaseAny(RetainerInfo.BellName, "リテイナーベル") && !IsOccupied())
        {
            if (Vector3.Distance(x.Position, Svc.Objects.LocalPlayer.Position) < RetainerInfo.GetValidInteractionDistance(x) && x.IsTargetable())
            {
                if (RetainerInfo.GenericThrottle && EzThrottler.Throttle("InteractWithBell", 5000))
                {
                    TargetSystem.Instance()->InteractWithObject((GameObject*)x.Address, false);
                    Svc.Log.Debug($"Interacted with {x}");
                    return true;
                }
            }
        }
        return false;
    }
}