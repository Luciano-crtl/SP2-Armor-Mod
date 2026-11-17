using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ArmorFragments
{
    // Like Harmony.PatchAll(), but each patch class is applied on its own.
    // If one fails, it is logged and the rest still work.
    internal static class SafePatcher
    {
        // Takes off the patches that were put on by code of 'assembly', and only those.
        //
        // Not Harmony.UnpatchSelf(): that removes every patch with this Harmony id, and a reload by the ScriptEngine makes the NEW
        // copy of the plugin patch (same id) BEFORE the OLD copy's OnDestroy runs, so UnpatchSelf would take the new patches off too.
        // A patch is identified by the method it runs, which belongs to one copy's assembly. Returns how many patches were removed;
        // 'methods' says on how many game methods.
        public static int UnpatchAll(Harmony harmony, Assembly assembly, out int methods)
        {
            int removed = 0;
            methods = 0;
            foreach (MethodBase original in Harmony.GetAllPatchedMethods().ToList())
            {
                Patches info = Harmony.GetPatchInfo(original);
                if (info == null) continue;

                int before = removed;
                removed += Remove(harmony, original, info.Prefixes, assembly);
                removed += Remove(harmony, original, info.Postfixes, assembly);
                removed += Remove(harmony, original, info.Transpilers, assembly);
                removed += Remove(harmony, original, info.Finalizers, assembly);
                if (removed > before) methods++;
            }
            return removed;
        }

        private static int Remove(Harmony harmony, MethodBase original, IEnumerable<Patch> patches, Assembly assembly)
        {
            int removed = 0;
            foreach (Patch patch in patches.ToList())
            {
                MethodInfo method = patch.PatchMethod;
                if (method == null || method.DeclaringType == null || method.DeclaringType.Assembly != assembly) continue;
                harmony.Unpatch(original, method);
                removed++;
            }
            return removed;
        }

        public static void PatchAll(Harmony harmony, Assembly assembly)
        {
            foreach (Type type in AccessTools.GetTypesFromAssembly(assembly))
            {
                if (type == null || !type.IsDefined(typeof(HarmonyPatch), false)) continue;

                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ArmorFragmentsPlugin.Log.LogInfo("Patch applied: " + type.Name);
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning(
                        "Patch " + type.Name + " skipped (the game may have changed): " +
                        e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message);
                }
            }
        }
    }
}
