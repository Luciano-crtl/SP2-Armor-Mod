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

        private Widget Template(string templateId, string label = null, string tooltip = null, string unit = null)
        {
            List<XAttribute> attributes = new List<XAttribute>();
            if (label != null) attributes.Add(new XAttribute("label", label));
            if (!string.IsNullOrEmpty(tooltip)) attributes.Add(new XAttribute("tooltip", tooltip));
            if (unit != null) attributes.Add(new XAttribute("unit", unit));
            return Parent.Context.CreateWidgetFromTemplate(templateId, Parent, attributes);
        }

        // A clickable section title. Everything added after it, up to the next header,
        // is hidden when the player collapses it.
        public void Header(string title, bool collapsed = false)
        {
            EndSubsection();   // a subsection never reaches past its section
            sectionTitle = title;
            AddHeader(title, title, collapsed);
        }

        private Widget root;            // the panel itself while a subsection is open
        private string sectionTitle = "";

        // A section inside the section: its own header and rows in a container of their own. The game's header hides the widgets after it under
        // the same parent, so the outer header hides the whole container and the inner one only what is in it.
        public void BeginSubsection(string title, bool collapsed = true)
        {
            EndSubsection();
            Widget container = Parent.Context.CreateWidget(new XElement("VerticalLayout",
                new XAttribute("class", "vertical-layout"),
                new XAttribute("padding", "16 0 0 0"),
                new XAttribute("spacing", "@Spacing")), Parent, Parent.Stylesheet);
            root = Parent;
            Parent = container;
            AddHeader(title, sectionTitle + "/" + title, collapsed);
        }

        public void EndSubsection()
        {
            if (root == null) return;
            Parent = root;
            root = null;
        }

        private void AddHeader(string title, string key, bool collapsed)
        {
            HeaderScript header = Template("control-header").GetComponentInChildren<HeaderScript>();
            header.LabelText = title;

            bool wasCollapsed;
            header.StartCollapsed = CollapsedSections.TryGetValue(key, out wasCollapsed)
                ? wasCollapsed : collapsed;
            header.CollapsedStateChanged += (object sender, HeaderScript.CollapsedStateChangedEventArgs e) =>
            {
                CollapsedSections[key] = e.IsCollapsed;
            };
        }

        public TextControl Text(string label, string value)
        {
            TextControl control = new TextControl(Template("control-text"));
            control.LabelText = label;
            control.ValueText.Text = value;
            return control;
        }

        // A paragraph: the text rows are one line high, so a long text is broken into lines of about `width` characters, one row each (the label
        // on the first). One long row would spill over the rows around it.
        public void Note(string label, string text, int width = 44)
        {
            string line = "";
            bool first = true;
            foreach (string word in text.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    Text(first ? label : "", line);
                    first = false;
                    line = word;
                }
                else line = line.Length == 0 ? word : line + " " + word;
            }
            if (line.Length > 0) Text(first ? label : "", line);
        }

        public ToggleControl Toggle(string label, string tooltip, bool value, Action<bool> changed)
        {
            ToggleControl control = new ToggleControl(Template("control-toggle", label, tooltip));
            control.LabelText = label;
            control.IsOn = value;   // set before listening, so it doesn't count as a change
            control.ValueChanged += (bool x) => Run(() => changed(x));
            return control;
        }

        // A row with a label and a box where text can be typed (the game's own "control-spinner-input-label" with a plain text box). changed runs
        // when the typing is finished (Enter, or the box loses the focus), with the text as typed.
        public TMPro.TMP_InputField TextInput(string label, string tooltip, string value, int maxLength, Action<string> changed, string placeholder = null)
        {
            List<XAttribute> attributes = new List<XAttribute>
            {
                new XAttribute("label", label),
                new XAttribute("contentType", "Standard"),
                new XAttribute("placeholderText", placeholder ?? "Enter a name..."),
            };
            if (!string.IsNullOrEmpty(tooltip)) attributes.Add(new XAttribute("tooltip", tooltip));
            Widget row = Parent.Context.CreateWidgetFromTemplate("control-spinner-input-label", Parent, attributes);
            TMPro.TMP_InputField field = row != null ? row.GetComponentInChildren<TMPro.TMP_InputField>(true) : null;
            if (field == null) return null;
            field.text = value ?? "";
            field.characterLimit = maxLength;
            string typedFrom = field.text;
            field.onSelect.AddListener(text => typedFrom = field.text);
            field.onEndEdit.AddListener(text =>
            {
                if (text != typedFrom) Run(() => changed(text));
            });
            return field;
        }

        // The game's slider with a box beside it where the value can be typed in (as on its fuselage shape panel).
        // steps: how many positions the slider has (0 = smooth). format: the number in the box; unit: shown after it.
        // changed: while dragging or typing. released: when the mouse is let go, or the typing is done.
        public SliderControl Slider(string label, string tooltip, float min, float max, int steps,
                                    float value, string unit, Func<float, string> format,
                                    Action<float> changed, Action<float> released)
        {
            SliderControl control = new SliderControl(Template("control-slider-text", label, tooltip, unit ?? ""));
            control.LabelText = label;
            control.SetRange(min, max, steps);
            control.ValueFormatter = format;
            control.SetValue(value);

            // Listen only after the first SetValue, or opening the panel would "change" the setting.
            control.OnValueChanged += (float oldValue, float x) => Run(() => changed(x));
            if (released != null)
            {
                control.OnRelease += (float oldValue, float x) => Run(() => released(x));
                if (control.ValueInput != null && control.ValueInput.Input != null)
                {
                    float typedFrom = value;
                    control.ValueInput.Input.onSelect.AddListener(text => typedFrom = control.Value);
                    control.ValueInput.Input.onEndEdit.AddListener(text =>
                    {
                        if (control.Value != typedFrom) Run(() => released(control.Value));
                    });
                }
            }
            return control;
        }

        // A row of buttons side by side. The one at `selected` (if any) is drawn as picked, in the game's blue
        // "btn-primary" style (as its own view buttons show the view in use).
        public void Buttons(IList<KeyValuePair<string, Action>> buttons, int selected = -1)
        {
            XElement row = new XElement("Row",
                new XAttribute("height", "48"),
                new XAttribute("spacing", "16"));
            int span = Math.Max(1, 12 / Math.Max(1, buttons.Count));

            for (int b = 0; b < buttons.Count; b++)
            {
                KeyValuePair<string, Action> button = buttons[b];
                row.Add(new XElement("Button",
                    new XAttribute("class", b == selected ? "btn btn-primary" : "btn btn-default"),
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
