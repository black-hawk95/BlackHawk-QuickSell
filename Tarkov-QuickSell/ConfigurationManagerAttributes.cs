using BepInEx.Configuration;
using System;

namespace QuickSell
{
    /// <summary>
    /// Tells the BepInEx Configuration Manager how to draw a setting in the F12 menu.
    ///
    /// This is the standard template class that plugins are expected to copy in rather than
    /// reference - the upstream documentation states as much, and it is why the fields are matched
    /// by NAME through reflection at runtime. That has two consequences worth knowing:
    ///
    ///  - The mod carries no dependency on Configuration Manager. If the user does not have it
    ///    installed, this class is simply never read and the settings fall back to default drawing.
    ///  - Only the fields actually used are declared here. Unused fields behave identically to
    ///    null ones, so a trimmed copy is equivalent to the full template.
    /// </summary>
#pragma warning disable 0169, 0414, 0649
    internal sealed class ConfigurationManagerAttributes
    {
        /// <summary>
        /// Replaces the default editor for this setting with a custom one. Used here to draw a
        /// button rather than the checkbox a bool would otherwise get.
        /// </summary>
        public Action<ConfigEntryBase> CustomDrawer;

        /// <summary>
        /// Display order within the section. Higher values are shown first; settings without an
        /// order are listed after those with one.
        /// </summary>
        public int? Order;

        /// <summary>Hides the default value hint next to the setting.</summary>
        public bool? HideDefaultButton;

        /// <summary>Hides the setting's description text.</summary>
        public bool? HideSettingName;
    }
#pragma warning restore 0169, 0414, 0649
}
