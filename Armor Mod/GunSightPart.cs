using System;
using System.Xml.Linq;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Design;
using HarmonyLib;

namespace ArmorFragments
{
    // The Gun Sight part: the game's Camera part (its model, attach points and first-person view) as a part type of its own, listed in the
    // designer's Weapons beside the Tank Cannon, with none of the camera's settings. Placed on a tank, it is the eye of a gun's sniper view
    // (picked on the cannon: Gun sight > Pick camera sight). The game adds a mod's parts through PartTypeList.LoadModPart.
    // A craft that has one can only be opened with the mod installed (the game does not know the part type without it).
    internal static class GunSightPart
    {
        public const string PartTypeId = "ArmorFragments-GunSight-1";
        public const string Name = "Gun Sight";

        // Whether the game already has the part type (its lookup throws for an unknown id).
        public static bool Known(PartTypeList types)
        {
            try { return types.GetPartType(PartTypeId) != null; }
            catch (Exception) { return false; }
        }

        public static bool Is(PartData part)
        {
            return part != null && part.PartType != null && part.PartType.PartTypeId == PartTypeId;
        }

        // When the plugin was loaded after the game had read its part types (a reload while the game runs), the part type is added here.
        private static bool registered;
        private static float nextCheck;

        public static void Tick()
        {
            if (registered || UnityEngine.Time.unscaledTime < nextCheck) return;
            nextCheck = UnityEngine.Time.unscaledTime + 2f;
            try
            {
                Assets.Scripts.Game game = Assets.Scripts.Game.Instance;
                PartTypeList types = game != null ? game.PartTypes : null;
                if (types == null) return;
                if (!Known(types))
                {
                    types.LoadModPart(PartTypeXml(), null);
                    ArmorFragmentsPlugin.Log.LogInfo("Gun Sight part added to the game's part types (late)");
                }
                registered = true;
            }
            catch (Exception e)
            {
                registered = true;
                ArmorFragmentsPlugin.Log.LogWarning("Gun Sight part: couldn't add the part type: " + e.Message);
            }
        }

        // A copy of the game's Camera-1 part type (Data/Parts/PartTypes), with its own id and name.
        public static XElement PartTypeXml()
        {
            return XElement.Parse(
                "<PartType id='" + PartTypeId + "' name='" + Name + "' prefabId='Camera' mass='0.5' dragType='None' constantDrag='0.0' sharesRigidBody='true' " +
                "damageDisconnect='false' defaultMaterials='10,-3' combineMeshes='false' underwaterDragScalar='0' performanceCost='3.8'>" +
                "<Modifiers><CameraVantage supportedViewModes='FirstPerson' /><CockpitSound /></Modifiers>" +
                "<AttachPoints>" +
                "<AttachPoint position='0,-0.05,0' normal='0,-1,0' adaptiveIgnore='true' seekType='Wing' allowRotation='true' />" +
                "<AttachPoint position='0,0,0' normal='0,-1,0' adaptiveIgnore='true' allowRotation='true' />" +
                "</AttachPoints></PartType>");
        }
    }

    [HarmonyPatch(typeof(PartTypeList), "LoadXml")]
    internal static class GunSightPartTypePatch
    {
        private static void Postfix(PartTypeList __instance)
        {
            try
            {
                if (GunSightPart.Known(__instance)) return;
                __instance.LoadModPart(GunSightPart.PartTypeXml(), null);
                ArmorFragmentsPlugin.Log.LogInfo("Gun Sight part added to the game's part types");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Gun Sight part: couldn't add the part type: " + e.Message);
            }
        }
    }

    // The designer's part list: the Gun Sight right after the Tank Cannon (Weapons > Projectiles), drawn like the Camera part.
    [HarmonyPatch(typeof(DesignerPartList), "Load")]
    internal static class GunSightDesignerListPatch
    {
        private static void Postfix(DesignerPartList __instance)
        {
            try
            {
                foreach (DesignerPart existing in __instance.Parts)
                    if (existing.Name == GunSightPart.Name) return;

                DesignerPart camera = null, cannon = null;
                int cannonIndex = -1;
                for (int i = 0; i < __instance.Parts.Count; i++)
                {
                    DesignerPart p = __instance.Parts[i];
                    if (camera == null && p.Name == "Camera" && p.Category == "Gizmos") camera = p;
                    if (cannon == null && p.Name == "Tank Cannon") { cannon = p; cannonIndex = i; }
                }

                DesignerPart sight = new DesignerPart
                {
                    Name = GunSightPart.Name,
                    Icon = camera != null && !string.IsNullOrEmpty(camera.Icon) ? camera.Icon : "Camera",
                    Category = "Weapons",
                    Header = cannon != null ? cannon.Header : "Projectiles",
                    Description = "The eye of a gun's sniper view. Place it where the gun's sight is, facing the way the gun points, then select the cannon and pick it under Gun sight. " +
                                  "(Armor Fragments mod: a craft with this part needs the mod to open.)",
                    Mass = 0.5f,
                    StudioScale = camera != null ? camera.StudioScale : 6f,
                    StudioOffset = camera != null ? camera.StudioOffset : new UnityEngine.Vector3(0f, -0.1f, 0f),
                    StudioRotation = camera != null ? camera.StudioRotation : UnityEngine.Vector3.zero,
                    AssemblyElement = new XElement("Assembly", new XElement("Parts",
                        new XElement("Part",
                            new XAttribute("id", "1"),
                            new XAttribute("partType", GunSightPart.PartTypeId),
                            new XAttribute("position", "0,0,0"),
                            new XAttribute("rotation", "0,0,0"),
                            new XElement("CameraVantage.State", new XAttribute("viewMode", "FirstPerson"))))),
                };
                // Dragged in exactly as the Camera is (the game's own assembly for it), only with this part type.
                if (camera != null && camera.AssemblyElement != null)
                {
                    XElement copy = new XElement(camera.AssemblyElement);
                    foreach (XElement p in copy.Descendants("Part"))
                    {
                        XAttribute type = p.Attribute("partType");
                        if (type != null && type.Value == "Camera-1") type.Value = GunSightPart.PartTypeId;
                    }
                    sight.AssemblyElement = copy;
                }
                if (cannonIndex >= 0) __instance.Parts.Insert(cannonIndex + 1, sight);
                else __instance.Parts.Add(sight);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Gun Sight part: couldn't add it to the designer's list: " + e.Message);
            }
        }
    }

    // None of the camera's settings show on a Gun Sight.
    [HarmonyPatch(typeof(CameraVantageData), "GetGenericDesignerPropertyVisibilityCallback")]
    internal static class GunSightPropertiesPatch
    {
        private static void Postfix(CameraVantageData __instance, ref Func<bool> __result)
        {
            if (__instance != null && GunSightPart.Is(__instance.Part)) __result = () => false;
        }
    }
}
