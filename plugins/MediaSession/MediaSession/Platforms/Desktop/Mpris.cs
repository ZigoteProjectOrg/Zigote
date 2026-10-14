using Tmds.DBus.Protocol;

namespace MediaSession;

/// <summary>
///     MPRIS2 — media keys, the GNOME lock screen and quick-settings media widget, KDE's media
///     applet, and <c>playerctl</c>. All of it follows from owning
///     <c>org.mpris.MediaPlayer2.&lt;appId&gt;</c> and answering the standard interfaces. Lifted from
///     Timbre, where it has been in daily use.
///     <para>
///         <b><see cref="MessageWriter" /> is a ref struct and must be passed by <c>ref</c>.</b>
///         Passing it by value hands a helper a copy: the writes land in the copy, the caller's
///         writer never advances, and the message goes out truncated. The bus does not reply with an
///         error to a malformed body — it disconnects the peer — so the only symptom is the MPRIS
///         name appearing and vanishing a moment later, with <c>dbus-broker</c> logging "sent a
///         message with an invalid body". Every helper here takes <c>ref MessageWriter</c>; keep it
///         that way, and note that a <c>using</c> variable cannot be passed by ref, which is why
///         <see cref="Update" /> disposes its writer by hand.
///     </para>
///     <para>
///         Threading: calls arrive on a D-Bus thread and are raised there — the client's contract.
///         Reads answer from <see cref="_now" />, an immutable record swapped in by
///         <see cref="Update" />, so a read never races a mutation.
///     </para>
/// </summary>
internal sealed class Mpris : IPathMethodHandler, IDisposable
{
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string IntrospectableInterface = "org.freedesktop.DBus.Introspectable";
    private const string NoTrack = "/org/mpris/MediaPlayer2/TrackList/NoTrack";

    private static readonly string[] RootProperties =
    [
        "CanQuit", "CanRaise", "HasTrackList", "Identity", "DesktopEntry",
        "SupportedUriSchemes", "SupportedMimeTypes"
    ];

    private static readonly string[] PlayerProperties =
    [
        "PlaybackStatus", "Rate", "Metadata", "Volume", "Position", "MinimumRate", "MaximumRate",
        "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause", "CanSeek", "CanControl"
    ];

    private readonly string _busName;
    private readonly string _appId;
    private readonly string _appName;
    private readonly Action<MediaCommand> _raise;

    private DBusConnection? _connection;
    private volatile NowPlaying? _now;

    /// <summary>Bumped whenever the item changes — MPRIS wants an object path per track.</summary>
    private volatile string _trackId = NoTrack;

    private int _generation;

    public Mpris(string appId, string appName, Action<MediaCommand> raise)
    {
        _appId = appId;
        _appName = appName;
        _raise = raise;
        _busName = "org.mpris.MediaPlayer2." + BusNameElement(appId);
    }

    public string Path => ObjectPath;

    public bool HandlesChildPaths => false;

    public bool Running { get; private set; }

    /// <summary>
    ///     Connect and claim the bus name. Never throws: a machine with no session bus is a normal
    ///     machine, it just does not get media keys.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            if (DBusAddress.Session is not { } address) return;

            // Our own connection, not the shared DBusConnection.Session: that one is an autoconnect
            // connection, and owning a name is not permitted on those.
            var connection = new DBusConnection(address);
            await connection.ConnectAsync();

            // Register before claiming the name, never after: the bus routes calls to us the moment
            // the name is ours, and there must be a handler in place to answer them.
            connection.AddMethodHandler(this);

            // Try, not Request: a second instance of the app already holding the name is normal.
            if (!await connection.TryRequestNameAsync(_busName, RequestNameOptions.None))
            {
                connection.Dispose();
                return;
            }

            _connection = connection;
            Running = true;
        }
        catch (Exception)
        {
            Running = false;
        }
    }

    /// <summary>
    ///     Publish state. Emits <c>PropertiesChanged</c> only for what actually changed, and never
    ///     for Position — MPRIS excludes it because it moves continuously, and clients either poll
    ///     it or follow <c>Seeked</c>.
    /// </summary>
    public void Update(NowPlaying? next)
    {
        var previous = _now;
        bool newItem = next is not null && !next.SameItem(previous);
        if (newItem) _trackId = $"/dev/zigote/MediaSession/Track/{++_generation}";
        else if (next is null) _trackId = NoTrack;
        _now = next;
        if (!Running || _connection is null) return;

        var changed = new List<string>(4);
        if (Status(previous) != Status(next)) changed.Add("PlaybackStatus");
        if (newItem || (next is null) != (previous is null)) changed.Add("Metadata");
        if (previous?.CanNext != next?.CanNext) changed.Add("CanGoNext");
        if (previous?.CanPrevious != next?.CanPrevious) changed.Add("CanGoPrevious");
        if (changed.Count == 0) return;

        try
        {
            // Not a `using` variable: it has to be passed by ref to the value writer below, and C#
            // forbids that for using-variables. Disposed explicitly instead.
            var writer = _connection.GetMessageWriter();
            try
            {
                writer.WriteSignalHeader(null, ObjectPath, PropertiesInterface, "PropertiesChanged", "sa{sv}as");
                writer.WriteString(PlayerInterface);

                var dict = writer.WriteDictionaryStart();
                foreach (var name in changed)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(name);
                    WriteValue(ref writer, PlayerInterface, name);
                }

                writer.WriteDictionaryEnd(dict);

                var invalidated = writer.WriteArrayStart(DBusType.String);
                writer.WriteArrayEnd(invalidated);

                _connection.TrySendMessage(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }
        catch (Exception)
        {
            // A dropped signal must not take the player with it.
        }
    }

    /// <summary>A seek the player made itself; clients resync their progress bars from this.</summary>
    public void NotifySeeked(TimeSpan position)
    {
        if (!Running || _connection is null) return;
        try
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(null, ObjectPath, PlayerInterface, "Seeked", "x");
            writer.WriteInt64(Micros(position));
            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception)
        {
            // As above.
        }
    }

    public void Dispose()
    {
        Running = false;
        _connection?.Dispose();
        _connection = null;
    }

    // ── incoming calls ────────────────────────────────────────────────────────

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        // Only method calls may be answered. The bus also delivers signals here — NameAcquired
        // arrives the instant the name is granted — and replying to a signal is a protocol
        // violation the daemon punishes by closing the connection.
        if (context.Request.MessageType != MessageType.MethodCall) return default;

        try
        {
            var iface = context.Request.InterfaceAsString;
            var member = context.Request.MemberAsString ?? "";

            if (iface == PropertiesInterface && member == "Get")
            {
                var reader = context.Request.GetBodyReader();
                var target = reader.ReadString();
                var property = reader.ReadString();
                if (Array.IndexOf(PropertiesOf(target), property) < 0)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", property);
                    return default;
                }

                var writer = context.CreateReplyWriter("v");
                WriteValue(ref writer, target, property);
                context.Reply(writer.CreateMessage());
                return default;
            }

            if (iface == PropertiesInterface && member == "GetAll")
            {
                var target = context.Request.GetBodyReader().ReadString();
                var writer = context.CreateReplyWriter("a{sv}");
                var dict = writer.WriteDictionaryStart();
                foreach (var name in PropertiesOf(target))
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(name);
                    WriteValue(ref writer, target, name);
                }

                writer.WriteDictionaryEnd(dict);
                context.Reply(writer.CreateMessage());
                return default;
            }

            // Volume and Rate are advertised as fixed, so a Set is accepted and ignored rather than
            // erroring — clients sometimes probe them.
            if (iface == PropertiesInterface && member == "Set")
            {
                context.Reply(context.CreateReplyWriter("").CreateMessage());
                return default;
            }

            if (iface == IntrospectableInterface && member == "Introspect")
            {
                var writer = context.CreateReplyWriter("s");
                writer.WriteString(IntrospectXml);
                context.Reply(writer.CreateMessage());
                return default;
            }

            if ((iface == RootInterface || iface == PlayerInterface) && Invoke(member, context))
            {
                context.Reply(context.CreateReplyWriter("").CreateMessage());
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

    /// <summary>
    ///     Run a transport command. False when the member is not one of ours, so the caller can
    ///     answer with UnknownMethod instead of a success it did not earn.
    /// </summary>
    private bool Invoke(string member, MethodContext context)
    {
        switch (member)
        {
            case "Raise": _raise(new MediaCommand(MediaAction.Raise)); return true;
            case "Next": _raise(new MediaCommand(MediaAction.Next)); return true;
            case "Previous": _raise(new MediaCommand(MediaAction.Previous)); return true;
            case "Play": _raise(new MediaCommand(MediaAction.Play)); return true;
            case "Pause": _raise(new MediaCommand(MediaAction.Pause)); return true;
            case "PlayPause": _raise(new MediaCommand(MediaAction.PlayPause)); return true;
            case "Stop": _raise(new MediaCommand(MediaAction.Stop)); return true;
            case "Quit": return true; // CanQuit is false; answered so a probing client sees success, not an error

            case "Seek":
            {
                var offset = context.Request.GetBodyReader().ReadInt64();
                if (_now is { } now) _raise(new MediaCommand(MediaAction.SeekTo, now.SeekTarget(FromMicros(offset))));
                return true;
            }

            case "SetPosition":
            {
                // (o, x) — the track id guards a stale client seeking the wrong track.
                var reader = context.Request.GetBodyReader();
                var trackId = reader.ReadObjectPath().ToString();
                var position = FromMicros(reader.ReadInt64());
                if (trackId == _trackId && _now is { } now && position >= TimeSpan.Zero
                    && (now.Duration == TimeSpan.Zero || position <= now.Duration))
                    _raise(new MediaCommand(MediaAction.SeekTo, position));
                return true;
            }

            default:
                return false;
        }
    }

    private static string[] PropertiesOf(string iface)
    {
        if (iface == RootInterface) return RootProperties;
        return iface == PlayerInterface ? PlayerProperties : [];
    }

    private static string Status(NowPlaying? now) => now is null ? "Stopped" : now.Playing ? "Playing" : "Paused";

    private void WriteValue(ref MessageWriter writer, string iface, string property)
    {
        var now = _now;
        if (iface == RootInterface)
        {
            switch (property)
            {
                case "CanRaise":
                    writer.WriteVariant(VariantValue.Bool(true));
                    return;
                case "Identity":
                    writer.WriteVariant(VariantValue.String(_appName));
                    return;
                case "DesktopEntry":
                    writer.WriteVariant(VariantValue.String(_appId));
                    return;
                case "SupportedUriSchemes":
                case "SupportedMimeTypes":
                    writer.WriteVariant(VariantValue.Array(Array.Empty<string>()));
                    return;
                default: // CanQuit, HasTrackList
                    writer.WriteVariant(VariantValue.Bool(false));
                    return;
            }
        }

        switch (property)
        {
            case "PlaybackStatus":
                writer.WriteVariant(VariantValue.String(Status(now)));
                return;
            case "Rate":
            case "MinimumRate":
            case "MaximumRate":
            case "Volume":
                writer.WriteVariant(VariantValue.Double(1.0));
                return;
            case "Position":
                writer.WriteVariant(VariantValue.Int64(now is null ? 0 : Micros(now.Position)));
                return;
            case "Metadata":
                WriteMetadata(ref writer, now);
                return;
            case "CanGoNext":
                writer.WriteVariant(VariantValue.Bool(now?.CanNext ?? false));
                return;
            case "CanGoPrevious":
                writer.WriteVariant(VariantValue.Bool(now?.CanPrevious ?? false));
                return;
            case "CanPlay":
            case "CanPause":
                writer.WriteVariant(VariantValue.Bool(now is not null));
                return;
            case "CanSeek":
                writer.WriteVariant(VariantValue.Bool(now is { Duration.Ticks: > 0 }));
                return;
            case "CanControl":
                writer.WriteVariant(VariantValue.Bool(true));
                return;
            // Never leave a dictionary key without a value: a body with a dangling key is exactly
            // what the bus rejects.
            default:
                writer.WriteVariant(VariantValue.Bool(false));
                return;
        }
    }

    /// <summary>
    ///     The <c>a{sv}</c> every client reads to draw the track. <c>mpris:trackid</c> and
    ///     <c>mpris:length</c> are what the spec requires; the <c>xesam:</c> fields are what shells
    ///     display. <c>xesam:artist</c> and <c>albumArtist</c> are string *lists*, not strings.
    /// </summary>
    private void WriteMetadata(ref MessageWriter writer, NowPlaying? now)
    {
        writer.WriteSignature("a{sv}");
        var dict = writer.WriteDictionaryStart();

        writer.WriteDictionaryEntryStart();
        writer.WriteString("mpris:trackid");
        writer.WriteVariant(VariantValue.ObjectPath(new ObjectPath(_trackId)));

        if (now is not null)
        {
            if (now.Duration > TimeSpan.Zero)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("mpris:length");
                writer.WriteVariant(VariantValue.Int64(Micros(now.Duration)));
            }

            if (now.ArtPath is { Length: > 0 } art && File.Exists(art))
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("mpris:artUrl");
                writer.WriteVariant(VariantValue.String(new Uri(System.IO.Path.GetFullPath(art)).AbsoluteUri));
            }

            writer.WriteDictionaryEntryStart();
            writer.WriteString("xesam:title");
            writer.WriteVariant(VariantValue.String(now.Title));

            if (now.Artist is { Length: > 0 } artist)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:artist");
                writer.WriteVariant(VariantValue.Array(new[] { artist }));
            }

            if (now.AlbumArtist is { Length: > 0 } albumArtist)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:albumArtist");
                writer.WriteVariant(VariantValue.Array(new[] { albumArtist }));
            }

            if (now.Album is { Length: > 0 } album)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:album");
                writer.WriteVariant(VariantValue.String(album));
            }

            if (now.TrackNumber > 0)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:trackNumber");
                writer.WriteVariant(VariantValue.Int32(now.TrackNumber));
            }
        }

        writer.WriteDictionaryEnd(dict);
    }

    private static long Micros(TimeSpan t) => t.Ticks / 10;

    private static TimeSpan FromMicros(long micros) => TimeSpan.FromTicks(micros * 10);

    /// <summary>
    ///     A bus-name element from an app id: <c>[A-Za-z0-9_]</c>, not starting with a digit. Dots
    ///     are kept — they only add elements — so <c>dev.zigote.Timbre</c> stays recognisable.
    /// </summary>
    internal static string BusNameElement(string appId)
    {
        var chars = appId.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!(char.IsAsciiLetterOrDigit(chars[i]) || chars[i] is '_' or '.')) chars[i] = '_';
        var name = new string(chars).Trim('.');
        // Every element must not start with a digit; prefixing each offending one keeps it valid.
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
            if (char.IsAsciiDigit(parts[i][0])) parts[i] = "_" + parts[i];
        return parts.Length == 0 ? "app" : string.Join('.', parts);
    }

    private const string IntrospectXml = """
                                         <!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN"
                                         "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
                                         <node>
                                           <interface name="org.freedesktop.DBus.Introspectable">
                                             <method name="Introspect"><arg name="xml_data" type="s" direction="out"/></method>
                                           </interface>
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
                                             <method name="Set">
                                               <arg name="interface_name" type="s" direction="in"/>
                                               <arg name="property_name" type="s" direction="in"/>
                                               <arg name="value" type="v" direction="in"/>
                                             </method>
                                             <signal name="PropertiesChanged">
                                               <arg name="interface_name" type="s"/>
                                               <arg name="changed_properties" type="a{sv}"/>
                                               <arg name="invalidated_properties" type="as"/>
                                             </signal>
                                           </interface>
                                           <interface name="org.mpris.MediaPlayer2">
                                             <method name="Raise"/>
                                             <method name="Quit"/>
                                             <property name="CanQuit" type="b" access="read"/>
                                             <property name="CanRaise" type="b" access="read"/>
                                             <property name="HasTrackList" type="b" access="read"/>
                                             <property name="Identity" type="s" access="read"/>
                                             <property name="DesktopEntry" type="s" access="read"/>
                                             <property name="SupportedUriSchemes" type="as" access="read"/>
                                             <property name="SupportedMimeTypes" type="as" access="read"/>
                                           </interface>
                                           <interface name="org.mpris.MediaPlayer2.Player">
                                             <method name="Next"/>
                                             <method name="Previous"/>
                                             <method name="Pause"/>
                                             <method name="PlayPause"/>
                                             <method name="Stop"/>
                                             <method name="Play"/>
                                             <method name="Seek"><arg name="Offset" type="x" direction="in"/></method>
                                             <method name="SetPosition">
                                               <arg name="TrackId" type="o" direction="in"/>
                                               <arg name="Position" type="x" direction="in"/>
                                             </method>
                                             <signal name="Seeked"><arg name="Position" type="x"/></signal>
                                             <property name="PlaybackStatus" type="s" access="read"/>
                                             <property name="Rate" type="d" access="readwrite"/>
                                             <property name="Metadata" type="a{sv}" access="read"/>
                                             <property name="Volume" type="d" access="readwrite"/>
                                             <property name="Position" type="x" access="read"/>
                                             <property name="MinimumRate" type="d" access="read"/>
                                             <property name="MaximumRate" type="d" access="read"/>
                                             <property name="CanGoNext" type="b" access="read"/>
                                             <property name="CanGoPrevious" type="b" access="read"/>
                                             <property name="CanPlay" type="b" access="read"/>
                                             <property name="CanPause" type="b" access="read"/>
                                             <property name="CanSeek" type="b" access="read"/>
                                             <property name="CanControl" type="b" access="read"/>
                                           </interface>
                                         </node>
                                         """;
}
