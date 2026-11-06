using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Assets.Scripts.UI;
using Assets.Scripts.UI.Controls;
using Jundroo.Juicy.Widgets;

namespace ArmorFragments.UI
{
    // Creates the game's own controls (headers, toggles, sliders...) inside a parent widget.
    // The controls come from templates the game defines in its "Xml/Styles" file.
    internal sealed class PanelBuilder
    {
        // Which sections the player collapsed. Kept between openings of the panel.
        private static readonly Dictionary<string, bool> CollapsedSections =
            new Dictionary<string, bool>();

        public Widget Parent { get; private set; }

        public PanelBuilder(Widget parent)
        {
            Parent = parent;
        }

        private Widget Template(string templateId, string label = null, string tooltip = null)
        {
            List<XAttribute> attributes = new List<XAttribute>();
            if (label != null) attributes.Add(new XAttribute("label", label));
            if (!string.IsNullOrEmpty(tooltip)) attributes.Add(new XAttribute("tooltip", tooltip));
            return Parent.Context.CreateWidgetFromTemplate(templateId, Parent, attributes);
        }

        // A clickable section title. Everything added after it, up to the next header,
        // is hidden when the player collapses it.
        public void Header(string title, bool collapsed = false)
        {
            HeaderScript header = Template("control-header").GetComponentInChildren<HeaderScript>();
            header.LabelText = title;

            bool wasCollapsed;
            header.StartCollapsed = CollapsedSections.TryGetValue(title, out wasCollapsed)
                ? wasCollapsed : collapsed;
            header.CollapsedStateChanged += (object sender, HeaderScript.CollapsedStateChangedEventArgs e) =>
            {
                CollapsedSections[title] = e.IsCollapsed;
            };
        }

        public TextControl Text(string label, string value)
        {
            TextControl control = new TextControl(Template("control-text"));
            control.LabelText = label;
            control.ValueText.Text = value;
            return control;
        }

        public ToggleControl Toggle(string label, string tooltip, bool value, Action<bool> changed)
        {
            ToggleControl control = new ToggleControl(Template("control-toggle", label, tooltip));
            control.LabelText = label;
            control.IsOn = value;   // set before listening, so it doesn't count as a change
            control.ValueChanged += (bool x) => Run(() => changed(x));
            return control;
        }

        // steps: how many positions the slider has (0 = smooth).
        // changed: while dragging. released: when the mouse is let go.
        public SliderControl Slider(string label, string tooltip, float min, float max, int steps,
                                    float value, Func<float, string> format,
                                    Action<float> changed, Action<float> released)
        {
            SliderControl control = new SliderControl(Template("control-slider", label, tooltip));
            control.LabelText = label;
            control.SetRange(min, max, steps);
            control.ValueFormatter = format;
            control.SetValue(value);

            // Listen only after the first SetValue, or opening the panel would "change" the setting.
            control.Slider.ValueChanged += (float x) => Run(() => changed(x));
            if (released != null)
                control.OnRelease += (float oldValue, float x) => Run(() => released(x));
            return control;
        }

        // A row of buttons side by side.
        public void Buttons(IList<KeyValuePair<string, Action>> buttons)
        {
            XElement row = new XElement("Row",
                new XAttribute("height", "48"),
                new XAttribute("spacing", "16"));
            int span = Math.Max(1, 12 / Math.Max(1, buttons.Count));

            foreach (KeyValuePair<string, Action> button in buttons)
            {
                row.Add(new XElement("Button",
                    new XAttribute("class", "btn btn-default"),
                    new XAttribute("colSpan", span),
                    new XElement("Text", new XAttribute("text", button.Key))));
            }

            List<ButtonWidget> created =
                Parent.Context.CreateWidget(row, Parent, Parent.Stylesheet).Widgets.OfType<ButtonWidget>().ToList();
            for (int i = 0; i < created.Count && i < buttons.Count; i++)
            {
                Action action = buttons[i].Value;
                created[i].Clicked += (Widget w) => Run(action);
            }
        }

        // A mistake in one control's code must not break the whole panel.
        private static void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError(e);
            }
        }
    }
}
