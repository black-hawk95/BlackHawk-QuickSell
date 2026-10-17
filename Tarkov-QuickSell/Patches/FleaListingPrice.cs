using EFT.InventoryLogic;
using System;

namespace QuickSell.Patches
{
    /// <summary>
    /// One source of truth for the flea price QuickSell will actually list at.
    ///
    /// The flea cache stores a market price per template. EFT consumables can be partially used,
    /// so the same template price must be scaled by the item's remaining resource before it is
    /// shown or submitted. The user-configured listing percentage is applied last.
    ///
    /// Stack count is intentionally separate from the unit listing price because EFT's flea offer
    /// requirement is a per-unit price for stackable items (ammo, currency-like stacks, etc.).
    /// </summary>
    internal static class FleaListingPrice
    {
        public static double GetQualityModifier(Item item)
        {
            if (item == null) return 1d;

            // Medkits / healing items with a remaining HP resource.
            var medKit = item.GetItemComponent<MedKitComponent>();
            if (medKit != null)
                return Normalize(medKit.RelativeValue);

            // Food and drinks with a remaining resource value.
            var foodDrink = item.GetItemComponent<FoodDrinkComponent>();
            if (foodDrink != null)
                return Normalize(foodDrink.RelativeValue);

            // Generic resource items. This also covers modded consumables that use EFT's normal
            // ResourceComponent instead of one of the specialised components above.
            var resource = item.GetItemComponent<ResourceComponent>();
            if (resource != null && resource.MaxResource > 0)
                return Normalize(resource.Value / resource.MaxResource);

            // Repair kits expose their resource directly on the item rather than through the
            // generic ResourceComponent.
            if (item is RepairKit repairKit && repairKit.MaxRepairResource > 0)
                return Normalize(repairKit.Resource / repairKit.MaxRepairResource);

            return 1d;
        }

        /// <summary>Raw current-value total for this actual stack, before the F12 percentage.</summary>
        public static double GetRawStackValue(Item item, double marketUnitPrice)
        {
            if (item == null || marketUnitPrice <= 0d) return 0d;

            var unitValue = marketUnitPrice * GetQualityModifier(item);
            return unitValue * Math.Max(1, item.StackObjectsCount);
        }

        /// <summary>Per-unit price submitted to EFT when QuickSell creates a flea offer.</summary>
        public static int GetListingUnitPrice(Item item, double marketUnitPrice)
        {
            if (item == null || marketUnitPrice <= 0d) return 0;

            var adjusted = marketUnitPrice
                * GetQualityModifier(item)
                * Plugin.AvgPricePercent / 100d;

            // A valid flea requirement cannot be zero. Fully depleted items are normally rejected
            // by EFT before this point, but keeping a floor makes modded resource items safe too.
            return Math.Max(1, (int)Math.Ceiling(adjusted));
        }

        private static double Normalize(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 1d;
            if (value <= 0d) return 0.01d;
            if (value >= 1d) return 1d;
            return value;
        }
    }
}
