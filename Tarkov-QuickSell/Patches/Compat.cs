using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace QuickSell.Patches
{
    /// <summary>
    /// Resolves game members that were renamed by the 4.1 deobfuscation, and that are private
    /// so they cannot simply be accessed directly.
    ///
    /// Everything here is looked up by FIELD TYPE rather than by name. A name is a guess that
    /// breaks on the next game build; "the one field on Trader whose type is ITradingSession"
    /// is a structural fact that survives renames.
    /// </summary>
    internal static class Compat
    {
        // Keyed on the type pair directly rather than on a formatted string. The old version built
        // an interpolated string on EVERY call including cache hits, which meant an allocation per
        // frame on the keybind path. A tuple key allocates nothing.
        private static readonly Dictionary<(Type Owner, Type Field), FieldInfo> Cache = new();

        private const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>
        /// Finds the field on <paramref name="owner"/> whose type is assignable to
        /// <paramref name="fieldType"/>. If several match, <paramref name="preferredName"/> breaks
        /// the tie. Returns null and logs if nothing matches.
        /// </summary>
        public static FieldInfo Field(Type owner, Type fieldType, string preferredName = null)
        {
            var key = (owner, fieldType);
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var matches = owner.GetFields(Flags)
                .Where(f => fieldType.IsAssignableFrom(f.FieldType))
                .ToArray();

            FieldInfo found;
            if (matches.Length == 1)
            {
                found = matches[0];
            }
            else if (matches.Length > 1)
            {
                found = (preferredName != null ? matches.FirstOrDefault(f => f.Name == preferredName) : null)
                        ?? matches[0];
                Plugin.LogSource?.LogWarning(
                    $"QuickSell: {matches.Length} fields of type {fieldType.Name} on {owner.Name} " +
                    $"({string.Join(", ", matches.Select(m => m.Name))}); using '{found.Name}'.");
            }
            else
            {
                found = null;
                Plugin.LogSource?.LogError(
                    $"QuickSell: no field of type {fieldType.Name} found on {owner.Name}. " +
                    "The game build changed; this feature will not work.");
            }

            Cache[key] = found;
            return found;
        }

        /// <summary>Reads a field of type <typeparamref name="T"/> off an instance, or null.</summary>
        public static T Get<T>(object instance, string preferredName = null) where T : class
        {
            if (instance == null) return null;
            var field = Field(instance.GetType(), typeof(T), preferredName);
            return field?.GetValue(instance) as T;
        }

        /// <summary>
        /// Same as <see cref="Get{T}"/> but looks the field up on a fixed declaring type rather
        /// than the runtime type. Use for generic types where the runtime type is constructed.
        /// </summary>
        public static T GetOn<T>(Type owner, object instance, string preferredName = null) where T : class
        {
            if (instance == null) return null;
            var field = Field(owner, typeof(T), preferredName);
            return field?.GetValue(instance) as T;
        }
    }
}
