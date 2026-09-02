using Artisan.GameInterop;
using Artisan.IPC;
using Artisan.RawInformation;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.Events;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using static ECommons.GenericHelpers;
using MemoryHelper = Dalamud.Memory.MemoryHelper;
using ECommons.Automation.UIInput;


namespace Artisan.Tasks;

internal static class TaskSelectRetainer
{
    internal static void EnqueueRetainer(this TaskManager TM, ulong id)
    {
        TM.Enqueue(() => RetainerListHandlers.SelectRetainerByID(id));
        TM.Enqueue(() => RetainerListHandlers.TryGetCurrentRetainer(out _));
    }
}

internal unsafe static class RetainerListHandlers
{
    internal static bool? SelectRetainerByID(ulong id)
    {
        var retainerName = ResolveRetainerName(id);
        if (string.IsNullOrEmpty(retainerName))
        {
            // 🔴 Returns false ("not done, ask me again") rather than throwing. This task runs 200ms after the
            // summoning bell is touched, and the client has usually not received the retainer list by then.
            // SelectRetainerByName below is already written to be retried until the RetainerList addon is
            // ready - that is what its bool? result is for - but throwing here jumped straight past it, and
            // ECommons' TaskManager answers an exception by logging the message, dropping the task and
            // carrying on with the rest of the queue. The whole restock then ran with no retainer ever
            // selected: the retainer window never opened, so every later step quietly did nothing while
            // looking like it was working.
            ReportRetainerUnresolved(id);
            return false;
        }

        return SelectRetainerByName(retainerName);
    }

    /// <summary>
    /// Turns a retainer id into its name, or "" when the client cannot answer yet.
    /// <para/>
    /// 🔴 Deliberately walks the raw <c>Retainers</c> array rather than calling
    /// <c>GetRetainerBySortedIndex</c>. That helper indexes through the display-order table at +0x2D0 and
    /// returns <c>&amp;Retainers[displayOrder[i]]</c>, so for as long as that table is still zeroed - it is
    /// only filled in once the retainer list has actually loaded - every sorted index resolves to retainer 0
    /// and a lookup for any other retainer silently finds nothing. AutoRetainer's own GameRetainerManager
    /// guards against the same table being unpopulated. The raw array needs no such table.
    /// </summary>
    private static string ResolveRetainerName(ulong id)
    {
        if (id == 0) return "";

        var manager = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance();
        if (manager == null) return "";

        for (var i = 0; i < manager->Retainers.Length; i++)
        {
            var retainer = manager->Retainers[i];
            if (retainer.RetainerId == id)
                return retainer.NameString;
        }

        return "";
    }

    /// <summary>
    /// Says why a retainer id could not be turned into a name. Information level because that is the level
    /// users actually run at, and because this is otherwise completely invisible - the only symptom is
    /// "restocking did nothing". Throttled per id, since the caller polls this.
    /// </summary>
    private static void ReportRetainerUnresolved(ulong id)
    {
        if (!EzThrottler.Throttle($"ArtisanResolveRetainer{id}", 3000)) return;

        var manager = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance();
        if (manager == null)
        {
            Svc.Log.Information($"[Artisan][Restock] Retainer {id} cannot be resolved: RetainerManager is not available yet. Waiting.");
            return;
        }

        var loaded = new List<ulong>();
        for (var i = 0; i < manager->Retainers.Length; i++)
        {
            var retainer = manager->Retainers[i];
            if (retainer.RetainerId != 0) loaded.Add(retainer.RetainerId);
        }

        Svc.Log.Information($"[Artisan][Restock] Retainer {id} is not in the client's retainer list yet " +
                            $"(IsReady={manager->IsReady}, {loaded.Count} loaded: {(loaded.Count == 0 ? "none" : string.Join(", ", loaded))}). " +
                            $"Waiting for the list to arrive rather than skipping this retainer.");
    }


    internal static bool? SelectRetainerByName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new Exception($"Name can not be null or empty");
        }
        if (TryGetAddonByName<AtkUnitBase>("RetainerList", out var retainerList) && IsAddonReady(retainerList))
        {
            var list = new AddonMaster.RetainerList(retainerList);
            foreach (var retainer in list.Retainers)
            {
                // 讀窗文字做判定:讀到 U+FFFD 代表窗記憶體正在變動,這一幀不碰。
                if (AddonPressGuard.IsTextCorrupt("RetainerList", retainer.Name)) return false;
                if (retainer.Name == name)
                {
                    if (RetainerInfo.GenericThrottle)
                    {
                        Svc.Log.Debug($"Selecting retainer {retainer.Name} with index {retainer.Index}");
                        retainer.Select();
                        return true;
                    }
                }
            }
        }

        return false;
    }


    internal static bool? CloseRetainerList()
    {
        if (TryGetAddonByName<AtkUnitBase>("RetainerList", out var retainerList) && IsAddonReady(retainerList))
        {
            if (RetainerInfo.GenericThrottle)
            {
                var v = stackalloc AtkValue[1]
                {
                    new()
                    {
                        Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int,
                        Int = -1
                    }
                };

                retainerList->FireCallback(1, v);
                return true;
            }
        }
        return false;
    }

    internal static bool TryGetCurrentRetainer(out string name)
    {
        if (Svc.Condition[ConditionFlag.OccupiedSummoningBell] && ProperOnLogin.PlayerPresent && Svc.Objects.Where(x => x.ObjectKind == ObjectKind.Retainer).OrderBy(x => Vector3.Distance(Svc.Objects.LocalPlayer.Position, x.Position)).TryGetFirst(out var obj))
        {
            name = obj.Name.ToString();
            return true;
        }
        name = "";
        return false;
    }
}

public unsafe class RetainerManager
{
    private static StaticRetainerContainer? _address;
    private static RetainerContainer* _container;

    public RetainerManager(ISigScanner sigScanner)
    {
        if (_address != null)
            return;

        _address ??= new StaticRetainerContainer(sigScanner);
        _container = (RetainerContainer*)_address.Address;
    }

    public bool Ready
        => _container != null && _container->Ready == 1;

    public int Count
        => Ready ? _container->RetainerCount : 0;

    public SeRetainer Retainer(int which)
        => which < Count
            ? ((SeRetainer*)_container->Retainers)[which]
            : throw new ArgumentOutOfRangeException($"Invalid retainer {which} requested, only {Count} available.");
    public void* RetainerAddress(int which)
        => which < Count
            ? &((SeRetainer*)_container->Retainers)[which]
            : throw new ArgumentOutOfRangeException($"Invalid retainer {which} requested, only {Count} available.");
}

public sealed class StaticRetainerContainer : SeAddressBase
{
    public StaticRetainerContainer(ISigScanner sigScanner)
        : base(sigScanner, "48 8B E9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0 74 4E")
    { }
}

public class SeAddressBase
{
    public readonly IntPtr Address;

    public SeAddressBase(ISigScanner sigScanner, string signature, int offset = 0)
    {
        return;
        Address = sigScanner.GetStaticAddressFromSig(signature);
        if (Address != IntPtr.Zero)
            Address += offset;
        var baseOffset = (ulong)Address.ToInt64() - (ulong)sigScanner.Module.BaseAddress.ToInt64();
    }
}

[StructLayout(LayoutKind.Sequential, Size = SeRetainer.Size * 10 + 12)]
public unsafe struct RetainerContainer
{
    public fixed byte Retainers[SeRetainer.Size * 10];
    public fixed byte DisplayOrder[10];
    public byte Ready;
    public byte RetainerCount;
}

[StructLayout(LayoutKind.Explicit, Size = Size)]
public unsafe struct SeRetainer
{
    public const int Size = 0x48;

    [FieldOffset(0x00)]
    public ulong RetainerID;

    [FieldOffset(0x08)]
    private fixed byte _name[0x20];

    [FieldOffset(0x29)]
    public byte ClassJob;

    [FieldOffset(0x2A)]
    public byte Level;

    [FieldOffset(0x2C)]
    public uint Gil;

    [FieldOffset(0x38)]
    public uint VentureID;

    [FieldOffset(0x3C)]
    public uint VentureCompleteTimeStamp;

    public bool Available
        => ClassJob != 0;

    public SeString Name
    {
        get
        {
            fixed (byte* name = _name)
            {
                return MemoryHelper.ReadSeStringNullTerminated((IntPtr)name);
            }
        }
    }
}

internal unsafe static class RetainerHandlers
{
    internal static bool? SelectQuit()
    {
        var text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2383).Text.ToDalamudString().GetText(true);
        return TrySelectSpecificEntry(text);
    }

    internal static bool? SelectEntrustItems()
    {
        //2378	Entrust or withdraw items.
        var text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2378).Text.ToDalamudString().GetText(true);
        return TrySelectSpecificEntry(text);
    }

    internal static bool? OpenItemContextMenu(uint ItemId, bool lookingForHQ, out int quantity)
    {
        quantity = 0;
        var inventories = new List<InventoryType>
        {
            InventoryType.RetainerPage1,
            InventoryType.RetainerPage2,
            InventoryType.RetainerPage3,
            InventoryType.RetainerPage4,
            InventoryType.RetainerPage5,
            InventoryType.RetainerPage6,
            InventoryType.RetainerPage7,
            InventoryType.RetainerCrystals
        };

        foreach (var inv in inventories)
        {
            // 容器指標提到迴圈外：原本每次迭代都呼叫兩次 GetInventoryContainer（條件式一次、取格子一次）。
            // 迴圈內唯二會回到遊戲的呼叫（OpenForItemSlot / Callback.Fire）後面都緊接著 return，
            // 沒有任何一輪會在遊戲可能重配容器之後繼續跑，所以提出來不會拿到失效指標。
            var container = InventoryManager.Instance()->GetInventoryContainer(inv);
            // 讀不到就跳過這一頁，當成「這一頁裡沒有這個道具」，最壞情況是回 false 讓呼叫端重試。
            // 反方向極危險：Items 尚未配置時 GetInventorySlot(i) 會回小偏移假指標，
            // 假的 ItemId 若剛好對上就會用那個 i 去 OpenForItemSlot，對錯的格子按下「取回」。
            if (container == null || container->Items == null)
                continue;
            //Svc.Log.Debug($"RETAINER PAGE {inv} WITH SIZE {container->Size}");
            for (int i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null)
                    continue;
                //Svc.Log.Debug($"ITEM {item->ItemId.NameOfItem()} IN {item->Slot}");
                if (item->ItemId == ItemId && ((lookingForHQ && item->Flags == InventoryItem.ItemFlags.HighQuality) || (!lookingForHQ)))
                {
                    quantity = item->Quantity;
                    Svc.Log.Debug($"Found item? {item->Quantity}");
                    // 🔴 這一行原本有三處連續裸解參考,任何一處是 null 都是 AccessViolationException
                    //    (corrupted-state exception,try/catch 攔不到,遊戲直接結束):
                    //    ① AgentInventoryContext.Instance() 是產生器產出的
                    //       「agentModule == null ? null : GetAgentByInternalId(...)」,兩層都能合法回 null;
                    //    ② AgentModule.Instance() 是 UIModule 的轉手,同樣會回 null;
                    //    ③ GetAgentByInternalId 查的是 FixedSizeArray484<Pointer<AgentInterface>>,
                    //       雇員 agent 那一格還沒建立時就是 null。
                    //    判法與 PreCrafting.cs 的裝備流程(451 行起)一致。
                    // fail-closed:取不到就回 false —— 與這個迴圈既有的「這一頁裡沒有這個道具」同義,
                    //    呼叫端本來就會重試(見上面 314 行的註解)。
                    var ag = AgentInventoryContext.Instance();
                    var agentModule = AgentModule.Instance();
                    var retainerAgent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.Retainer);
                    if (ag == null || retainerAgent == null)
                    {
                        Svc.Log.Information($"Artisan: inventory/retainer agent unavailable (ctx={(nint)ag:X}, retainer={(nint)retainerAgent:X}) - not opening the context menu for item {ItemId} this time.");
                        return false;
                    }
                    ag->OpenForItemSlot(inv, i, 0, retainerAgent->GetAddonId());
                    var contextMenu = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextMenu", 1).Address;
                    // 重新取得的一次呼叫,各自判空(下面 358 行起整段都在解參考它)。
                    var contextAgent = AgentInventoryContext.Instance();
                    if (contextAgent == null)
                        return false;
                    var indexOfRetrieveAll = -1;
                    var indexOfRetrieveQuantity = -1;

                    // Addon#98 "Retrieve from Retainer" is the ONLY retrieve entry the game ever places in
                    // the retainer item context menu. Addon#773 "Retrieve Quantity from Retainer" is not a
                    // menu entry at all - it is the caption of the quantity dialog that the game opens by
                    // itself once the selected stack holds more than one item. Verified against the client
                    // data: Addon rows 88-99 are the inventory context-menu block (91 Discard, 92 Split,
                    // 93 Sell, 96 Equip, 97 Entrust to Retainer, 98 Retrieve from Retainer, 99 Put Up for
                    // Sale), while 772/773 live in the quantity-dialog block right next to 889/914 "Select
                    // the desired quantity."; the client binary emits 97+98 together 24 times over and never
                    // emits 773 anywhere near 98.
                    // The old code asked for #773 whenever Quantity > 1, never found it, and therefore fired
                    // no callback at all - the right-click menu opened and nothing was ever retrieved. The
                    // caller (RetainerInfo.ExtractItem/ExtractSingular) already waits for the quantity dialog
                    // and types the amount via InputNumericValue, so selecting #98 completes the flow.
                    // #773 is still preferred when a client really does expose it, so this cannot regress.
                    var retrieveAllText = LuminaSheets.AddonSheet[98].Text.ExtractText().Trim();
                    var retrieveQuantityText = LuminaSheets.AddonSheet[773].Text.ExtractText().Trim();

                    // Index the entries the way the game (and AutoRetainer, which works on this client) does:
                    // the live menu occupies EventParams[ContexItemStartIndex .. +ContextItemCount]. Scanning
                    // all 98 slots and counting only strings also matched stale leftovers from a previously
                    // opened menu, and the resulting counter was not the menu row index the callback wants.
                    var startIndex = Math.Clamp(contextAgent->ContexItemStartIndex, 0, 98);
                    var itemCount = Math.Clamp(contextAgent->ContextItemCount, 0, 98 - startIndex);
                    var labels = new string[itemCount];

                    for (var entry = 0; entry < itemCount; entry++)
                    {
                        var contextObj = contextAgent->EventParams[startIndex + entry];
                        if (contextObj.Type is not FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String
                            and not FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString)
                            continue;

                        var label = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(contextObj.String)).ExtractText().Trim();
                        // 讀窗文字做判定:讀到 U+FFFD 代表選單記憶體正在變動,這一幀不碰(回 false = 下一輪重來)。
                        if (AddonPressGuard.IsTextCorrupt("ContextMenu", label)) return false;
                        labels[entry] = label;

                        if (indexOfRetrieveAll == -1 && retrieveAllText == label) indexOfRetrieveAll = entry;
                        if (indexOfRetrieveQuantity == -1 && retrieveQuantityText == label) indexOfRetrieveQuantity = entry;
                    }

                    Svc.Log.Debug($"Artisan: retainer context menu for item {ItemId} (qty {item->Quantity}) - ContexItemStartIndex={contextAgent->ContexItemStartIndex}, ContextItemCount={contextAgent->ContextItemCount}, disabledMask=0x{contextAgent->ContextItemDisabledMask:X}, ContextMenu addon=0x{(nint)contextMenu:X}");
                    for (var entry = 0; entry < itemCount; entry++)
                    {
                        var contextObj = contextAgent->EventParams[startIndex + entry];
                        Svc.Log.Debug($"Artisan:   entry[{entry}] = EventParams[{startIndex + entry}] type={contextObj.Type} disabled={contextAgent->IsContextItemDisabled(entry)} text=\"{labels[entry] ?? "<not a string>"}\"");
                    }
                    Svc.Log.Debug($"Artisan: Addon#98 \"{retrieveAllText}\" -> index {indexOfRetrieveAll}; Addon#773 \"{retrieveQuantityText}\" -> index {indexOfRetrieveQuantity}");

                    if (contextMenu != null)
                    {
                        // A single item (and crystals/shards, ItemId <= 19) is retrieved outright; a larger
                        // stack makes the game raise the quantity dialog, which the caller then fills in.
                        var index = item->Quantity == 1 || item->ItemId <= 19
                            ? indexOfRetrieveAll
                            : indexOfRetrieveQuantity >= 0 ? indexOfRetrieveQuantity : indexOfRetrieveAll;

                        if (index == -1)
                        {
                            Svc.Log.Warning($"Artisan: couldn't find \"{retrieveAllText}\" in the retainer item context menu, item {ItemId} was not retrieved. " +
                                            $"Menu had {itemCount} entries starting at EventParams[{startIndex}]: {string.Join(" | ", labels.Select(x => x ?? "<not a string>"))}");
                            return true;
                        }

                        if (contextAgent->IsContextItemDisabled(index))
                            Svc.Log.Warning($"Artisan: context menu entry {index} (\"{labels[index]}\") is disabled, retrieving item {ItemId} will probably do nothing.");

                        Svc.Log.Debug($"Artisan: firing retainer context menu entry {index} (\"{labels[index]}\") for item {ItemId}");
                        Callback.Fire(contextMenu, true, 0, index, 0, 0, 0);
                        return true;
                    }
                }
            }
        }
        return false;
    }

    internal static bool InputNumericValue(int value)
    {
        var numeric = (AtkUnitBase*)Svc.GameGui.GetAddonByName("InputNumeric", 1).Address;
        if (numeric != null)
        {
            Svc.Log.Debug($"{value}");
            Callback.Fire(numeric, true, value);
            return true;
        }
        return false;
    }
    internal static bool? ClickCloseEntrustWindow()
    {
        //13530	Close Window
        var text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(13530).Text.ToDalamudString().GetText();
        if (TryGetAddonByName<AtkUnitBase>("RetainerItemTransferProgress", out var addon) && IsAddonReady(addon))
        {
            // 🔴 GetComponent()／GetAsAtkTextNode() 在 FFXIVClientStructs 都是 [MemberFunction] 原生呼叫，
            // 不是受管理的 null-safe 存取器 —— 對空節點呼叫一樣是 AVE。IsEnabled 解的又是 OwnerNode
            // （AtkComponentBase 的 [0xA8]），對 OwnerNode 零空指標檢查。
            // AVE 是 corrupted-state exception，try/catch 攔不到，只能在呼叫／讀取前先驗。
            // 節點與元件一律先取到區域變數再用（原本 NodeList[2] 被重取三次，是 TOCTOU）；
            // 上界也要驗 —— NodeListCount 不足時 NodeList[2] 讀到的是陣列後方的堆積垃圾，
            // 那不是 null，只做判空等於沒擋。任一層驗不過就這一幀不做事，下一輪重來。
            var nodeCount = addon->UldManager.NodeListCount;
            var nodeList = addon->UldManager.NodeList;
            if (nodeCount <= 2 || nodeList == null) return false;
            var progressNode = nodeList[2];
            if (progressNode == null) return false;
            var component = progressNode->GetComponent();
            if (component == null) return false;
            var innerCount = component->UldManager.NodeListCount;
            var innerList = component->UldManager.NodeList;
            if (innerCount <= 2 || innerList == null) return false;
            var labelNode = innerList[2];
            if (labelNode == null) return false;
            var labelTextNode = labelNode->GetAsAtkTextNode();
            if (labelTextNode == null) return false;

            var button = (AtkComponentButton*)component;
            var nodetext = MemoryHelper.ReadSeString(&labelTextNode->NodeText).GetText();
            // 讀窗文字做判定:讀到 U+FFFD 代表窗記憶體正在變動,這一幀不碰。
            if (AddonPressGuard.IsTextCorrupt("RetainerItemTransferProgress", nodetext)) return false;
            // 這顆是關閉鈕,按下窗就關;GenericThrottle 100ms(~6 幀)落在關閉中的窗口內,不是防護。
            // 守衛放在節流之後,同一扇窗只按一次。
            if (nodetext == text && progressNode->IsVisible() && IsComponentEnabled(button) && RetainerInfo.GenericThrottle
                && AddonPressGuard.TryBeginPress("RetainerItemTransferProgress", addon))
            {
                button->ClickAddonButton(addon);
                return true;
            }
        }
        else
        {
            RetainerInfo.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? CloseAgentRetainer()
    {
        // 五層鏈逐節判空(Framework isPointer:true 合法回 null,UIModule/AgentModule/agent 各層皆可 null)。
        // 對照組=AutoRetainer 的 RetainerHandlers.CloseAgentRetainer 同鏈每層都判。取不到=沒有東西要關,回 false。
        var framework = CSFramework.Instance();
        if (framework == null) return false;
        var uiModule = framework->UIModule;
        if (uiModule == null) return false;
        var agentModule = uiModule->GetAgentModule();
        if (agentModule == null) return false;
        var a = agentModule->GetAgentByInternalId(AgentId.Retainer);
        if (a == null) return false;
        if (a->IsAgentActive())
        {
            a->Hide();
            return true;
        }
        return false;
    }

    internal static bool TrySelectSpecificEntry(string text)
    {
        return TrySelectSpecificEntry(new string[] { text });
    }

    internal static bool TrySelectSpecificEntry(IEnumerable<string> text)
    {
        if (TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            // 讀窗文字做判定:任一列讀到 U+FFFD 代表窗記憶體正在變動,這一幀不碰。
            if (GetEntries(addon).Any(x => AddonPressGuard.IsTextCorrupt("SelectString", x))) return false;
            var entry = GetEntries(addon).FirstOrDefault(x => x.StartsWithAny(text));
            if (entry != null)
            {
                var index = GetEntries(addon).IndexOf(entry);
                if (index >= 0 && RetainerInfo.GenericThrottle)
                {
                    new AddonMaster.SelectString((nint)addon).Entries[(ushort)index].Select();
                    return true;
                }
            }
        }
        else
        {
            RetainerInfo.RethrottleGeneric();
        }
        return false;
    }

    internal static List<string> GetEntries(AddonSelectString* addon)
    {
        var list = new List<string>();
        for (int i = 0; i < addon->PopupMenu.PopupMenu.EntryCount; i++)
        {
            list.Add(MemoryHelper.ReadSeStringNullTerminated((nint)addon->PopupMenu.PopupMenu.EntryNames[i].Value).GetText());
        }
        return list;
    }
}