using Artisan.CraftingLists;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using ECommons.DalamudServices;
using System;

namespace Artisan.IPC
{
    /// <summary>
    /// Pulls materials out of the currently open retainer by asking AutoRetainer to fire the game's own
    /// retrieve command straight at a slot, instead of driving the retainer window the way
    /// <c>RetainerInfo.ExtractItem</c> does (right-click the stack, find "Retrieve from Retainer" in the
    /// context menu, then type a number into the quantity dialog).
    /// <para/>
    /// The UI route costs roughly half a second per item plus a confirmation dialog, and every step of it is
    /// a place where a mistimed click leaves the chain stuck. The command route is the same one AutoRetainer
    /// uses for its own entrust/vendor work, measured at about 0.13s per slot on the TC client.
    /// <para/>
    /// ⚠️ This is strictly an accelerator. Everything here degrades to <see cref="Progress.FallBackToUi"/>,
    /// and the caller is expected to run the old path when that is set - AutoRetainer may be absent, too old,
    /// or unable to answer, and none of those are errors.
    /// </summary>
    internal static class RetainerDirectFetch
    {
        private const string TagApiVersion = "AutoRetainer.PluginState.GetRetainerItemRetrieveApiVersion";
        private const string TagRetrieve = "AutoRetainer.PluginState.RetrieveRetainerItemSlotById";
        private const string TagQuantity = "AutoRetainer.PluginState.GetOpenRetainerItemQuantity";
        private const string TagResetTracking = "AutoRetainer.PluginState.ResetRetainerRetrieveTracking";

        /// <summary>Lowest AutoRetainer-side API version whose contract matches the result codes below.</summary>
        private const int RequiredApiVersion = 1;

        // Result codes returned by AutoRetainer.PluginState.RetrieveRetainerItemSlotById. Anything above zero
        // is "a command was fired at a slot holding this many". 🔴 0 and -1 must stay distinguishable: 0 means
        // the retainer's storage was walked end to end and does not hold the item, -1 means it could not be
        // walked at all (retainer inventory is only populated once the window has actually been opened). If
        // those were collapsed into one falsey answer, a retainer that was merely still loading would be
        // silently written off as empty.
        internal const int ResultNotPresent = 0;
        internal const int ResultRetainerUnavailable = -1;
        internal const int ResultCommandInFlight = -2;
        internal const int ResultInventoryFull = -3;
        internal const int ResultBlockedUnique = -4;
        internal const int ResultInCrystals = -5;

        /// <summary>Give up on an item once nothing has arrived for this long. Generous on purpose: the
        /// retrieve is a real server round trip, and AutoRetainer will not re-fire at a slot whose command it
        /// has not seen land yet, so short stretches of "-2, nothing yet" are the normal case rather than a
        /// fault.</summary>
        private const int NoProgressMs = 8000;

        /// <summary>Hard ceiling for one item on one retainer, however well it is going.</summary>
        private const int OverallDeadlineMs = 60000;

        /// <summary>
        /// Much shorter deadline for the one case that is not "the server is being slow": every single query
        /// answered "cannot look at the retainer's storage" and no command has ever gone out. That is a
        /// standing condition - almost always no retainer window is open - not a transient one, so waiting out
        /// <see cref="NoProgressMs"/> buys nothing. Generous enough to cover a window that is merely slow to
        /// open; the old UI path gives itself 500ms plus retries for the same thing.
        /// </summary>
        private const int UnavailableGiveUpMs = 3000;

        /// <summary>
        /// How many items in a row may fail that way before the direct path stands down for the rest of the
        /// restock. Two rather than one on purpose: a single slow-opening retainer window should cost one
        /// item's fallback, not the whole round's accelerator.
        /// </summary>
        private const int SuspendAfterUnavailableItems = 2;

        /// <summary>How long to wait before asking again after AutoRetainer failed to answer.</summary>
        private const int ProbeBackoffMs = 15000;

        private static ICallGateSubscriber<int>? _apiVersion;
        private static ICallGateSubscriber<uint, bool, bool, int>? _retrieve;
        private static ICallGateSubscriber<uint, bool, bool, int>? _quantity;
        private static ICallGateSubscriber<object>? _resetTracking;

        private static bool _available;
        private static long _nextProbeAt;
        private static bool _loggedUnavailable;
        private static bool _suspendedForRound;
        private static int _consecutiveUnavailableItems;

        /// <summary>
        /// Stands the direct path down for the remainder of the current restock, so the items still to come
        /// go straight to the retainer-window path instead of each discovering the same thing on its own
        /// clock. Without this a 60-item list pays the per-item deadline sixty times over.
        /// <para/>
        /// ⚠️ Only the accelerator is suspended. The retainer-window path is untouched and remains the thing
        /// that actually fetches the materials.
        /// </summary>
        private static void SuspendForRound(string why)
        {
            if (_suspendedForRound) return;
            _suspendedForRound = true;
            Svc.Log.Information($"[Artisan][Restock] Direct retainer retrieval is standing down for the rest of this restock: {why}. " +
                                $"The remaining items go straight to the retainer-window path rather than each waiting out its own timeout.");
        }

        /// <summary>Re-arms the direct path at the head of every restock chain, so a suspension only ever
        /// lasts for the run that caused it.</summary>
        internal static void BeginRound()
        {
            _suspendedForRound = false;
            _consecutiveUnavailableItems = 0;
        }

        /// <summary>
        /// Whether AutoRetainer is present and exposes a retrieve API this code understands. Re-probed on a
        /// backoff rather than cached forever, so installing or updating AutoRetainer mid-session is picked up
        /// without a restart, and so a plugin that unloads mid-chain does not leave this stuck on true.
        /// </summary>
        internal static bool Available
        {
            get
            {
                if (!P.Config.UseDirectRetainerRetrieval) return false;
                // Checked before the cached capability flag: a round that has proved the command path cannot
                // run must fall through to the window path immediately, with no IPC and no waiting.
                if (_suspendedForRound) return false;
                if (_available) return true;
                if (Environment.TickCount64 < _nextProbeAt) return false;
                _nextProbeAt = Environment.TickCount64 + ProbeBackoffMs;

                try
                {
                    _apiVersion ??= Svc.PluginInterface.GetIpcSubscriber<int>(TagApiVersion);
                    var version = _apiVersion.InvokeFunc();
                    _available = version >= RequiredApiVersion;
                    if (_available)
                    {
                        Svc.Log.Information($"[Artisan][Restock] AutoRetainer direct retrieval available (API v{version}); retainer restocking will use the command path instead of the retainer window.");
                        _loggedUnavailable = false;
                    }
                    else
                    {
                        LogUnavailableOnce($"AutoRetainer reports retrieve API v{version}, this build needs v{RequiredApiVersion} or newer");
                    }
                    return _available;
                }
                catch (Exception e)
                {
                    _available = false;
                    LogUnavailableOnce($"AutoRetainer did not answer {TagApiVersion} ({e.GetType().Name})");
                    return false;
                }
            }
        }

        private static void LogUnavailableOnce(string why)
        {
            if (_loggedUnavailable) return;
            _loggedUnavailable = true;
            Svc.Log.Information($"[Artisan][Restock] Direct retainer retrieval unavailable: {why}. Falling back to driving the retainer window, which is slower but works on its own.");
        }

        private static void MarkUnavailable(Exception e)
        {
            _available = false;
            _nextProbeAt = Environment.TickCount64 + ProbeBackoffMs;
            _loggedUnavailable = false;
            LogUnavailableOnce($"an IPC call threw ({e.GetType().Name}: {e.Message})");
        }

        /// <summary>Fires one retrieve command at a slot holding the item. Returns the quantity that slot held
        /// (always positive) when a command went out, one of the Result* codes when it did not, or null when
        /// AutoRetainer could not be reached at all.</summary>
        private static int? Retrieve(uint itemId, bool hqOnly)
        {
            if (!Available) return null;
            try
            {
                _retrieve ??= Svc.PluginInterface.GetIpcSubscriber<uint, bool, bool, int>(TagRetrieve);
                // includeCrystals is deliberately false. Crystals are the one category the game is known to
                // always ask a quantity for, and an unanswered quantity dialog would park this loop until its
                // deadline; AutoRetainer reports ResultInCrystals instead of "not present" so the caller can
                // hand those to the UI path, which does know how to answer that dialog.
                return _retrieve.InvokeFunc(itemId, hqOnly, false);
            }
            catch (Exception e)
            {
                MarkUnavailable(e);
                return null;
            }
        }

        /// <summary>How many of the item the open retainer holds, or -1 when that cannot be determined.
        /// ⚠️ -1 is "unknown", not "none".</summary>
        internal static int RetainerQuantity(uint itemId, bool hqOnly)
        {
            if (!Available) return -1;
            try
            {
                _quantity ??= Svc.PluginInterface.GetIpcSubscriber<uint, bool, bool, int>(TagQuantity);
                return _quantity.InvokeFunc(itemId, hqOnly, false);
            }
            catch (Exception e)
            {
                MarkUnavailable(e);
                return -1;
            }
        }

        /// <summary>Tells AutoRetainer to forget which slots it has already fired at, so a slot the server
        /// refused is offered again immediately instead of waiting out its staleness timeout. Optional - the
        /// tracking expires on its own - so failure here is not worth reacting to.</summary>
        internal static void ResetTracking()
        {
            if (!Available) return;
            try
            {
                _resetTracking ??= Svc.PluginInterface.GetIpcSubscriber<object>(TagResetTracking);
                _resetTracking.InvokeAction();
            }
            catch (Exception e)
            {
                MarkUnavailable(e);
            }
        }

        /// <summary>
        /// One item being pulled off one retainer. Progress is measured by watching the player's own bags
        /// rather than by counting fired commands, because a command the server refuses changes nothing and
        /// would otherwise be counted as a success - the retainer would be left holding materials the list
        /// then fails for.
        /// </summary>
        internal sealed class Progress
        {
            internal readonly uint ItemId;
            internal readonly bool HqOnly;
            internal readonly int Wanted;

            /// <summary>Bag count when the first readable frame arrived, -1 until then. Nothing is retrieved
            /// before this is known, otherwise the arrivals could not be told apart from what was already
            /// there.</summary>
            private int Baseline = -1;
            private int LastSeenOwned = -1;

            private readonly long StartedAt = Environment.TickCount64;
            private long LastProgressAt = Environment.TickCount64;
            private long LastCommandAt;
            private int CommandsFired;
            private int QuantityCommanded;
            private bool Finished;

            /// <summary>How many times AutoRetainer answered "cannot look at the retainer's storage".</summary>
            private int UnavailableAnswers;

            /// <summary>Set the moment any answer other than "unavailable" comes back, which proves the
            /// retainer's storage really is readable. Until then the short deadline applies.</summary>
            private bool SawReadableRetainer;

            /// <summary>Set when this item still needs the retainer-window path - either because nothing was
            /// retrieved and it is not clear the retainer is empty, or because the item lives somewhere the
            /// command path deliberately does not touch. The caller must run the old path when this is set,
            /// otherwise the item is silently left behind.</summary>
            internal bool FallBackToUi { get; private set; }

            /// <summary>How many actually landed in the player's bags. 0 when nothing could be measured.</summary>
            internal int Gained => Baseline < 0 || LastSeenOwned < 0 ? 0 : Math.Max(0, LastSeenOwned - Baseline);

            internal Progress(uint itemId, bool hqOnly, int wanted)
            {
                ItemId = itemId;
                HqOnly = hqOnly;
                Wanted = wanted;
            }

            /// <summary>
            /// Advances one frame. Returns true when this item is done on this retainer (check
            /// <see cref="FallBackToUi"/>), false to be called again.
            /// <para/>
            /// 🔴 Never returns null: the TaskManager reads a null result as "abort the whole chain", which
            /// would drop the trailing tasks that close the retainer window and un-suppress AutoRetainer.
            /// </summary>
            internal bool Step()
            {
                if (Finished) return true;
                var now = Environment.TickCount64;

                var owned = OwnedOrUnknown(ItemId);
                if (owned >= 0)
                {
                    if (Baseline < 0)
                    {
                        Baseline = owned;
                        LastSeenOwned = owned;
                    }
                    else if (owned > LastSeenOwned)
                    {
                        LastSeenOwned = owned;
                        LastProgressAt = now;
                    }

                    if (Gained >= Wanted) return Finish("wanted amount reached", false);
                }
                else if (Baseline < 0)
                {
                    // Cannot tell what is already in the bags, so an arrival could not be recognised either.
                    // Wait it out rather than firing blind; the deadlines below are the way out.
                    return CheckDeadlines(now);
                }

                if (CheckDeadlines(now)) return true;

                var result = Retrieve(ItemId, HqOnly);
                if (result == null) return Finish("AutoRetainer stopped answering", true);

                switch (result.Value)
                {
                    case > 0:
                        SawReadableRetainer = true;
                        CommandsFired++;
                        QuantityCommanded += result.Value;
                        Svc.Log.Information($"[Artisan][Restock][direct] item {ItemId}: retrieve command #{CommandsFired} fired at a slot of {result.Value} " +
                                            $"({(LastCommandAt == 0 ? 0 : now - LastCommandAt)}ms since the previous command, {now - StartedAt}ms into this item, " +
                                            $"{Gained} of {Wanted} arrived so far).");
                        LastCommandAt = now;
                        return false;

                    case ResultNotPresent:
                        // Proved absent, not merely unreadable - nothing more to get here.
                        SawReadableRetainer = true;
                        return Finish("retainer holds no more of it", false);

                    case ResultCommandInFlight:
                        // The item is there and a command is on its way; the storage was clearly readable.
                        SawReadableRetainer = true;
                        return false;

                    case ResultRetainerUnavailable:
                        // "Could not look" - which is not "not there". Kept separate from the in-flight case
                        // above so the short deadline can tell a retainer that is merely still loading from
                        // one whose window was never opened at all.
                        UnavailableAnswers++;
                        return false;

                    case ResultInventoryFull:
                        // AutoRetainer's own reserve-slot setting, which Artisan users never opted into and
                        // which the window path does not honour. Handing back rather than stopping keeps the
                        // old behaviour available: withdrawing an exact amount can still merge into a partial
                        // stack that a whole-slot retrieve has no room for.
                        return Finish("player bags are at AutoRetainer's reserve limit", true);

                    case ResultBlockedUnique:
                        // Deliberately no fallback: the game refuses to hand over a unique item the player
                        // already owns, every time, so the window path would spend several seconds arriving
                        // at the same refusal. Crafting materials are never unique, so this should not fire
                        // during a normal restock at all - if it does, the log line above is the evidence.
                        return Finish("unique item the player already owns - the game always refuses these", false);

                    case ResultInCrystals:
                        return Finish("held in the crystal container, which the command path does not touch", true);

                    default:
                        return Finish($"unrecognised result {result.Value} - treating as unsupported", true);
                }
            }

            private bool CheckDeadlines(long now)
            {
                // Every answer so far has been "cannot look", and nothing was ever sent. Deliberately requires
                // at least one such answer rather than just an absence of progress, so that an item held up by
                // unreadable *player* bags (zoning) is not mistaken for an unusable retainer.
                if (UnavailableAnswers > 0 && !SawReadableRetainer && CommandsFired == 0 && now - StartedAt > UnavailableGiveUpMs)
                {
                    if (++_consecutiveUnavailableItems >= SuspendAfterUnavailableItems)
                        SuspendForRound($"{_consecutiveUnavailableItems} items in a row found the retainer's storage unreadable, which normally means no retainer window is open");
                    return Finish($"the retainer's storage stayed unreadable for {UnavailableGiveUpMs}ms and nothing could be sent", true);
                }

                if (now - LastProgressAt > NoProgressMs)
                    return Finish($"nothing arrived for {NoProgressMs}ms", true);
                if (now - StartedAt > OverallDeadlineMs)
                    return Finish($"hit the {OverallDeadlineMs}ms ceiling for one item", true);
                return false;
            }

            private bool Finish(string reason, bool fallBackToUi)
            {
                Finished = true;
                // Any item that got a real answer clears the streak: the run of unreadable items has to be
                // consecutive before the whole round stands down.
                if (SawReadableRetainer) _consecutiveUnavailableItems = 0;
                // Only ask for the slow path when it could still achieve something. Falling back after the
                // wanted amount already arrived would just make the retainer window dance for nothing.
                FallBackToUi = fallBackToUi && Gained < Wanted;
                var elapsed = Environment.TickCount64 - StartedAt;
                // On a shortfall, say what is still sitting on the retainer: "40 of 60 arrived" on its own
                // does not tell anyone whether the other 20 exist. ⚠️ Printed as "?" when unknown rather than
                // as 0 - a 0 there would read as "the retainer is empty, nothing was missed".
                var stillOnRetainer = Gained < Wanted ? RetainerQuantity(ItemId, HqOnly) : 0;
                Svc.Log.Information($"[Artisan][Restock][direct] item {ItemId} done: {reason}. " +
                                    $"{CommandsFired} command(s) covering {QuantityCommanded}, {Gained} of {Wanted} arrived, {elapsed}ms" +
                                    // Named outright rather than left to be inferred from "0 commands": these
                                    // two say completely different things about whose fault it is.
                                    $"{(UnavailableAnswers > 0 ? $", {UnavailableAnswers} \"retainer storage unreadable\" answer(s)" : "")}" +
                                    $"{(CommandsFired > 0 ? $" ({elapsed / CommandsFired}ms per command)" : "")}." +
                                    $"{(Gained < Wanted ? $" Retainer still holds {(stillOnRetainer < 0 ? "?" : stillOnRetainer.ToString())}." : "")}" +
                                    $"{(FallBackToUi ? " Handing the remainder to the retainer-window path." : "")}");
                return true;
            }
        }

        /// <summary>
        /// How many of the item are in the player's bags, or -1 when that genuinely cannot be read.
        /// <para/>
        /// ⚠️ Distinguishing the two matters here: <c>GetInventoryItemCount</c> reports 0 while zoning rather
        /// than failing, and a 0 taken at face value would look like "none of it arrived" and burn the
        /// no-progress deadline, or worse, be used as a baseline and make every later arrival invisible.
        /// </summary>
        private static int OwnedOrUnknown(uint itemId)
        {
            if (!Svc.ClientState.IsLoggedIn) return -1;
            if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return -1;
            return CraftingListUI.NumberOfIngredient(itemId);
        }
    }
}
