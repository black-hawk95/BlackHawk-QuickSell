using EFT.InventoryLogic;
using System.Collections.Generic;

namespace QuickSell.Patches
{
    /// <summary>
    /// Decides whether an item is somewhere it may be sold from.
    ///
    /// The mod originally rejected anything whose parent chain touched equipment, which meant stash
    /// only. That is safe but blunt: it also excludes loot sitting in a backpack, rig or pockets,
    /// which is what people actually want to sell after a raid.
    ///
    /// This replaces that with a check on WHICH equipment slot the item ultimately lives in.
    ///
    /// The rule is deliberately an allow-list, not a deny-list. Anything that cannot be positively
    /// identified as a permitted location is refused, because selling cannot be undone and
    /// multi-select turns one misclassification into several lost items.
    /// </summary>
    internal static class SellLocation
    {
        // Slot IDs as the game names them. Matched case-insensitively.
        private static readonly HashSet<string> ContainerSlots = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "Backpack",
            "TacticalVest",
            "Pockets",
        };

        // Its own setting, off by default: a secure container holds the things people least want
        // to lose, and selling cannot be undone.
        private const string SecuredContainerSlot = "SecuredContainer";

        // The stash itself.
        private const string StashTemplateId = "55d7217a4bdc2d86028b456d";

        /// <summary>
        /// True when the item may be sold from where it currently sits.
        /// </summary>
        public static bool IsSellable(Item item)
        {
            if (item == null) return false;

            // Never the stash container itself, whatever else is allowed.
            if (item.Parent?.Container?.ParentItem?.TemplateId == StashTemplateId) return false;

            var slot = FindEquipmentSlot(item);
            return Decide(slot);
        }

        private static bool Decide(string slot)
        {
            // Not under equipment at all, so it is in the stash - always allowed, and the
            // behaviour the mod has always had.
            if (slot == null) return true;

            if (SecuredContainerSlot.Equals(slot, System.StringComparison.OrdinalIgnoreCase))
                return Plugin.SellFromSecureContainer;

            if (ContainerSlots.Contains(slot)) return Plugin.SellFromContainers;

            // Anything else - worn gear, weapon slots, special slots, a modded slot, or a slot
            // added in a later game version. Refused, because the safe answer to "I do not know
            // what this is" is no.
            //
            // Worn gear is not supported at all: logging showed the game never builds this kind of
            // context menu for equipped items, so there is nothing to attach an entry to.
            return false;
        }

        /// <summary>
        /// The equipment slot this item ultimately lives in, or null if it is not under equipment.
        ///
        /// Walks up the parent chain rather than checking the immediate parent, because an item can
        /// be nested - a round in a magazine, in a pouch, in a rig.
        /// </summary>
        private static string FindEquipmentSlot(Item item)
        {
            var current = item;
            var guard = 0;

            // A depth limit, because a malformed parent chain must not hang the game. Real nesting
            // never approaches this.
            while (current != null && guard++ < 32)
            {
                if (current.Parent is SlotItemAddress slotAddress)
                {
                    var id = slotAddress.Slot?.ID;

                    // The FIRST slot found is the answer, and logging confirmed that is correct:
                    // for every container the first and outermost slot are the same value.
                    if (!string.IsNullOrEmpty(id)) return id;
                }

                var parentItem = current.Parent?.Container?.ParentItem;
                if (parentItem == null || ReferenceEquals(parentItem, current)) break;

                current = parentItem;
            }

            return null;
        }
    }
}
