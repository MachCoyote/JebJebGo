using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace JebJebGo
{
    /// <summary>
    /// "Janitor's Closet Filter Fix" (setting, default on). Janitor's Closet's mod
    /// filter window adds exclude filters to the parts list ("Mod Filter", "Size
    /// Filter", "Resource Filter", "Resource Exclude Filter", "Module Filter",
    /// "Module Exclude Filter") that walk every filter button for every part on
    /// every refresh: about 3.6 s per tab switch or search on a 4,900-part
    /// install. Their answer for a part depends only on the window's buttons
    /// (enabled and inverse states) and on its per-button part sets, so this
    /// replaces each of those filters with a wrapper that remembers Janitor's
    /// Closet's own answer per part and forgets them all whenever a signature of
    /// those inputs changes (checked at the start of each refresh and once per
    /// frame). Results are Janitor's Closet's, never recomputed by JebJebGo.
    ///
    /// Patched member: EditorPartList.Refresh() (prefix, Priority.First) to wrap
    /// new Janitor's Closet filters and refresh the signature. Without Janitor's
    /// Closet, or if its fields cannot be read, nothing is wrapped (logged once).
    /// </summary>
    internal static class JanitorsClosetFilterFix
    {
        private static readonly string[] SlowFilterIds =
        {
            "Mod Filter", "Size Filter", "Resource Filter", "Resource Exclude Filter", "Module Filter", "Module Exclude Filter",
        };

        private static readonly string[] ButtonFields = { "modButtons", "sizeButtons", "resourceButtons", "partModuleButtons" };
        private static readonly string[] PartSetFields = { "modHash", "sizeHash", "resourceHash", "partModuleHash" };

        private static FieldInfo[] buttonFields;
        private static FieldInfo[] partSetFields;
        private static FieldInfo enabledStateField;
        private static FieldInfo inverseField;
        private static bool available;
        private static long signature;
        private static int signatureFrame = -1;

        /// <summary>Whether the fix is in effect now: Janitor's Closet found and the setting on.</summary>
        public static bool Active => available && JebJebGoStatus.JanitorsClosetFilterFix;

        public static void Install(Harmony harmony)
        {
            Type window = AccessTools.TypeByName("JanitorsCloset.ModFilterWindow");
            if (window == null)
            {
                return; // Janitor's Closet not installed.
            }
            try
            {
                buttonFields = Fields(window, ButtonFields);
                partSetFields = Fields(window, PartSetFields);
                Type toggleState = buttonFields[0].FieldType.GetGenericArguments()[1];
                enabledStateField = AccessTools.Field(toggleState, "enabledState") ?? throw new MissingFieldException(toggleState.FullName, "enabledState");
                inverseField = AccessTools.Field(toggleState, "inverse") ?? throw new MissingFieldException(toggleState.FullName, "inverse");
                ComputeSignature();
                harmony.Patch(AccessTools.Method(typeof(EditorPartList), "Refresh", Type.EmptyTypes)
                        ?? throw new MissingMethodException("EditorPartList.Refresh() not found"),
                    prefix: new HarmonyMethod(typeof(JanitorsClosetFilterFix), nameof(RefreshPrefix)) { priority = Priority.First });
                available = true;
                Log.Info("Janitor's Closet found: Janitor's Closet Filter Fix ready (setting " + (JebJebGoStatus.JanitorsClosetFilterFix ? "on" : "off") + ")");
            }
            catch (Exception error)
            {
                available = false;
                Log.Warning("Janitor's Closet Filter Fix unavailable (this Janitor's Closet version differs); its filters run unchanged: " + error.Message);
            }
        }

        public static void RefreshPrefix(EditorPartList __instance)
        {
            if (!available)
            {
                return;
            }
            try
            {
                signatureFrame = -1; // a refresh may follow a filter change in the same frame
                WrapNewFilters(__instance.ExcludeFilters);
            }
            catch (Exception error)
            {
                available = false;
                Log.Error("Janitor's Closet Filter Fix failed; Janitor's Closet's filters run unchanged from now on: " + error);
            }
        }

        private static FieldInfo[] Fields(Type type, string[] names)
        {
            FieldInfo[] fields = new FieldInfo[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                fields[i] = AccessTools.Field(type, names[i]) ?? throw new MissingFieldException(type.FullName, names[i]);
                if (!fields[i].IsStatic || !typeof(IDictionary).IsAssignableFrom(fields[i].FieldType))
                {
                    throw new MissingFieldException(type.FullName, names[i] + " (static dictionary)");
                }
            }
            return fields;
        }

        private static void WrapNewFilters(EditorPartListFilterList<AvailablePart> filters)
        {
            List<EditorPartListFilter<AvailablePart>> toWrap = null;
            for (int i = 0; i < filters.Count; i++)
            {
                EditorPartListFilter<AvailablePart> filter = filters[i];
                Func<AvailablePart, bool> criteria = filter.FilterCriteria;
                if (criteria == null || criteria.Target is CachedFilter || Array.IndexOf(SlowFilterIds, filter.ID) < 0
                    || criteria.Method.DeclaringType?.Assembly.GetName().Name != "JanitorsCloset")
                {
                    continue;
                }
                (toWrap ?? (toWrap = new List<EditorPartListFilter<AvailablePart>>())).Add(filter);
            }
            if (toWrap == null)
            {
                return;
            }
            // Exclude filters combine with AND, so their order does not change the result.
            foreach (EditorPartListFilter<AvailablePart> filter in toWrap)
            {
                CachedFilter cached = new CachedFilter(filter.FilterCriteria);
                filters.RemoveFilter(filter);
                filters.AddFilter(new EditorPartListFilter<AvailablePart>(filter.ID, cached.Evaluate, filter.criteriaFailMessage));
                JebJebGoStatus.JanitorsClosetFiltersWrapped++;
            }
            Log.Info("Janitor's Closet Filter Fix: remembering the answers of " + toWrap.Count + " Janitor's Closet filters");
        }

        /// <summary>The inputs of the wrapped filters, folded into one number (once per frame and per refresh).</summary>
        private static long CurrentSignature()
        {
            if (signatureFrame != Time.frameCount)
            {
                signatureFrame = Time.frameCount;
                long previous = signature;
                ComputeSignature();
                if (signature != previous)
                {
                    JebJebGoStatus.JanitorsClosetCacheResets++;
                }
            }
            return signature;
        }

        private static void ComputeSignature()
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                for (int f = 0; f < buttonFields.Length; f++)
                {
                    IDictionary buttons = (IDictionary)buttonFields[f].GetValue(null);
                    hash = Mix(hash, buttons == null ? 0 : RuntimeHelpers.GetHashCode(buttons));
                    if (buttons == null)
                    {
                        continue;
                    }
                    hash = Mix(hash, buttons.Count);
                    foreach (DictionaryEntry entry in buttons)
                    {
                        hash = Mix(hash, entry.Key.GetHashCode());
                        object state = entry.Value;
                        int bits = state == null ? 3 : ((bool)enabledStateField.GetValue(state) ? 1 : 0) | ((bool)inverseField.GetValue(state) ? 2 : 0);
                        hash = Mix(hash, bits);
                    }
                }
                for (int f = 0; f < partSetFields.Length; f++)
                {
                    IDictionary sets = (IDictionary)partSetFields[f].GetValue(null);
                    hash = Mix(hash, sets == null ? 0 : RuntimeHelpers.GetHashCode(sets));
                    if (sets == null)
                    {
                        continue;
                    }
                    hash = Mix(hash, sets.Count);
                    foreach (DictionaryEntry entry in sets)
                    {
                        hash = Mix(hash, entry.Value == null ? 0 : RuntimeHelpers.GetHashCode(entry.Value));
                        hash = Mix(hash, entry.Value is ICollection set ? set.Count : -1);
                    }
                }
                signature = hash;
            }
        }

        private static long Mix(long hash, int value)
        {
            unchecked
            {
                return (hash ^ value) * 1099511628211L;
            }
        }

        /// <summary>One wrapped Janitor's Closet filter: its own answers, remembered per part until the inputs change.</summary>
        internal sealed class CachedFilter
        {
            private readonly Dictionary<AvailablePart, bool> answers = new Dictionary<AvailablePart, bool>();
            private long answersSignature;
            private bool wasActive;

            public CachedFilter(Func<AvailablePart, bool> original)
            {
                Original = original;
            }

            /// <summary>Janitor's Closet's filter delegate (for attribution in reports).</summary>
            public Func<AvailablePart, bool> Original { get; }

            public bool Evaluate(AvailablePart part)
            {
                if (!Active)
                {
                    wasActive = false;
                    return Original(part);
                }
                long current;
                try
                {
                    current = CurrentSignature();
                }
                catch (Exception error)
                {
                    available = false;
                    Log.Error("Janitor's Closet Filter Fix failed; Janitor's Closet's filters run unchanged from now on: " + error.Message);
                    return Original(part);
                }
                if (!wasActive || current != answersSignature)
                {
                    answers.Clear();
                    answersSignature = current;
                    wasActive = true;
                }
                if (part != null && answers.TryGetValue(part, out bool answer))
                {
                    return answer;
                }
                answer = Original(part);
                if (part != null)
                {
                    answers[part] = answer;
                }
                return answer;
            }
        }
    }
}
