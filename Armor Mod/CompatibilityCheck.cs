using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Multiplayer;
using UnityEngine;

namespace ArmorFragments
{
    // Runs once when the mod loads: looks, with reflection, at every part of the game the mod depends on,
    // and says in the log ([COMPAT]) which ones are not as the mod expects. A game update can rename or
    // retype something; this makes that show up in the very first lines of the log (and in a user's
    // bug report) instead of as a failure somewhere else.
    internal static class CompatibilityCheck
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static int checks;
        private static int problems;

        public static void Run()
        {
            try
            {
                checks = 0;
                problems = 0;

                // Parts and damage
                Method(typeof(PartScript), "OnDamaged", typeof(int?), typeof(float), typeof(Vector3), typeof(Vector3));
                Method(typeof(PartScript), "OnDamageLevelIncreased");
                Member(typeof(PartScript), "PartDamage", typeof(float));
                Member(typeof(PartScript), "MaxHealth", typeof(float));
                Member(typeof(PartScript), "Aircraft", typeof(AircraftScript));
                Member(typeof(PartScript), "PartMaterialScript", typeof(PartMaterialScript));
                Member(typeof(PartScript), "PartGroup", null);
                Member(typeof(PartData), "Name", typeof(string));
                Member(typeof(PartData), "PartScript", typeof(PartScript));

                // X-ray
                Member(typeof(PartMaterialScript), "RendererMaps", null);
                Member(typeof(PartMaterialScript.RendererMaterialMap), "Renderer", typeof(Renderer));   // since game 0.7.10.100 (was MeshRenderer)

                // Aircraft and variables
                Member(typeof(AircraftScript), "RemoteAircraft", typeof(bool));
                Member(typeof(AircraftScript), "Velocity", typeof(Vector3));
                Member(typeof(AircraftScript), "VariableSystem", typeof(VariableSystemScript));
                Member(typeof(AircraftScript), "Parts", null);
                Member(typeof(AircraftScript), "MainCockpit", null);
                Method(typeof(BodyScript), "DisconnectParts");
                Member(typeof(AircraftScript), "LoadContext", null);
                Method(typeof(AircraftScript), "GetPartById");
                Method(typeof(VariableSystemScript), "AddVariable", typeof(string));
                Method(typeof(VariableSystemScript), "RefreshVariables");
                Method(typeof(AircraftVariable), "SetValue", typeof(float), typeof(int));

                // Cannons
                Method(typeof(CannonProjectileScript), "OnCollisionEnter", typeof(Collision));
                Member(typeof(CannonProjectileScript), "_previousFrameVelocity", typeof(Vector3));
                Member(typeof(CannonProjectileScript), "_cannonScript", typeof(CannonScript));
                Member(typeof(CannonProjectileScript), "Owner", typeof(AircraftScript));
                Member(typeof(CannonProjectileScript), "SelfDestructTimer", typeof(float));
                Method(typeof(CannonScript), "PlayMuzzleFlash");
                Member(typeof(CannonScript), "_projectileSpawnPoint", typeof(Transform));
                Member(typeof(CannonScript), "_projectilePrefab", typeof(GameObject));
                Member(typeof(CannonData), "CaliberInMilimeters", typeof(float));
                Member(typeof(CannonData), "ProjectileVolume", typeof(float));
                Member(typeof(CannonData), "ProjectileVelocity", typeof(float));
                Member(typeof(CannonData), "ProjectileLifetime", typeof(float));
                Member(typeof(CannonData), "AmmoType", typeof(CannonData.ProjectileType));
                Member(typeof(CannonProjectileScript), "_isExplosive", typeof(bool));

                // Multiplayer
                Method(typeof(NetworkAircraftScript), "DamagePart");
                Method(typeof(NetworkAircraftScript), "ProcessPartNetworkMessage");
                Method(typeof(NetworkAircraftScript), "RpcLogic___RpcDamageRemotePartServer___2803290691");

                // What the game now has for OnDamaged (an override somewhere would be missed by a patch on the base).
                Overrides();

                ArmorFragmentsPlugin.Log.LogInfo(
                    "[COMPAT] Game " + Application.version + ", mod " + ArmorFragmentsPlugin.Version + ": " +
                    checks + " checks, " + problems + (problems == 1 ? " problem" : " problems") +
                    (problems == 0 ? " (everything the mod uses is where it expects)" : " (see the warnings above)"));
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[COMPAT] check failed: " + e);
            }
        }

        private static void Problem(string text)
        {
            problems++;
            ArmorFragmentsPlugin.Log.LogWarning("[COMPAT] " + text);
        }

        // A method with this name (and these parameters, if given) must exist.
        private static void Method(Type type, string name, params Type[] parameters)
        {
            checks++;
            MethodInfo method = parameters.Length > 0
                ? type.GetMethod(name, All, null, parameters, null)
                : FirstNamed(type, name);
            if (method != null) return;

            Problem(type.Name + "." + name + "(" + Names(parameters) + ") not found. The game has: " + Overloads(type, name));
        }

        // A property or field with this name (and this type, if given) must exist.
        private static void Member(Type type, string name, Type expected)
        {
            checks++;
            PropertyInfo property = type.GetProperty(name, All);
            FieldInfo field = property == null ? type.GetField(name, All) : null;
            Type actual = property != null ? property.PropertyType : (field != null ? field.FieldType : null);

            if (actual == null)
            {
                Problem(type.Name + "." + name + " not found");
                return;
            }
            if (expected != null && actual != expected)
                Problem(type.Name + "." + name + " is now a " + actual.FullName + " (the mod expects " + expected.FullName + ")");
        }

        private static MethodInfo FirstNamed(Type type, string name)
        {
            foreach (MethodInfo method in type.GetMethods(All))
                if (method.Name == name) return method;
            return null;
        }

        // Every OnDamaged that a part class declares, so a user's log shows where damage can enter.
        private static void Overrides()
        {
            List<string> found = new List<string>();
            foreach (Type type in TypesOf(typeof(PartScript).Assembly))
            {
                if (type == null || !typeof(PartScript).IsAssignableFrom(type)) continue;
                foreach (MethodInfo method in type.GetMethods(All | BindingFlags.DeclaredOnly))
                    if (method.Name == "OnDamaged") found.Add(type.Name + "(" + Names(ParameterTypes(method)) + ")");
            }
            ArmorFragmentsPlugin.Log.LogInfo("[COMPAT] OnDamaged declared by: " + (found.Count == 0 ? "none?" : string.Join("; ", found.ToArray())));
        }

        private static IEnumerable<Type> TypesOf(System.Reflection.Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types;
            }
        }

        private static Type[] ParameterTypes(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            Type[] types = new Type[parameters.Length];
            for (int i = 0; i < types.Length; i++) types[i] = parameters[i].ParameterType;
            return types;
        }

        private static string Overloads(Type type, string name)
        {
            List<string> list = new List<string>();
            foreach (MethodInfo method in type.GetMethods(All))
                if (method.Name == name) list.Add(name + "(" + Names(ParameterTypes(method)) + ")");
            return list.Count == 0 ? "no method of that name" : string.Join("; ", list.ToArray());
        }

        private static string Names(Type[] types)
        {
            StringBuilder text = new StringBuilder();
            foreach (Type type in types)
            {
                if (text.Length > 0) text.Append(", ");
                text.Append(type != null ? type.Name : "?");
            }
            return text.ToString();
        }
    }
}
