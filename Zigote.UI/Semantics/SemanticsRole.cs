namespace Zigote.UI.Semantics;

/// <summary>
///     The accessibility role a semantics node plays — the platform-neutral equivalent of an ARIA role
///     / NSAccessibility role / UIA control type. A platform bridge (<see cref="ISemanticsBridge" />)
///     maps these onto the host's native accessibility vocabulary. <see cref="None" /> marks a
///     grouping
///     node that carries no role of its own (a container that exists only to hold children).
/// </summary>
public enum SemanticsRole
{
    None = 0,
    Group,

    /// <summary>Static, non-interactive text.</summary>
    Text,

    /// <summary>A heading / section title.</summary>
    Header,

    Image,
    Button,
    Link,
    Checkbox,
    RadioButton,
    Switch,
    Slider,

    /// <summary>An editable text input (single- or multi-line).</summary>
    TextField,

    ProgressBar,
    Tab,
    TabList,
    List,
    ListItem,
    MenuItem,
    ScrollView,

    /// <summary>A modal surface (dialog/sheet) that traps focus.</summary>
    Dialog,

    /// <summary>A transient announcement region (snackbar/toast).</summary>
    Alert,
}