using System.Collections.Generic;

namespace SolidWorksQrAddIn.Core.Payload
{
    /// <summary>
    /// How one preset field is entered. The pane builds an input per kind:
    /// a text box for <see cref="Text"/>, a multi-line box for
    /// <see cref="MultilineText"/>, a dropdown for <see cref="Choice"/> and a
    /// checkbox for <see cref="Boolean"/>.
    /// </summary>
    public enum PayloadFieldKind
    {
        /// <summary>Single-line text; the field value is the literal text.</summary>
        Text,

        /// <summary>
        /// Multi-line text; the field value is the literal text including
        /// line breaks — meaningful whitespace is never stripped.
        /// </summary>
        MultilineText,

        /// <summary>
        /// One option out of <see cref="PayloadFieldDescriptor.Options"/>;
        /// the field value is the chosen option's
        /// <see cref="PayloadFieldOption.Value"/>.
        /// </summary>
        Choice,

        /// <summary>On/off flag; the field value is "true" or "false".</summary>
        Boolean,
    }

    /// <summary>One selectable value of a <see cref="PayloadFieldKind.Choice"/> field.</summary>
    public sealed class PayloadFieldOption
    {
        public PayloadFieldOption(string value, string label)
        {
            Value = value;
            Label = label;
        }

        /// <summary>The field value this option submits — stable, lowercase.</summary>
        public string Value { get; }

        /// <summary>What the dropdown shows.</summary>
        public string Label { get; }
    }

    /// <summary>
    /// One ordered input field of a preset — the model the pane renders an
    /// input control from. Field keys are stable contract keys: the pane
    /// submits them verbatim as the keys of the
    /// <see cref="PayloadComposer.Compose"/> fields map.
    /// </summary>
    public sealed class PayloadFieldDescriptor
    {
        public PayloadFieldDescriptor(
            string key, string label, PayloadFieldKind kind, bool required,
            string defaultValue, string? hint = null,
            IReadOnlyList<PayloadFieldOption>? options = null)
        {
            Key = key;
            Label = label;
            Kind = kind;
            Required = required;
            DefaultValue = defaultValue;
            Hint = hint;
            Options = options;
        }

        /// <summary>The stable key the field map uses (e.g. "ssid").</summary>
        public string Key { get; }

        /// <summary>The caption the pane shows next to the input.</summary>
        public string Label { get; }

        /// <summary>Which input control the pane builds.</summary>
        public PayloadFieldKind Kind { get; }

        /// <summary>
        /// True when composition fails unless the field holds a non-blank
        /// value. Only ever set on text kinds — Choice and Boolean fields
        /// always carry a <see cref="DefaultValue"/> so they are never
        /// "missing".
        /// </summary>
        public bool Required { get; }

        /// <summary>
        /// The value the pane pre-fills and composition falls back to when
        /// the field is absent or empty: a <see cref="PayloadFieldOption.Value"/>
        /// for Choice, "true"/"false" for Boolean, "" for text kinds.
        /// </summary>
        public string DefaultValue { get; }

        /// <summary>
        /// Optional placeholder/example text (e.g. "47.3769") the pane may
        /// show inside an empty input. Never a submitted value.
        /// </summary>
        public string? Hint { get; }

        /// <summary>The selectable values of a Choice field; null otherwise.</summary>
        public IReadOnlyList<PayloadFieldOption>? Options { get; }
    }

    /// <summary>
    /// One named payload preset: a stable id, a display name for the
    /// content-type dropdown, and the ordered field descriptors the pane
    /// renders as inputs. This is the pane-facing contract — ticket #27
    /// enumerates <see cref="PayloadComposer.Presets"/> and should never
    /// hard-code per-preset UI.
    /// </summary>
    public sealed class PayloadPresetDescriptor
    {
        public PayloadPresetDescriptor(
            string id, string displayName, IReadOnlyList<PayloadFieldDescriptor> fields)
        {
            Id = id;
            DisplayName = displayName;
            Fields = fields;
        }

        /// <summary>The stable compose key (e.g. "wifi").</summary>
        public string Id { get; }

        /// <summary>What the content-type dropdown shows.</summary>
        public string DisplayName { get; }

        /// <summary>The preset's input fields in display order.</summary>
        public IReadOnlyList<PayloadFieldDescriptor> Fields { get; }
    }
}
