using System;
using System.Xml.Linq;
using Jundroo.Juicy;
using Jundroo.Juicy.Widgets;

namespace ArmorFragments.UI
{
    // Opens and closes the panel, adds its button to the flight menu and
    // provides the panel's layout.
    internal static class ArmorPanel
    {
        // The path the game "loads" for the panel. UiPatches hands out our own XML for it.
        public const string PanelXmlPath = "Mods/ArmorFragments/Panel";
        public const string ButtonId = "armor-fragments-menu-button";

        private const string ButtonColor = "#E8903A";
        private const string ButtonColorActive = "#B8651A";

        private static ArmorPanelScript panel;
        private static ButtonWidget menuButton;

        public static bool IsOpen
        {
            get { return panel != null && !panel.IsClosed; }
        }

        // Called each time the flight menu is built.
        public static void AddMenuButton(Widget menu)
        {
            if (menu.FindWidget(ButtonId) != null) return;

            XElement element = new XElement("Button",
                new XAttribute("id", ButtonId),
                new XAttribute("class", "btn btn-default menu-tooltip"),
                new XAttribute("color", ButtonColor),
                new XAttribute("tooltip", "Tune the armor fragment system and see its latest results."),
                new XElement("Text", new XAttribute("text", "Armor Fragments")));

            ButtonWidget button = (ButtonWidget)menu.Context.CreateWidget(element, menu, menu.Stylesheet);
            button.Clicked += (Widget w) => Toggle();

            menuButton = button;
            panel = null;   // a new flight scene means any old panel is gone
            UpdateButtonColor();
        }

        public static void Toggle()
        {
            if (IsOpen)
            {
                panel.Close();
                return;
            }

            try
            {
                panel = Assets.Scripts.Game.Instance.UserInterface
                    .CreateDialog<ArmorPanelScript>(PanelXmlPath);
                panel.Closed += (Assets.Scripts.UI.IDialog d) =>
                {
                    panel = null;
                    UpdateButtonColor();
                };
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Couldn't open the Armor Fragments panel: " + e);
                panel = null;
            }
            UpdateButtonColor();
        }

        private static void UpdateButtonColor()
        {
            if (menuButton != null)
                menuButton.SetStyle("color", IsOpen ? ButtonColorActive : ButtonColor);
        }

        // The panel layout. Single quotes keep the C# string readable.
        public static XElement BuildLayout()
        {
            string xml = @"
<Xml>
   <Stylesheets>
      <Stylesheet path='Xml/Styles' />
      <Stylesheet>
         <Styles>
            <Style class='armor-dialog' width='620' maxHeightPercent='90' />
         </Styles>
      </Stylesheet>
   </Stylesheets>
   <Widgets>
      <Instance id='armor-dialog' templateId='dialog' class='armor-dialog' contentPadding='7 7 7 7'
                anchor='TopLeft' position='25 -25' script='{SCRIPT}' title='Armor Fragments'
                backerVisible='false' buttonRowVisible='false' spacerVisible='false'>
         <Section sectionId='title-buttons'>
            <Button id='armor-close-button' class='btn btn-danger' width='35' height='35' anchor='MiddleRight'>
               <Image sprite='Sprites/Common/IconButtonClose' />
            </Button>
         </Section>
         <Section sectionId='content'>
            <ScrollView fitContentHeight='true' verticalScrollbarVisibility='AutoHideAndExpandViewport'>
               <VerticalLayout id='armor-content' class='vertical-layout'
                               padding='@HalfSpacing @Spacing @HalfSpacing @HalfSpacing' spacing='@Spacing' />
            </ScrollView>
         </Section>
      </Instance>
   </Widgets>
</Xml>";
            return XElement.Parse(xml.Replace("{SCRIPT}", typeof(ArmorPanelScript).FullName));
        }
    }
}
