using Tmds.DBus.Protocol;
using Zigote.Core.Engine;

namespace Tray;

/// <summary>
///     The Linux tray, which is not an OS call but a pair of D-Bus objects: an
///     <c>org.kde.StatusNotifierItem</c> the shell draws, and a <c>com.canonical.dbusmenu</c> the
///     shell asks for the menu behind it. That is why it lives in a plugin rather than in the
///     engine — see <see cref="TrayIcon" /> — on a protocol library the engine must not carry.
///     <para>
///         <b>Degrades silently.</b> A desktop with no <c>org.kde.StatusNotifierWatcher</c> on the
///         bus — plain GNOME, without the AppIndicator extension — has nowhere to put an icon, so
///         nothing is registered and nothing is logged as an error. KDE, XFCE, Cinnamon and
///         GNOME-with-extension get it. <see cref="LastError" /> says why when it did not happen.
///     </para>
///     <para>
///         Threading: calls arrive on a D-Bus thread and read
///         <see cref="_items" />/<see cref="_tooltip" />, which the UI thread swaps whole rather
///         than mutating; the menu callbacks are handed straight to the app, which posts them.
///     </para>
///     <para>
///         <b><see cref="MessageWriter" /> is a ref struct and must be passed by <c>ref</c>.</b>
///         Passing it by value writes into a copy and sends a truncated body, which the bus answers
///         by disconnecting rather than erroring.
///     </para>
/// </summary>
public sealed class StatusNotifierItem : ITrayIcon, IPathMethodHandler
{
    private const string ItemPath = "/StatusNotifierItem";
    private const string MenuPath = "/MenuBar";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string MenuInterface = "com.canonical.dbusmenu";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string IntrospectableInterface = "org.freedesktop.DBus.Introspectable";
    private const string WatcherService = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";

    private static readonly string[] ItemProperties =
    [
        "Category", "Id", "Title", "Status", "IconName", "IconThemePath", "ToolTip", "ItemIsMenu",
        "Menu", "WindowId"
    ];

    private static readonly string[] MenuProperties =
    [
        "Version", "TextDirection", "Status", "IconThemePath"
    ];

    private readonly string _appId;
    private readonly string _title;
    private readonly Action<int> _onSelect;
    private readonly Action _onActivate;

    private DBusConnection? _connection;
    private volatile string _tooltip;
    private volatile IReadOnlyList<TrayMenuItem> _items = [];

    /// <summary>Bumped whenever the menu is replaced; the shell refetches a layout whose revision
    ///     moved.</summary>
    private uint _revision;

    /// <param name="appId">The desktop entry / icon name — the shell looks the icon up in the
    ///     hicolor theme under this name, which is what the app's installer puts there.</param>
    /// <param name="title">The item's title, shown by shells in tooltips and accessibility.</param>
    /// <param name="tooltip">Initial hover text.</param>
    /// <param name="onSelect">A menu item was chosen, by tag. Arrives on a D-Bus thread.</param>
    /// <param name="onActivate">Left click on the icon. Arrives on a D-Bus thread.</param>
    public StatusNotifierItem(string appId, string title, string tooltip, Action<int> onSelect,
        Action onActivate)
    {
        _appId = appId;
        _title = title;
        _tooltip = tooltip;
        _onSelect = onSelect;
        _onActivate = onActivate;
    }

    /// <summary>True once the shell has taken the item.</summary>
    public bool Running { get; private set; }

    /// <summary>Why there is no tray icon, or null when there is one.</summary>
    public string? LastError { get; private set; }

    public string Path => "/";

    // One handler for both objects: the item and its menu are two paths of the same feature, and
    // splitting them would be two registrations and two copies of the property plumbing.
    public bool HandlesChildPaths => true;

    public void SetTooltip(string tooltip)
    {
        if (_tooltip == tooltip) return;
        _tooltip = tooltip;
        Emit(ItemPath, ItemInterface, "NewToolTip");
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> items)
    {
        _items = items;
        _revision++;
        if (!Running || _connection is null) return;
        try
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(null, MenuPath, MenuInterface, "LayoutUpdated", "ui");
            writer.WriteUInt32(_revision);
            writer.WriteInt32(0); // the root's subtree, i.e. all of it
            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception)
        {
            // A menu the shell has not heard about yet is a stale menu, not a broken app.
        }
    }

    /// <summary>
    ///     Own a name, publish the two objects, and offer the item to the shell. Never throws: no
    ///     session bus and no watcher are both normal, and both mean "no tray".
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            var address = DBusAddress.Session;
            if (address is null)
            {
                LastError = "no session bus address";
                return;
            }

            // Our own connection rather than the shared autoconnect one: owning a name is not
            // permitted on autoconnect connections.
            var connection = new DBusConnection(address);
            await connection.ConnectAsync();
            connection.AddMethodHandler(this);

            // The name the spec tells watchers to expect. The trailing counter is for apps with
            // several items; this plugin publishes one.
            var busName = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
            if (!await connection.TryRequestNameAsync(busName, RequestNameOptions.None))
            {
                LastError = $"bus name {busName} is already owned";
                return;
            }

            _connection = connection;
            await connection.CallMethodAsync(Register(connection, busName));
            Running = true;
        }
        catch (Exception ex)
        {
            // Overwhelmingly: no StatusNotifierWatcher on the bus, which is plain GNOME and is not
            // an error — the app simply has no tray there.
            Running = false;
            _connection = null;
            LastError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public void Dispose()
    {
        // Dropping the connection unowns the name, which is what tells the shell the item is gone.
        // There is no Unregister in the spec — watchers follow NameOwnerChanged.
        _connection?.Dispose();
        _connection = null;
        Running = false;
    }

    private static MessageBuffer Register(DBusConnection connection, string busName)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                WatcherService, WatcherPath, WatcherService, "RegisterStatusNotifierItem", "s");
            writer.WriteString(busName);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private void Emit(string path, string iface, string member)
    {
        if (!Running || _connection is null) return;
        try
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(null, path, iface, member);
            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception)
        {
            // As above: a dropped signal must not take the app with it.
        }
    }

    // ── incoming calls ────────────────────────────────────────────────────────

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        // Signals are delivered here too (NameAcquired arrives the moment the name is ours), and
        // replying to one is a protocol violation the daemon punishes by closing the connection.
        if (context.Request.MessageType != MessageType.MethodCall) return default;

        try
        {
            var path = context.Request.PathAsString ?? "";
            var iface = context.Request.InterfaceAsString ?? "";
            var member = context.Request.MemberAsString ?? "";

            if (iface == IntrospectableInterface && member == "Introspect")
            {
                var writer = context.CreateReplyWriter("s");
                writer.WriteString(path == MenuPath ? MenuIntrospectXml : ItemIntrospectXml);
                context.Reply(writer.CreateMessage());
                return default;
            }

            if (iface == PropertiesInterface)
            {
                HandleProperties(context, path, member);
                return default;
            }

            if (path == MenuPath && iface == MenuInterface)
            {
                HandleMenu(context, member);
                return default;
            }

            if (path == ItemPath && iface == ItemInterface)
            {
                // Activate is a left click; the rest are the gestures a shell may send that this
                // item has no separate meaning for, and they are answered rather than errored so
                // the shell does not log a failure per scroll event.
                if (member == "Activate") _onActivate();
                var writer = context.CreateReplyWriter("");
                context.Reply(writer.CreateMessage());
                return default;
            }

            context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", member);
        }
        catch (Exception)
        {
            if (!context.ReplySent && !context.NoReplyExpected)
                context.ReplyError("org.freedesktop.DBus.Error.Failed", "");
        }

        return default;
    }

    private void HandleProperties(MethodContext context, string path, string member)
    {
        var menu = path == MenuPath;
        var names = menu ? MenuProperties : ItemProperties;

        switch (member)
        {
            case "Get":
            {
                var reader = context.Request.GetBodyReader();
                reader.ReadString(); // interface: only one per object here
                var property = reader.ReadString();
                if (Array.IndexOf(names, property) < 0)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", property);
                    return;
                }

                var writer = context.CreateReplyWriter("v");
                WriteValue(ref writer, menu, property);
                context.Reply(writer.CreateMessage());
                return;
            }

            case "GetAll":
            {
                var writer = context.CreateReplyWriter("a{sv}");
                var dict = writer.WriteDictionaryStart();
                foreach (var name in names)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(name);
                    WriteValue(ref writer, menu, name);
                }

                writer.WriteDictionaryEnd(dict);
                context.Reply(writer.CreateMessage());
                return;
            }

            default:
            {
                // Set: nothing here is writable, and an error would show up in the shell's log on
                // every start.
                var writer = context.CreateReplyWriter("");
                context.Reply(writer.CreateMessage());
                return;
            }
        }
    }

    private void WriteValue(ref MessageWriter writer, bool menu, string property)
    {
        if (menu)
        {
            switch (property)
            {
                case "Version":
                    writer.WriteVariantUInt32(3);
                    return;
                case "TextDirection":
                    writer.WriteVariantString("ltr");
                    return;
                case "Status":
                    writer.WriteVariantString("normal");
                    return;
                default:
                    writer.WriteVariant(VariantValue.Array(Array.Empty<string>()));
                    return;
            }
        }

        switch (property)
        {
            case "Category":
                // The spec's categories are ApplicationStatus, Communications, SystemServices and
                // Hardware; an app's status item is the first.
                writer.WriteVariantString("ApplicationStatus");
                return;
            case "Id":
                writer.WriteVariantString(_appId);
                return;
            case "Title":
                writer.WriteVariantString(_title);
                return;
            case "Status":
                writer.WriteVariantString("Active");
                return;
            case "IconName":
                // A theme icon name, not a pixmap: the icon is installed in hicolor for the
                // launcher, so the shell can find it and there is nothing to marshal.
                writer.WriteVariantString(_appId);
                return;
            case "ToolTip":
                WriteToolTip(ref writer);
                return;
            case "ItemIsMenu":
                // False: a left click activates (shows the window) and only a right click opens
                // the menu. True would make every click a menu.
                writer.WriteVariantBool(false);
                return;
            case "Menu":
                writer.WriteVariantObjectPath(MenuPath);
                return;
            case "WindowId":
                writer.WriteVariantInt32(0);
                return;
            default:
                // Never leave a dictionary key without a value: a body with a dangling key is
                // exactly what the bus rejects.
                writer.WriteVariantString("");
                return;
        }
    }

    /// <summary>The <c>(sa(iiay)ss)</c> a shell shows on hover: icon name, pixmaps (none — the
    ///     named icon covers it), title, description.</summary>
    private void WriteToolTip(ref MessageWriter writer)
    {
        writer.WriteSignature("(sa(iiay)ss)");
        writer.WriteStructureStart();
        writer.WriteString(_appId);
        var pixmaps = writer.WriteArrayStart(DBusType.Struct);
        writer.WriteArrayEnd(pixmaps);
        writer.WriteString(_title);
        writer.WriteString(_tooltip);
    }

    // ── com.canonical.dbusmenu ────────────────────────────────────────────────

    /// <summary>An underscore is a mnemonic marker in dbusmenu; doubling it is how a literal one
    ///     survives into the menu.</summary>
    internal static string EscapeMnemonics(string label)
    {
        return label.Replace("_", "__", StringComparison.Ordinal);
    }

    private void HandleMenu(MethodContext context, string member)
    {
        switch (member)
        {
            case "GetLayout":
            {
                var writer = context.CreateReplyWriter("u(ia{sv}av)");
                writer.WriteUInt32(_revision);
                WriteLayout(ref writer, _items);
                context.Reply(writer.CreateMessage());
                return;
            }

            case "GetGroupProperties":
            {
                var items = _items;
                var writer = context.CreateReplyWriter("a(ia{sv})");
                var array = writer.WriteArrayStart(DBusType.Struct);
                for (var i = 0; i < items.Count; i++)
                {
                    writer.WriteStructureStart();
                    writer.WriteInt32(i + 1);
                    WriteItemProperties(ref writer, items[i]);
                }

                writer.WriteArrayEnd(array);
                context.Reply(writer.CreateMessage());
                return;
            }

            case "GetProperty":
            {
                var reader = context.Request.GetBodyReader();
                var id = reader.ReadInt32();
                var property = reader.ReadString();
                var items = _items;
                var item = id >= 1 && id <= items.Count ? items[id - 1] : default;
                var writer = context.CreateReplyWriter("v");
                switch (property)
                {
                    case "label":
                        writer.WriteVariantString(item.Label);
                        break;
                    case "enabled":
                        writer.WriteVariantBool(item.Enabled);
                        break;
                    case "visible":
                        writer.WriteVariantBool(true);
                        break;
                    case "type":
                        writer.WriteVariantString(item.IsSeparator ? "separator" : "standard");
                        break;
                    default:
                        writer.WriteVariantString("");
                        break;
                }

                context.Reply(writer.CreateMessage());
                return;
            }

            case "Event":
            {
                var reader = context.Request.GetBodyReader();
                var id = reader.ReadInt32();
                var eventId = reader.ReadString();
                var items = _items;
                if (eventId == "clicked" && id >= 1 && id <= items.Count)
                {
                    var item = items[id - 1];
                    if (!item.IsSeparator && item.Enabled) _onSelect(item.Tag);
                }

                var writer = context.CreateReplyWriter("");
                context.Reply(writer.CreateMessage());
                return;
            }

            case "AboutToShow":
            {
                // False = "the layout you have is current". It is: the app pushes a new menu with
                // LayoutUpdated whenever the state behind it moves.
                var writer = context.CreateReplyWriter("b");
                writer.WriteBool(false);
                context.Reply(writer.CreateMessage());
                return;
            }

            default:
            {
                // EventGroup, AboutToShowGroup: answered empty rather than errored — a shell that
                // gets an error here logs one per menu opening.
                var writer = context.CreateReplyWriter("");
                context.Reply(writer.CreateMessage());
                return;
            }
        }
    }

    /// <summary>
    ///     The <c>(ia{sv}av)</c> tree. One level deep: a root whose children are the items, each
    ///     child a variant carrying the same struct type. Ids are the item's position, one-based —
    ///     zero is the root's.
    /// </summary>
    private static void WriteLayout(ref MessageWriter writer, IReadOnlyList<TrayMenuItem> items)
    {
        writer.WriteStructureStart();
        writer.WriteInt32(0);

        var rootProperties = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("children-display");
        writer.WriteVariantString("submenu");
        writer.WriteDictionaryEnd(rootProperties);

        var children = writer.WriteArrayStart(DBusType.Variant);
        for (var i = 0; i < items.Count; i++)
        {
            writer.WriteSignature("(ia{sv}av)");
            writer.WriteStructureStart();
            writer.WriteInt32(i + 1);
            WriteItemProperties(ref writer, items[i]);
            var grandchildren = writer.WriteArrayStart(DBusType.Variant);
            writer.WriteArrayEnd(grandchildren);
        }

        writer.WriteArrayEnd(children);
    }

    private static void WriteItemProperties(ref MessageWriter writer, TrayMenuItem item)
    {
        var properties = writer.WriteDictionaryStart();

        if (item.IsSeparator)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("type");
            writer.WriteVariantString("separator");
        }
        else
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("label");
            writer.WriteVariantString(EscapeMnemonics(item.Label));

            writer.WriteDictionaryEntryStart();
            writer.WriteString("enabled");
            writer.WriteVariantBool(item.Enabled);
        }

        writer.WriteDictionaryEntryStart();
        writer.WriteString("visible");
        writer.WriteVariantBool(true);

        writer.WriteDictionaryEnd(properties);
    }

    private const string ItemIntrospectXml = """
                                             <!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN"
                                             "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
                                             <node>
                                               <interface name="org.freedesktop.DBus.Properties">
                                                 <method name="Get">
                                                   <arg name="interface_name" type="s" direction="in"/>
                                                   <arg name="property_name" type="s" direction="in"/>
                                                   <arg name="value" type="v" direction="out"/>
                                                 </method>
                                                 <method name="GetAll">
                                                   <arg name="interface_name" type="s" direction="in"/>
                                                   <arg name="properties" type="a{sv}" direction="out"/>
                                                 </method>
                                               </interface>
                                               <interface name="org.kde.StatusNotifierItem">
                                                 <method name="Activate">
                                                   <arg name="x" type="i" direction="in"/>
                                                   <arg name="y" type="i" direction="in"/>
                                                 </method>
                                                 <method name="SecondaryActivate">
                                                   <arg name="x" type="i" direction="in"/>
                                                   <arg name="y" type="i" direction="in"/>
                                                 </method>
                                                 <method name="ContextMenu">
                                                   <arg name="x" type="i" direction="in"/>
                                                   <arg name="y" type="i" direction="in"/>
                                                 </method>
                                                 <method name="Scroll">
                                                   <arg name="delta" type="i" direction="in"/>
                                                   <arg name="orientation" type="s" direction="in"/>
                                                 </method>
                                                 <signal name="NewIcon"/>
                                                 <signal name="NewToolTip"/>
                                                 <signal name="NewStatus"><arg name="status" type="s"/></signal>
                                                 <property name="Category" type="s" access="read"/>
                                                 <property name="Id" type="s" access="read"/>
                                                 <property name="Title" type="s" access="read"/>
                                                 <property name="Status" type="s" access="read"/>
                                                 <property name="IconName" type="s" access="read"/>
                                                 <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
                                                 <property name="ItemIsMenu" type="b" access="read"/>
                                                 <property name="Menu" type="o" access="read"/>
                                               </interface>
                                             </node>
                                             """;

    private const string MenuIntrospectXml = """
                                             <!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN"
                                             "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
                                             <node>
                                               <interface name="com.canonical.dbusmenu">
                                                 <method name="GetLayout">
                                                   <arg name="parentId" type="i" direction="in"/>
                                                   <arg name="recursionDepth" type="i" direction="in"/>
                                                   <arg name="propertyNames" type="as" direction="in"/>
                                                   <arg name="revision" type="u" direction="out"/>
                                                   <arg name="layout" type="(ia{sv}av)" direction="out"/>
                                                 </method>
                                                 <method name="GetGroupProperties">
                                                   <arg name="ids" type="ai" direction="in"/>
                                                   <arg name="propertyNames" type="as" direction="in"/>
                                                   <arg name="properties" type="a(ia{sv})" direction="out"/>
                                                 </method>
                                                 <method name="GetProperty">
                                                   <arg name="id" type="i" direction="in"/>
                                                   <arg name="name" type="s" direction="in"/>
                                                   <arg name="value" type="v" direction="out"/>
                                                 </method>
                                                 <method name="Event">
                                                   <arg name="id" type="i" direction="in"/>
                                                   <arg name="eventId" type="s" direction="in"/>
                                                   <arg name="data" type="v" direction="in"/>
                                                   <arg name="timestamp" type="u" direction="in"/>
                                                 </method>
                                                 <method name="AboutToShow">
                                                   <arg name="id" type="i" direction="in"/>
                                                   <arg name="needUpdate" type="b" direction="out"/>
                                                 </method>
                                                 <signal name="LayoutUpdated">
                                                   <arg name="revision" type="u"/>
                                                   <arg name="parent" type="i"/>
                                                 </signal>
                                                 <property name="Version" type="u" access="read"/>
                                                 <property name="TextDirection" type="s" access="read"/>
                                                 <property name="Status" type="s" access="read"/>
                                               </interface>
                                             </node>
                                             """;
}
