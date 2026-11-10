using Assets.Scripts.Flight;
using UnityEngine.InputSystem;

namespace ArmorFragments
{
    // Key (and panel button) that shows the player's own vehicle in the X-ray view, coloured by the
    // damage its parts have really taken. Pressing it again switches the view off.
    internal static class VehicleXRay
    {
        // Called every frame by the plugin.
        public static void Tick()
        {
            try
            {
                Key key = ArmorFragmentsPlugin.VehicleXRayKey.Value;
                Keyboard keyboard = Keyboard.current;
                if (key == Key.None || keyboard == null) return;
                if (keyboard[key].wasPressedThisFrame) Toggle();
            }
            catch (System.Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Vehicle X-ray key failed: " + e);
            }
        }

        public static void Toggle()
        {
            if (XRayView.IsLive)
            {
                XRayView.Hide();
                return;
            }

            FlightSceneScript scene = FlightSceneScript.Instance;
            FlightScenePlayer player = scene != null ? scene.LocalPlayer : null;
            if (player == null || player.Aircraft == null)
            {
                ArmorFragmentsPlugin.Log.LogInfo("Vehicle X-ray: no vehicle to show (not in a flight?)");
                return;
            }

            XRayView.ShowVehicle(player.Aircraft);
        }
    }
}
