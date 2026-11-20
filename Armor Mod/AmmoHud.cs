using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using Assets.Scripts.Craft;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.UI;
using Jundroo.Juicy.Widgets;
using UnityEngine;

namespace ArmorFragments
{
    // The rounds left in each ammo rack, on the flight screen: one bar for each rack, above the game's fire buttons (bottom left), made like the
    // throttle and fuel bars (the same pill: a rounded button with a filling bar). On the left the rack's round (caliber and kind, "50mm AP"),
    // on the right the rounds left; a click on any bar switches all of them between rounds left and percent, like the speed bar does between
    // IAS, GS and TAS. The widgets come from the game's own flight UI (its styles and classes), as children of its "main-ui", so they hide with it.
    internal static class AmmoHud
    {
        private const string IdPrefix = "af-ammo-";
        private const float LeftMargin = 15f;      // like the fire buttons (position "15 15")
        private const float FirstBottom = 105f;    // above the fire buttons (75 high)
        private const float Spacing = 40f;         // a bar is 32 high
        private const float PillWidth = 215f;
        private const float ClickLength = 0.18f;   // seconds, for shots that are not close together
        private const float ClickDepth = 0.07f;    // how much the button shrinks

        private sealed class Pill
        {
            public Widget Root;
            public ImageWidget Fill;
            public TextWidget Name, Value;
            public int Rack;
            public string RackName;
            public int Rounds = -1;
            public int Start = -1;
            public bool Relative;
            public bool Warning;
            public float PulseStart, PulseLength, LastShot = -10f;   // the click of a shot
        }

        private static readonly List<Pill> pills = new List<Pill>();
        private static readonly List<AmmoRacks.RackHud> racks = new List<AmmoRacks.RackHud>();
        private static Widget parent;
        private static AircraftScript craft;
        private static float nextAttempt;

        private static readonly Color Normal = new Color(0.89f, 0.62f, 0.05f, 1f);
        private static readonly Color Low = new Color(0.86f, 0.21f, 0.27f, 1f);   // the game's "danger" red

        public static void Tick()
        {
            if (!ArmorFragmentsPlugin.AmmoHudEnabled.Value)
            {
                if (pills.Count > 0) Clear();
                return;
            }
            try
            {
                FlightSceneScript scene = FlightSceneScript.Instance;
                AircraftScript aircraft = scene != null && scene.LocalPlayer != null ? scene.LocalPlayer.Aircraft : null;
                if (aircraft == null || aircraft.LoadContext != CraftLoadContext.Flight)
                {
                    if (pills.Count > 0) Clear();
                    return;
                }

                AmmoRacks.HudRacks(aircraft, racks);
                if (racks.Count == 0)
                {
                    if (pills.Count > 0) Clear();
                    return;
                }

                if (NeedsRebuild(aircraft))
                {
                    // Looking for the flight screen costs a little: not every frame while it isn't there.
                    if (Time.unscaledTime < nextAttempt) return;
                    nextAttempt = Time.unscaledTime + 0.5f;
                    Rebuild(scene, aircraft);
                }
                Refresh();
                KeepBehind();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Ammo display failed, it is switched off: " + e);
                ArmorFragmentsPlugin.AmmoHudEnabled.Value = false;
                Clear();
            }
        }

        private static bool NeedsRebuild(AircraftScript aircraft)
        {
            if (aircraft != craft || parent == null || pills.Count != racks.Count) return true;
            for (int i = 0; i < pills.Count; i++)
            {
                if (pills[i].Root == null) return true;
                if (pills[i].Rack != racks[i].Id || pills[i].RackName != racks[i].Name) return true;
            }
            return false;
        }

        private static void Rebuild(FlightSceneScript scene, AircraftScript aircraft)
        {
            Clear();
            FlightUIScript ui = scene.FlightUI;
            Widget root = ui != null ? ui.RootWidget : null;
            Widget mainUi = root != null ? root.FindWidget("main-ui") : null;
            if (mainUi == null) return;

            RemoveLeftovers(mainUi);
            for (int i = 0; i < racks.Count; i++)
            {
                AmmoRacks.RackHud entry = racks[i];
                Widget widget = mainUi.Context.CreateWidget(PillXml(i, entry.Name), mainUi, mainUi.Stylesheet);
                if (widget == null) continue;
                Pill pill = new Pill
                {
                    Root = widget,
                    Fill = widget.FindWidget<ImageWidget>(IdPrefix + "fill-" + i),
                    Name = widget.FindWidget<TextWidget>(IdPrefix + "name-" + i),
                    Value = widget.FindWidget<TextWidget>(IdPrefix + "value-" + i),
                    Rack = entry.Id,
                    RackName = entry.Name,
                };
                if (pill.Name != null) pill.Name.Text = entry.Name;
                widget.Clicked += OnClicked;
                if (widget.transform != null) widget.transform.SetAsFirstSibling();   // under the game's other menus
                pills.Add(pill);
            }
            parent = mainUi;
            craft = aircraft;
            ArmorFragmentsPlugin.Log.LogInfo("Ammo display: " + pills.Count + " bar(s) above the fire buttons");
        }

        // A bar like the game's own (Xml/Flight/FlightUI: the throttle and fuel pills): the rounded button, the bar that fills, and two texts.
        private static XElement PillXml(int index, string name)
        {
            string xml =
                "<Button id='" + IdPrefix + index + "' class='btn btn-flight' anchor='BottomLeft' position='" +
                LeftMargin.ToString("F0", CultureInfo.InvariantCulture) + " " +
                (FirstBottom + index * Spacing).ToString("F0", CultureInfo.InvariantCulture) + "' width='" +
                PillWidth.ToString("F0", CultureInfo.InvariantCulture) + "' mask='EnabledAndShown' tooltipPosition='Right' " +
                "tooltip='Rounds left in this ammo rack\\nClick to switch between rounds and percent'>" +
                "<Image id='" + IdPrefix + "fill-" + index + "' sprite='@SpriteWhite' color='#E39E0D' type='Filled' fillMethod='Horizontal' fillAmount='1' />" +
                "<Text id='" + IdPrefix + "name-" + index + "' fontSize='18' alignment='Left' color='White' margin-left='12' />" +
                "<Text id='" + IdPrefix + "value-" + index + "' alignment='Right' color='White' />" +
                "</Button>";
            return XElement.Parse(xml);
        }

        private static void OnClicked(Widget widget)
        {
            ArmorFragmentsPlugin.AmmoHudRelative.Value = !ArmorFragmentsPlugin.AmmoHudRelative.Value;
        }

        // Each frame: the bar and the number, only touching the widgets when something changed.
        // Drawn under the game's other menus: a widget made later sits above the earlier ones among its siblings, so each bar is kept first.
        private static float nextOrder;

        private static void KeepBehind()
        {
            if (Time.unscaledTime < nextOrder) return;
            nextOrder = Time.unscaledTime + 0.5f;
            foreach (Pill pill in pills)
            {
                Transform t = pill.Root != null ? pill.Root.transform : null;
                if (t != null && t.GetSiblingIndex() != 0) t.SetAsFirstSibling();
            }
        }

        private static void Refresh()
        {
            bool relative = ArmorFragmentsPlugin.AmmoHudRelative.Value;
            for (int i = 0; i < pills.Count && i < racks.Count; i++)
            {
                Pill pill = pills[i];
                AmmoRacks.RackHud entry = racks[i];

                // A shot out of the rack: the button plays a click (it dips and comes back). A slow rate plays it at the usual length; a fast one
                // plays it faster, so that it always finishes before the next shot.
                float now = Time.unscaledTime;
                if (pill.PulseLength > 0f)
                {
                    float t = (now - pill.PulseStart) / pill.PulseLength;
                    if (t >= 1f)
                    {
                        pill.PulseLength = 0f;
                        pill.Root.Rect.localScale = Vector3.one;
                    }
                    else
                    {
                        float scale = 1f - ClickDepth * Mathf.Sin(Mathf.PI * t);
                        pill.Root.Rect.localScale = new Vector3(scale, scale, 1f);
                    }
                }
                if (pill.Rounds >= 0 && entry.Rounds < pill.Rounds && !entry.Warning)
                {
                    float gap = now - pill.LastShot;
                    pill.LastShot = now;
                    pill.PulseStart = now;
                    pill.PulseLength = gap < ClickLength / 0.9f ? Mathf.Max(gap * 0.9f, 0.03f) : ClickLength;
                }

                if (pill.Rounds == entry.Rounds && pill.Start == entry.Start && pill.Relative == relative && pill.Warning == entry.Warning) continue;

                pill.Rounds = entry.Rounds;
                pill.Start = entry.Start;
                pill.Relative = relative;
                pill.Warning = entry.Warning;

                float fraction = entry.Start > 0 ? Mathf.Clamp01((float)entry.Rounds / entry.Start) : 0f;
                if (pill.Fill != null)
                {
                    pill.Fill.Image.fillAmount = fraction;
                    Color color = entry.Warning || fraction < 0.2f ? Low : Normal;   // red while the rack burns
                    pill.Fill.Color.Base = color;
                    pill.Fill.Image.color = color;
                }
                if (pill.Value != null)
                    pill.Value.Text = relative ? Mathf.RoundToInt(fraction * 100f).ToString(CultureInfo.InvariantCulture) + "%"
                                               : entry.Rounds.ToString(CultureInfo.InvariantCulture);
            }
        }

        // Bars that an earlier copy of the plugin (before a reload) left in the flight screen.
        private static void RemoveLeftovers(Widget mainUi)
        {
            for (int guard = 0; guard < 64; guard++)
            {
                Widget old = null;
                foreach (Widget child in mainUi.Widgets)
                {
                    if (child != null && child.Id != null && child.Id.StartsWith(IdPrefix, StringComparison.Ordinal)) { old = child; break; }
                }
                if (old == null) return;
                old.Destroy();
            }
        }

        private static void Clear()
        {
            foreach (Pill pill in pills)
            {
                try
                {
                    if (pill.Root != null) pill.Root.Destroy();
                }
                catch (Exception) { }
            }
            pills.Clear();
            parent = null;
            craft = null;
        }

    }
}
