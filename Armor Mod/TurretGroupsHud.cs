using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.UI;
using Jundroo.Juicy.Widgets;
using UnityEngine;

namespace ArmorFragments
{
    // The turret groups, on the flight screen (bottom right, above the game's row of buttons: recenter view, enter/exit, change view). For each
    // group two things, one above the other: a bar made like the game's own pills (the same classes and the same way the ammo bars are made,
    // see AmmoHud.cs) and, right under it, two small buttons: Ground and Air (both lit together = both). They are made like the other pills of the flight screen:
    // dark and half transparent with rounded corners (the game's "btn btn-flight" look, clipped by the button's mask), and blue (the game's
    // primary color) when lit; there is no fill of their own while they are off.
    //   The bar: blue = the group follows the mouse aim now (or is on an automatic mode), dark = it holds. A click switches it (several groups
    //   can be controlled together, or none at all).
    //   The two buttons: the group's automatic aim (ground targets, air targets, or both when both are lit). They are put out (darker) while the
    //   group is controlled by hand; a click on one takes the group off the manual control and sets it to that alone, and after that each click
    //   switches that option on or off (both off = the group holds). The bar then says what the group is shooting at.
    // Each group is one block (an empty container of a fixed size, 70 high) with the bar at its top and the options at its bottom, and the blocks
    // are stacked at a fixed pitch, so the groups never overlap each other whatever the buttons' own heights come out as. (A single vertical layout
    // holding everything was tried and the game did not draw it at all.) Only while the mouse aim is on. The cursor is locked in mouse aim: hold
    // Alt to click.
    internal static class TurretGroupsHud
    {
        private const string IdPrefix = "af-groups-";
        private const float RightMargin = 15f;     // like the game's row of buttons (position "-15 15")
        private const float FirstBottom = 95f + MouseAimButton.Height;   // above the row (50 high; 75 for the buttons that only show on a catapult or an elevator) and the mouse aim button
        private const float OptionHeight = 32f;     // as high as the bar: the button's corner radius then reads as the pills' rounded rectangle (24 high it was an oval)
        private const float Gap = 4f;              // between the two options
        private const float BarHeight = 32f;
        private const float BlockGap = 10f;        // between one group and the next
        private const float BlockHeight = BarHeight + 6f + OptionHeight;            // the container of one group
        private const float BlockPitch = BlockHeight + BlockGap;
        private const float PillWidth = 230f;
        private const float OptionWidth = (PillWidth - Gap) / 2f;

        private static readonly string[] OptionNames = { "Ground", "Air" };

        // The game's own sprites for the options (the paths used in its Xml): the checkered flag of the menu's Activities button for ground and the
        // aircraft of the menu's Change craft button for air.
        private const string SpriteGround = "Sprites/Common/IconActivity";
        private const string SpriteAir = "Sprites/Flight/IconLoad";
        private const float IconSize = 22f;
        private static readonly TurretAim.GroupAuto[] OptionModes = { TurretAim.GroupAuto.Ground, TurretAim.GroupAuto.Air };

        private sealed class Pill
        {
            public ImageWidget Fill;
            public TextWidget Name, State;
            public ImageWidget[] OptionFill = new ImageWidget[2];
            public readonly List<ImageWidget> OptionIcons = new List<ImageWidget>();
            public int Group;
            public int Controlled = -1, Live = -1, OnTarget = -1, Total = -1, Blocked = -1, Auto = -1;
            public string AutoText;
        }

        private static readonly List<Pill> pills = new List<Pill>();
        private static readonly List<TurretAim.GroupHud> groups = new List<TurretAim.GroupHud>();
        private static Widget parent;
        private static readonly List<Widget> blocks = new List<Widget>();   // one container for each group
        private static string signature = "";
        private static float nextAttempt;

        // The game's colors (its "ColorPrimary" #28599E and "ColorDanger" #DC3545): a lit bar or option is filled with the blue, an off one has no fill
        // (so the button's own dark half-transparent look shows), a put-out option is darkened with half-transparent black.
        private static readonly Color Blue = new Color(0x28 / 255f, 0x59 / 255f, 0x9E / 255f, 1f);
        private static readonly Color Gone = new Color(0xDC / 255f, 0x35 / 255f, 0x45 / 255f, 1f);
        private static readonly Color PutOut = new Color(0f, 0f, 0f, 0.5f);

        public static void Tick()
        {
            if (!ArmorFragmentsPlugin.TurretGroupsHudEnabled.Value || !TurretAim.Active)
            {
                if (pills.Count > 0) Clear();
                return;
            }
            try
            {
                TurretAim.GetGroups(groups);
                if (groups.Count < 2)   // one set (a tank, or every gun in the main set): nothing to switch
                {
                    if (pills.Count > 0) Clear();
                    return;
                }

                if (NeedsRebuild())
                {
                    if (Time.unscaledTime < nextAttempt) return;
                    nextAttempt = Time.unscaledTime + 0.5f;
                    Rebuild();
                }
                Refresh();
                KeepBehind();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Turret groups display failed, it is switched off: " + e);
                ArmorFragmentsPlugin.TurretGroupsHudEnabled.Value = false;
                Clear();
            }
        }

        private static string SignatureOf()
        {
            StringBuilder text = new StringBuilder();
            foreach (TurretAim.GroupHud group in groups) text.Append(group.Id).Append(':').Append(group.Total).Append(':').Append(group.Name).Append(',');
            return text.ToString();
        }

        private static bool NeedsRebuild()
        {
            if (parent == null || pills.Count != groups.Count || signature != SignatureOf()) return true;
            foreach (Widget block in blocks) if (block == null) return true;
            return blocks.Count != groups.Count;
        }

        private static void Rebuild()
        {
            Clear();
            FlightSceneScript scene = FlightSceneScript.Instance;
            FlightUIScript ui = scene != null ? scene.FlightUI : null;
            Widget root = ui != null ? ui.RootWidget : null;
            Widget mainUi = root != null ? root.FindWidget("main-ui") : null;
            if (mainUi == null) return;

            RemoveLeftovers(mainUi);
            for (int i = 0; i < groups.Count; i++)
            {
                TurretAim.GroupHud group = groups[i];
                Widget layout = mainUi.Context.CreateWidget(BlockXml(i, group), mainUi, mainUi.Stylesheet);
                if (layout == null) continue;
                blocks.Add(layout);
                layout.transform.SetAsFirstSibling();   // under the game's other menus
                Pill pill = new Pill
                {
                    Fill = layout.FindWidget<ImageWidget>(IdPrefix + "fill-" + i),
                    Name = layout.FindWidget<TextWidget>(IdPrefix + "name-" + i),
                    State = layout.FindWidget<TextWidget>(IdPrefix + "state-" + i),
                    Group = group.Id,
                };
                if (pill.Name != null) pill.Name.Text = group.Name;
                int id = group.Id;
                Widget bar = layout.FindWidget(IdPrefix + i);
                if (bar != null) bar.Clicked += w => TurretAim.ToggleGroup(id);
                for (int k = 0; k < 2; k++)
                {
                    pill.OptionFill[k] = layout.FindWidget<ImageWidget>(IdPrefix + "optfill-" + i + "-" + k);
                    ImageWidget icon = layout.FindWidget<ImageWidget>(IdPrefix + "optico-" + i + "-" + k);
                    if (icon != null) pill.OptionIcons.Add(icon);
                    Widget button = layout.FindWidget(IdPrefix + "opt-" + i + "-" + k);
                    TurretAim.GroupAuto mode = OptionModes[k];
                    if (button != null) button.Clicked += w => TurretAim.SetAuto(id, mode);
                }
                pills.Add(pill);
            }
            parent = mainUi;
            signature = SignatureOf();
            ArmorFragmentsPlugin.Log.LogInfo("Turret groups display: " + pills.Count + " group(s) above the camera buttons");
            try
            {
                // Where the game put the first block and its bar (if a bar ever does not show, this says whether it was made, how big, and where).
                if (blocks.Count > 0 && blocks[0] != null)
                {
                    Widget bar0 = blocks[0].FindWidget(IdPrefix + "0");
                    ArmorFragmentsPlugin.Log.LogInfo("Turret groups display: block 0 size " + blocks[0].Rect.rect.size + " at " + blocks[0].Rect.anchoredPosition + ", active " +
                                                     blocks[0].gameObject.activeInHierarchy + (bar0 != null ? "; its bar size " + bar0.Rect.rect.size + " at " + bar0.Rect.anchoredPosition : "; no bar found"));
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret groups display: could not read the block's size: " + e.Message);
            }
        }

        private static string Number(float value)
        {
            return value.ToString("F0", CultureInfo.InvariantCulture);
        }

        private static string Escape(string text)
        {
            return System.Security.SecurityElement.Escape(text ?? "");
        }

        // The icon of option k: ground = the flag, air = the aircraft.
        private static string OptionIconsXml(int i, int k)
        {
            return "<Image id='" + IdPrefix + "optico-" + i + "-" + k + "' sprite='" + (k == 0 ? SpriteGround : SpriteAir) + "' width='" + Number(IconSize) + "' height='" + Number(IconSize) +
                   "' anchor='MiddleCenter' color='White' raycastTarget='false' />";
        }

        // One group's block: an empty container anchored at the bottom right above the game's row of buttons, with the bar at its top (a rounded
        // button with its mask, a fill that is only shown while lit, and two texts: made like the fuel pill in Xml/Flight/FlightUI) and, at its
        // bottom, the row of two options (a horizontal layout like the game's row of buttons).
        private static XElement BlockXml(int i, TurretAim.GroupHud group)
        {
            StringBuilder xml = new StringBuilder();
            xml.Append("<Empty id='" + IdPrefix + "block-" + i + "' anchor='BottomRight' position='-" + Number(RightMargin) + " " + Number(FirstBottom + i * BlockPitch) + "' width='" + Number(PillWidth) +
                       "' height='" + Number(BlockHeight) + "'>");
            xml.Append("<Button id='" + IdPrefix + i + "' class='btn btn-flight' anchor='TopRight' position='0 0' width='" + Number(PillWidth) + "' height='" + Number(BarHeight) + "' mask='EnabledAndShown' tooltipPosition='Left' " +
                       "tooltip='Turret group " + Escape(group.Name) + "\\nClick: control it with the mouse aim, or let it hold. Several groups can be controlled together.\\nHold Alt to free the cursor.'>" +
                       "<Image id='" + IdPrefix + "fill-" + i + "' sprite='@SpriteWhite' color='#28599E' type='Filled' fillMethod='Horizontal' fillAmount='0' />" +
                       "<Text id='" + IdPrefix + "name-" + i + "' fontSize='18' alignment='Left' color='White' margin-left='12' />" +
                       "<Text id='" + IdPrefix + "state-" + i + "' alignment='Right' color='White' />" +
                       "</Button>");
            xml.Append("<HorizontalLayout anchor='BottomRight' position='0 0' childAlignment='LowerRight' sizeFitter='Horizontal' spacing='" + Number(Gap) +
                       "' childControlHeight='false' childControlWidth='false' childForceExpandHeight='false' childForceExpandWidth='false'>");
            for (int k = 0; k < 2; k++)
            {
                xml.Append("<Button id='" + IdPrefix + "opt-" + i + "-" + k + "' class='btn btn-flight' anchor='BottomRight' width='" + Number(OptionWidth) + "' height='" + Number(OptionHeight) + "' mask='EnabledAndShown' " +
                           "tooltipPosition='Left' tooltip='Automatic aim at " + OptionNames[k].ToLowerInvariant() + " targets\\nThe group aims and fires by itself. A click takes a group off the manual control and sets just this; after that a click switches it on or off, and with both lit the group takes both kinds. A vehicle that belongs to a player counts as air above a set height or speed (see the panel).'>" +
                           "<Image id='" + IdPrefix + "optfill-" + i + "-" + k + "' sprite='@SpriteWhite' color='#28599E' type='Filled' fillMethod='Horizontal' fillAmount='0' />" +
                           OptionIconsXml(i, k) +
                           "</Button>");
            }
            xml.Append("</HorizontalLayout></Empty>");
            return XElement.Parse(xml.ToString());
        }

        // Each frame: the colors and texts, only touching the widgets when something changed.
        // Drawn under the game's other menus: a widget made later sits above the earlier ones among its siblings, so each block is kept first.
        private static float nextOrder;

        private static void KeepBehind()
        {
            if (Time.unscaledTime < nextOrder) return;
            nextOrder = Time.unscaledTime + 0.5f;
            foreach (Widget block in blocks)
            {
                Transform t = block != null ? block.transform : null;
                if (t != null && t.GetSiblingIndex() != 0) t.SetAsFirstSibling();
            }
        }

        private static void Refresh()
        {
            for (int i = 0; i < pills.Count && i < groups.Count; i++)
            {
                Pill pill = pills[i];
                TurretAim.GroupHud group = groups[i];
                int controlled = group.Controlled ? 1 : 0;
                int auto = (int)group.Auto;
                if (pill.Controlled == controlled && pill.Live == group.Live && pill.OnTarget == group.OnTarget && pill.Total == group.Total && pill.Blocked == group.Blocked && pill.Auto == auto && pill.AutoText == group.AutoText) continue;
                pill.Controlled = controlled;
                pill.Live = group.Live;
                pill.OnTarget = group.OnTarget;
                pill.Total = group.Total;
                pill.Blocked = group.Blocked;
                pill.Auto = auto;
                pill.AutoText = group.AutoText;

                bool automatic = !group.Controlled && group.Auto != TurretAim.GroupAuto.Off;
                bool lit = group.Controlled || automatic;
                if (pill.Fill != null)
                {
                    Color color = group.Live == 0 ? Gone : Blue;
                    pill.Fill.Color.Base = color;
                    pill.Fill.Image.color = color;
                    pill.Fill.Image.fillAmount = group.Live == 0 || lit ? 1f : 0f;
                }
                if (pill.State != null)
                {
                    pill.State.Text = group.Live == 0 ? "destroyed"
                        : group.Controlled ? group.OnTarget.ToString(CultureInfo.InvariantCulture) + "/" + group.Live.ToString(CultureInfo.InvariantCulture) + " aimed" +
                                             (group.Blocked > 0 ? " (" + group.Blocked.ToString(CultureInfo.InvariantCulture) + " blocked)" : "")
                        : automatic ? (string.IsNullOrEmpty(group.AutoText) ? "auto" : group.AutoText)
                        : group.Live.ToString(CultureInfo.InvariantCulture) + " held";
                }

                // The options: the ones the group has are blue; while the group is controlled by hand both are put out (darkened); otherwise
                // they have no fill (the button's own dark half-transparent look).
                for (int k = 0; k < 2; k++)
                {
                    ImageWidget fill = pill.OptionFill[k];
                    if (fill == null) continue;
                    bool active = !group.Controlled && (group.Auto & OptionModes[k]) != 0;
                    Color option = active ? Blue : PutOut;
                    fill.Color.Base = option;
                    fill.Image.color = option;
                    fill.Image.fillAmount = active || group.Controlled ? 1f : 0f;
                }
                Color iconColor = group.Controlled ? new Color(1f, 1f, 1f, 0.3f) : Color.white;
                foreach (ImageWidget icon in pill.OptionIcons)
                {
                    icon.Color.Base = iconColor;
                    icon.Image.color = iconColor;
                }
            }
        }

        private static void RemoveLeftovers(Widget mainUi)
        {
            for (int guard = 0; guard < 128; guard++)
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
            foreach (Widget block in blocks)
            {
                try
                {
                    if (block != null) block.Destroy();
                }
                catch (Exception) { }
            }
            blocks.Clear();
            pills.Clear();
            parent = null;
            signature = "";
        }

    }
}
