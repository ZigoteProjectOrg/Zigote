namespace Zigote.Cli;

/// <summary>
///     The files <c>zigote</c> writes.
///     <para>
///         Plain interpolated strings rather than a template engine or embedded resources: there
///         are six of them, they take two substitutions each, and a tool that must locate its own
///         template files at runtime is a tool that breaks when installed globally.
///     </para>
///     <para>
///         The comments inside the generated files are deliberate. Most of them record something
///         that is invisible until it breaks — why the Android RID is mandatory, why the picker
///         activity must not be no-history, why the service type has to match the permission — and
///         a generated file is the only place a reader will look.
///     </para>
/// </summary>
public static class Templates
{
    /// <summary>
    ///     The app project. Two shapes, and which one you get is the answer to "what is my app
    ///     built against".
    ///     <para>
    ///         The default is <c>PackageReference</c>s at the CLI's own version: an app depends on
    ///         <em>Zigote 0.1.0</em>, restores it like any other dependency, survives being moved,
    ///         and builds on CI without reproducing anyone's directory layout. The native engine
    ///         comes from <c>Zigote.Engine.runtime.&lt;rid&gt;</c>, which is why a phone build needs
    ///         no Zig toolchain.
    ///     </para>
    ///     <para>
    ///         Passing <c>--engine</c> (or scaffolding inside a checkout) gives the other shape:
    ///         <c>ProjectReference</c>s into the engine sources, which is what engine development
    ///         needs and what nobody else should be given by default.
    ///     </para>
    /// </summary>
    public static string AppCsproj(
        string name,
        string? engine,
        string version,
        string? extraReference = null
    ) =>
        $"""
         <Project Sdk="Microsoft.NET.Sdk">

             <PropertyGroup>
                 <OutputType>WinExe</OutputType>
                 <TargetFramework>net10.0</TargetFramework>
                 <ImplicitUsings>enable</ImplicitUsings>
                 <Nullable>enable</Nullable>
                 <LangVersion>latest</LangVersion>
                 <RootNamespace>{name}</RootNamespace>
                 <AssemblyName>{name.ToLowerInvariant()}</AssemblyName>
                 <!-- One version for the app itself. `zigote build` derives the Android versionCode,
                      the iOS CFBundleShortVersionString and the desktop metadata from this, so the
                      three cannot disagree on release day. -->
                 <Version>1.0.0</Version>
             </PropertyGroup>
         {(engine is null ? PackageEngineBlock(version) : ProjectEngineBlock(engine))}{(
             extraReference is null
                 ? ""
                 : $"\n    <ItemGroup>\n        <ProjectReference Include=\"{extraReference}\"/>\n    </ItemGroup>\n")}
         </Project>

         """;

    /// <summary>The SDK as packages — the shape a user who installed the tool gets.</summary>
    private static string PackageEngineBlock(string version) =>
        $"""
             <!-- The SDK version this app is built against. ONE string, used by every Zigote package
                  below, including the native engine: upgrading is editing this line, and a managed
                  half that disagrees with the native half is the mismatch that fails at startup with
                  an ABI error. Override per build with -p:ZigoteSdkVersion=<version> to try a newer
                  SDK without touching the file. -->
             <PropertyGroup>
                 <ZigoteSdkVersion Condition="'$(ZigoteSdkVersion)' == ''">{version}</ZigoteSdkVersion>
             </PropertyGroup>

             <!-- The engine's native half ships one package per RID, so building for a phone needs no
                  Zig toolchain and no NDK. The RID is spelled out because it is what couples the
                  managed build to the native one; get it wrong by hand and the app installs and dies. -->
             <PropertyGroup>
                 <ZigoteRuntimeRid Condition="'$(ZigoteRuntimeRid)' == '' And '$(ZigTargetRid)' != ''">$(ZigTargetRid)</ZigoteRuntimeRid>
                 <ZigoteRuntimeRid Condition="'$(ZigoteRuntimeRid)' == '' And '$(RuntimeIdentifier)' != ''">$(RuntimeIdentifier)</ZigoteRuntimeRid>
                 <ZigoteRuntimeRid Condition="'$(ZigoteRuntimeRid)' == ''">$(NETCoreSdkPortableRuntimeIdentifier)</ZigoteRuntimeRid>
                 <!-- Named so the native assets land in the output next to the executable rather than
                      only in the NuGet cache. Not self-contained: .NET 8 and later separate the two. -->
                 <RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == ''">$(ZigoteRuntimeRid)</RuntimeIdentifier>
                 <SelfContained>false</SelfContained>
             </PropertyGroup>

             <ItemGroup>
                 <PackageReference Include="Zigote.UI" Version="$(ZigoteSdkVersion)"/>
                 <PackageReference Include="Zigote.UI.Adwaita" Version="$(ZigoteSdkVersion)"/>
                 <PackageReference Include="Zigote.Engine.runtime.$(ZigoteRuntimeRid)" Version="$(ZigoteSdkVersion)"/>
             </ItemGroup>

             <!-- Debug-only: the Shift+D widget inspector and perf overlay. A release must not
                  carry the HUD, and the DEBUG-gated Install call compiles out with it. -->
             <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                 <PackageReference Include="Zigote.UI.DevTools" Version="$(ZigoteSdkVersion)"/>
             </ItemGroup>
         """;

    /// <summary>The SDK as sources — `--engine`, or a scaffold inside a checkout.</summary>
    private static string ProjectEngineBlock(string engine) =>
        $"""
             <!-- Where the engine lives. Override with -p:ZigoteRoot=<path> or $ZIGOTE_ROOT. -->
         {(engine is null ? "" : $"""
             <PropertyGroup>
                     <ZigoteRoot Condition="'$(ZigoteRoot)' == '' And '$(ZIGOTE_ROOT)' != ''">$(ZIGOTE_ROOT)</ZigoteRoot>
                     <ZigoteRoot Condition="'$(ZigoteRoot)' == ''">$(MSBuildThisFileDirectory){Identifier.XmlEscape(engine)}</ZigoteRoot>
                 </PropertyGroup>
             """)}

             <ItemGroup>
                 <ProjectReference Include="$(ZigoteRoot)/Zigote.UI/Zigote.UI.csproj"/>
                 <ProjectReference Include="$(ZigoteRoot)/Zigote.UI.Adwaita/Zigote.UI.Adwaita.csproj"/>
             </ItemGroup>

             <!-- Debug-only: the Shift+D widget inspector and perf overlay. A release must not
                  carry the HUD, and the DEBUG-gated Install call compiles out with it. -->
             <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                 <ProjectReference Include="$(ZigoteRoot)/Zigote.UI.DevTools/Zigote.UI.DevTools.csproj"/>
             </ItemGroup>
         """;

    public static string AppProgram(string name) =>
        $$"""
          namespace {{name}};

          /// <summary>The desktop entry point.</summary>
          public static class Program
          {
              public static void Main(string[] args)
              {
                  new {{name}}App().Run();
              }
          }

          """;

    public static string AppShell(string name) =>
        $$""""
          using Zigote.Core.Paint;
          using Zigote.Core.State;
          using Zigote.UI.Adwaita;
          using Zigote.UI.Theme;
          using Zigote.UI.Widgets;
          using Zigote.UI.Widgets.Controls;
          using Zigote.UI.Widgets.Layout;

          namespace {{name}};

          /// <summary>
          ///     The app. Everything here is shared by every platform it ships on — a platform head
          ///     (see <c>zigote add android</c>) starts this same class and adds nothing to it.
          /// </summary>
          public sealed class {{name}}App : AdwaitaApp
          {
              public {{name}}App() : base(
                  home: new HomePage(),
                  title: "{{name}}",
                  theme: AdwTheme.Dark
              )
              {
                  // The desktop window size. Ignored on a phone, where the OS decides.
                  Width = 420;
                  Height = 720;
              }

              protected override void OnInit()
              {
                  base.OnInit();
          #if DEBUG
                  if (App is { } app)
                      Zigote.UI.DevTools.DevTools.Install(app, Zigote.UI.DevTools.DevToolsProfile.TwoD);
          #endif
              }
          }

          /// <summary>
          ///     Wrapped in <c>SafeArea</c> because this runs on phones too, where the status bar and
          ///     the gesture pill overlap the window — without it the top row sits under the clock.
          /// </summary>
          internal sealed class HomePage : ComposedWidget
          {
              // Safe as a field because this page is retained for the app's lifetime. State that has
              // to outlive rebuilds of its owner belongs above it, in a store the page is handed.
              private readonly Signal<int> _taps = new(0);

              protected override Widget Build(BuildContext context)
              {
                  var theme = ThemeProvider.Of(context);

                  return new SafeArea(new AdwToolbarView(
                      new Center(
                          new Column(
                              mainAxisAlignment: MainAxisAlignment.Center,
                              children:
                              [
                                  new Text(
                                      "Hello from {{name}}",
                                      new TextStyle(28, fontWeight: FontWeight.Bold)
                                  ),
                                  new SizedBox(height: 8),
                                  new Text(
                                      "Taps so far:",
                                      new TextStyle(color: theme.TextSecondary)
                                  ),
                                  // Only this subtree re-runs when the signal changes.
                                  new Watch(() => new Text(
                                      _taps.Value.ToString(),
                                      new TextStyle(34, fontWeight: FontWeight.SemiBold)
                                  )),
                                  new SizedBox(height: 16),
                                  new AdwButton("Tap", () => _taps.Value++) {
                                      Style = AdwButtonStyle.Suggested,
                                      Pill = true,
                                  }
                              ]
                          )
                      )
                  ) { TopBars = { new AdwHeaderBar { Title = "{{name}}" } } });
              }
          }

          """";

    public static string GitIgnore() =>
        """
        bin/
        obj/
        *.user
        .idea/
        .vs/

        """;

    public static string Readme(string name) =>
        $"""
         # {name}

         A [Zigote](https://github.com/zigote) app.

         ## Run

         ```
         zigote run                       # the attached device if there is one, else this machine
         zigote run --device desktop      # here, explicitly
         zigote run --widget My.Widget    # one widget on its own
         ```

         ## Android

         ```
         zigote add android
         zigote run                                                 # deploy + hot reload
         dotnet build {name}.Android -p:ZigTargetRid=android-arm64   # device
         dotnet build {name}.Android -p:ZigTargetRid=android-x64     # emulator
         ```

         `zigote run` reads the attached device's ABI and picks the RID for you, then keeps
         the app running while you edit: saving a `Build()` re-runs it in place instead of
         reinstalling. `zigote logs` is that app's logcat and nothing else.

         The RID is not optional. It selects the managed runtime identifier *and* the engine's
         native cross-compile, and `zig-out` holds only one `libzigote.so` at a time — so building
         without it can package one architecture's engine under another's folder, which installs
         cleanly and then dies on load. The generated project refuses to build without it.

         """;

    // ── android head ──────────────────────────────────────────────────────────

    public static string AndroidCsproj(string name, string appId, string? engine, string version) =>
        $"""
         <Project Sdk="Microsoft.NET.Sdk">

             <!--
               Android head for {name}: compiles the SAME sources as the desktop head, plus the
               platform files in this project. Java owns the process here — SDLActivity starts the
               SDL thread and calls zigote_android_main, which invokes the app-main that
               {name}Application registered during startup.
             -->
             <PropertyGroup>
                 <OutputType>Exe</OutputType>
                 <TargetFramework>net10.0-android</TargetFramework>
                 <!-- 26 is the engine's Android floor: AAudio needs it, and the NDK sysroot the
                      native build links against is pinned to the same level. -->
                 <SupportedOSPlatformVersion>26</SupportedOSPlatformVersion>
                 <ImplicitUsings>enable</ImplicitUsings>
                 <Nullable>enable</Nullable>
                 <LangVersion>latest</LangVersion>
                 <RootNamespace>{name}</RootNamespace>
                 <ApplicationTitle>{name}</ApplicationTitle>
                 <ApplicationId>{appId}</ApplicationId>
                 <ApplicationVersion>1</ApplicationVersion>
                 <ApplicationDisplayVersion>1.0.0</ApplicationDisplayVersion>
                 <RuntimeIdentifiers>android-arm64;android-x64</RuntimeIdentifiers>
                 <AndroidPackageFormat>apk</AndroidPackageFormat>
                 <!-- Ship the managed assemblies INSIDE the apk. Debug builds otherwise use Fast
                      Deployment, which pushes them out-of-band — so an apk installed by hand
                      (adb install) aborts at startup with "No assemblies found". -->
                 <EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>
                 <!-- The UI stack leans on reflection (DevTools late-bind, widget inspector). -->
                 <AndroidLinkMode>None</AndroidLinkMode>
             </PropertyGroup>

         {(engine is null ? "" : $"""
             <PropertyGroup>
                     <ZigoteRoot Condition="'$(ZigoteRoot)' == '' And '$(ZIGOTE_ROOT)' != ''">$(ZIGOTE_ROOT)</ZigoteRoot>
                     <ZigoteRoot Condition="'$(ZigoteRoot)' == ''">$(MSBuildThisFileDirectory){Identifier.XmlEscape(engine)}</ZigoteRoot>
                 </PropertyGroup>
             """)}

             <ItemGroup>
                 <!-- The Android SDK's implicit usings put Android.App/Widget/Views in global scope,
                      where they collide by name with the toolkit's own Dialog, GridView and Button.
                      The app sources are shared verbatim with the desktop head, so the platform namespaces come out of global scope and the few Android-facing
                      files here import them explicitly. -->
                 <Using Remove="Android.App"/>
                 <Using Remove="Android.Widget"/>
                 <Using Remove="Android.Views"/>
                 <Using Remove="Android.Content"/>
                 <Using Remove="Android.OS"/>
                 <Using Remove="Android.Runtime"/>
             </ItemGroup>

         {(engine is null
             ? $"""
                <!-- The SDK version this head is built against — the SAME string the shared app
                         project uses, because a head whose engine package disagrees with the app's
                         managed packages fails at startup with an ABI mismatch, not at build. -->
                    <PropertyGroup>
                        <ZigoteSdkVersion Condition="'$(ZigoteSdkVersion)' == ''">{version}</ZigoteSdkVersion>
                    </PropertyGroup>

                    <ItemGroup>
                        <!-- The engine binary for this ABI, SDL's Java half, the fonts and the
                             hot-reload switch all arrive as packages: an Android build needs no Zig
                             toolchain, no NDK and no engine sources on the machine. The runtime
                             package's own .targets files the .so under the right ABI folder, which
                             is the step people get wrong by hand. -->
                        <PackageReference Include="Zigote.UI" Version="{version}"/>
                        <PackageReference Include="Zigote.UI.Adwaita" Version="{version}"/>
                        <PackageReference Include="Zigote.Android.Support" Version="{version}"/>
                        <PackageReference Include="Zigote.Engine.runtime.$(ZigTargetRid)" Version="{version}"/>
                    </ItemGroup>
                    <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                        <PackageReference Include="Zigote.UI.DevTools" Version="$(ZigoteSdkVersion)"/>
                    </ItemGroup>
                """
             : """
                <!-- The on-device hot-reload switch `zigote run` passes (-p:ZigoteHotReload=true).
                         Imported rather than restated so the head cannot drift from the engine's own heads. -->
                    <Import Project="$(ZigoteRoot)/build/Zigote.Android.targets"/>

                    <ItemGroup>
                        <ProjectReference Include="$(ZigoteRoot)/Zigote.UI/Zigote.UI.csproj"/>
                        <ProjectReference Include="$(ZigoteRoot)/Zigote.UI.Adwaita/Zigote.UI.Adwaita.csproj"/>
                    </ItemGroup>
                    <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                        <ProjectReference Include="$(ZigoteRoot)/Zigote.UI.DevTools/Zigote.UI.DevTools.csproj"/>
                    </ItemGroup>
                """)}

             <ItemGroup>
                 <!-- The desktop head's sources, minus its entry point: Android's entry point is
                      {name}Application.OnCreate, and two Mains in one assembly do not compile. -->
                 <Compile Include="../{name}/**/*.cs"
                          Exclude="../{name}/bin/**;../{name}/obj/**;../{name}/Program.cs"/>
             </ItemGroup>

         {(engine is null ? """
             <!-- The RID is mandatory, and it is the app's whole identity on a device: it names the
                     managed target AND the engine package that carries the matching .so. Two ABIs are
                     one letter apart, and the wrong one installs cleanly and dies at the first dlopen —
                     on a phone, never on the emulator it was built on. -->
                 <Target Name="RequireAndroidRid" BeforeTargets="BeforeBuild"
                         Condition="'$(DesignTimeBuild)' != 'true'">
                     <Error Condition="'$(ZigTargetRid)' != 'android-arm64' And '$(ZigTargetRid)' != 'android-x64'"
                            Text="Pass -p:ZigTargetRid=android-arm64 for a device or -p:ZigTargetRid=android-x64 for the emulator. It selects the managed RID AND the engine package the .so comes from." />
                 </Target>

                 <PropertyGroup>
                     <RuntimeIdentifier Condition="'$(ZigTargetRid)' != ''">$(ZigTargetRid)</RuntimeIdentifier>
                 </PropertyGroup>
             """ : $$"""
             <ItemGroup>
                     <!-- SDL's Java half, vendored in the engine at the tag whose native version
                          SDLActivity checks at startup, plus the activity that points SDL at
                          libzigote.so and zigote_android_main. All of SDL's JNI-registered classes must
                          reach the dex or the first JNI lookup throws. -->
                     <AndroidJavaSource Include="$(ZigoteRoot)/mobile/android/JavaSources/**/*.java">
                         <Bind>false</Bind>
                     </AndroidJavaSource>
                 </ItemGroup>

                 <!-- The ABI folder the engine lands in, derived from the RID the native build used. -->
                 <PropertyGroup>
                     <{{name}}AndroidAbi Condition="'$(ZigTargetRid)' == 'android-x64'">x86_64</{{name}}AndroidAbi>
                     <{{name}}AndroidAbi Condition="'$({{name}}AndroidAbi)' == ''">arm64-v8a</{{name}}AndroidAbi>
                     <RuntimeIdentifier Condition="'$(ZigTargetRid)' != ''">$(ZigTargetRid)</RuntimeIdentifier>
                 </PropertyGroup>

                 <!--
                   zig-out holds exactly ONE libzigote.so, for whichever target was built last, and the
                   ABI folder above is only a label. Get them out of step and the APK carries, say, an
                   x86_64 engine filed under arm64-v8a: it installs happily and then dies the moment the
                   loader touches it — on a device, never on the emulator that built it. So the RID is
                   mandatory rather than defaulted, and the arch of the actual file is checked below.
                 -->
                 <Target Name="RequireAndroidRid" BeforeTargets="BeforeBuild"
                         Condition="'$(DesignTimeBuild)' != 'true'">
                     <Error Condition="'$(ZigTargetRid)' != 'android-arm64' And '$(ZigTargetRid)' != 'android-x64'"
                            Text="Pass -p:ZigTargetRid=android-arm64 for a device or -p:ZigTargetRid=android-x64 for the emulator. It selects BOTH the managed RID and the native cross-compile; without it the two can disagree and the app crashes on load." />
                 </Target>

                 <!-- e_machine lives at byte 18 of the ELF header: 0xB7 (183) = aarch64, 0x3E (62) = x86-64. -->
                 <Target Name="CheckEngineArch" AfterTargets="BuildZigEngine"
                         Condition="'$(DesignTimeBuild)' != 'true' And Exists('$(ZigoteRoot)/Zigote.Engine/zig-out/lib/libzigote.so')">
                     <PropertyGroup>
                         <_EngineMachine>$([System.IO.File]::ReadAllBytes('$(ZigoteRoot)/Zigote.Engine/zig-out/lib/libzigote.so')[18])</_EngineMachine>
                         <_ExpectedMachine Condition="'$({{name}}AndroidAbi)' == 'arm64-v8a'">183</_ExpectedMachine>
                         <_ExpectedMachine Condition="'$({{name}}AndroidAbi)' == 'x86_64'">62</_ExpectedMachine>
                     </PropertyGroup>
                     <Error Condition="'$(_EngineMachine)' != '$(_ExpectedMachine)'"
                            Text="libzigote.so is built for ELF machine $(_EngineMachine) but is being packaged as $({{name}}AndroidAbi) (expects $(_ExpectedMachine)). The native build is stale — rebuild with -p:ZigTargetRid=$(ZigTargetRid)." />
                 </Target>

                 <ItemGroup>
                     <AndroidNativeLibrary Include="$(ZigoteRoot)/Zigote.Engine/zig-out/lib/libzigote.so">
                         <Abi>$({{name}}AndroidAbi)</Abi>
                     </AndroidNativeLibrary>
                 </ItemGroup>

                 <ItemGroup>
                     <!-- Fonts ship as Android assets; the engine opens them through FreeType by file
                          path, and an APK asset has no path — so the Application stages them out on
                          first run. Without fonts the engine cannot initialize at all. -->
                     <AndroidAsset Include="$(ZigoteRoot)/Zigote.UI/Fonts/Inter/static/Inter_18pt-Regular.ttf"
                                   Link="Fonts\Inter-Regular.ttf"/>
                     <AndroidAsset Include="$(ZigoteRoot)/Zigote.UI/Fonts/Inter/static/Inter_18pt-Medium.ttf"
                                   Link="Fonts\Inter-Medium.ttf"/>
                     <AndroidAsset Include="$(ZigoteRoot)/Zigote.UI/Fonts/Inter/static/Inter_18pt-SemiBold.ttf"
                                   Link="Fonts\Inter-SemiBold.ttf"/>
                     <AndroidAsset Include="$(ZigoteRoot)/Zigote.UI/Fonts/Inter/static/Inter_18pt-Bold.ttf"
                                   Link="Fonts\Inter-Bold.ttf"/>
                     <AndroidAsset Include="$(ZigoteRoot)/Zigote.UI/Fonts/MaterialIcons/MaterialIcons-Regular.ttf"
                                   Link="Fonts\MaterialIcons-Regular.ttf"/>
                     <AndroidAsset Include="$(ZigoteRoot)/Zigote.UI/Fonts/Noto_Emoji/static/NotoEmoji-Regular.ttf"
                                   Link="Fonts\NotoEmoji-Regular.ttf"/>
                 </ItemGroup>
             """)}

         </Project>

         """;

    // ── ios head ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     The iOS head. Smaller than the Android one for one reason: iOS has no inverted entry
    ///     point — <c>ZigoteApp.Run</c> routes through SDL's <c>UIApplicationMain</c> wrapper, so
    ///     the shared <c>Program.Main</c> is the entry point here and there is no platform class to
    ///     write. What is left is the arrangement that took a while to find: the link mode that
    ///     keeps native references, how the engine gets into the binary on a device, and an
    ///     <c>Info.plist</c> whose keys match what SDL asks for.
    /// </summary>
    public static string IosCsproj(string name, string appId, string? engine, string version)
    {
        // The engine half as a local: a raw string cannot nest another of the same quote count
        // inside its own interpolation, and three levels of doubled braces is not worth reading.
        string engineBlock = engine is null ? PackageIosBlock(version) : CheckoutIosBlock(engine);

        return $$"""
                 <Project Sdk="Microsoft.NET.Sdk">

                     <!--
                       iOS head for {{name}}: compiles the SAME sources as the desktop head, its
                       Program.Main included — on iOS the app's own entry point is the entry point,
                       and SDL owns UIApplicationMain underneath it.

                       Simulator:  dotnet build {{name}}.iOS -p:ZigTargetRid=iossimulator-arm64
                       Device:     dotnet build {{name}}.iOS -p:ZigTargetRid=ios-arm64
                       The RID must be on the COMMAND LINE: it selects the managed target AND the
                       engine build, and a plain csproj property does not reach referenced projects.
                     -->
                     <PropertyGroup>
                         <OutputType>Exe</OutputType>
                         <TargetFramework>net10.0-ios</TargetFramework>
                         <!-- 15.0 is the engine's floor: the Metal features SDL's renderer needs. -->
                         <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
                         <ImplicitUsings>enable</ImplicitUsings>
                         <Nullable>enable</Nullable>
                         <LangVersion>latest</LangVersion>
                         <RootNamespace>{{name}}</RootNamespace>
                         <ApplicationTitle>{{name}}</ApplicationTitle>
                         <ApplicationId>{{appId}}</ApplicationId>
                         <ApplicationVersion>1</ApplicationVersion>
                         <ApplicationDisplayVersion>1.0.0</ApplicationDisplayVersion>
                         <RuntimeIdentifier Condition="'$(ZigTargetRid)' != ''">$(ZigTargetRid)</RuntimeIdentifier>
                         <RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == ''">iossimulator-arm64</RuntimeIdentifier>
                         <!-- SdkOnly: app assemblies stay untrimmed (the UI stack leans on
                              reflection), but the linker pipeline still runs — which is what collects
                              @(NativeReference) for the native link. MtouchLink=None skips it and the
                              engine never reaches the linker, which surfaces as missing zigote_*
                              symbols at launch. -->
                         <MtouchLink>SdkOnly</MtouchLink>
                     </PropertyGroup>

                 {{engineBlock}}

                     <ItemGroup>
                         <!-- The desktop head's sources, entry point included. -->
                         <Compile Include="../{{name}}/**/*.cs"
                                  Exclude="../{{name}}/bin/**;../{{name}}/obj/**"/>
                     </ItemGroup>

                     <!-- libzigote.dylib arrives through several referencing projects on a simulator
                          build and the iOS SDK keeps every copy — parallel install_name_tool runs
                          then race on one output file and the build dies. Dedupe before the tool. -->
                     <Target Name="DedupeNativeLibrariesToReidentify"
                             AfterTargets="_ComputeDynamicLibrariesToReidentify"
                             BeforeTargets="_InstallNameTool">
                         <RemoveDuplicates Inputs="@(_DynamicLibraryToReidentify)">
                             <Output TaskParameter="Filtered" ItemName="_DedupedDynamicLibraryToReidentify"/>
                         </RemoveDuplicates>
                         <ItemGroup>
                             <_DynamicLibraryToReidentify Remove="@(_DynamicLibraryToReidentify)"/>
                             <_DynamicLibraryToReidentify Include="@(_DedupedDynamicLibraryToReidentify)"/>
                         </ItemGroup>
                     </Target>

                 </Project>

                 """;
    }

    /// <summary>iOS against the released SDK. The support package carries what a bundle cannot inherit.</summary>
    private static string PackageIosBlock(string version) =>
        $"""
             <PropertyGroup>
                 <ZigoteSdkVersion Condition="'$(ZigoteSdkVersion)' == ''">{version}</ZigoteSdkVersion>
             </PropertyGroup>

             <ItemGroup>
                 <!-- Zigote.iOS.Support carries the fonts as BundleResource and, for a device RID,
                      the static-archive wiring: the iOS resource pipeline does not lift a referenced
                      project's Content into the app bundle, and a bundled dylib is rejected outright
                      by AMFI on a device. Neither can be inherited the way desktop inherits them. -->
                 <PackageReference Include="Zigote.UI" Version="$(ZigoteSdkVersion)"/>
                 <PackageReference Include="Zigote.UI.Adwaita" Version="$(ZigoteSdkVersion)"/>
                 <PackageReference Include="Zigote.iOS.Support" Version="$(ZigoteSdkVersion)"/>
                 <PackageReference Include="Zigote.Engine.runtime.$(RuntimeIdentifier)" Version="$(ZigoteSdkVersion)"/>
             </ItemGroup>
         """;

    /// <summary>iOS against a checkout — the shape the engine's own heads use.</summary>
    private static string CheckoutIosBlock(string engine) =>
        $"""
             <PropertyGroup>
                 <ZigoteRoot Condition="'$(ZigoteRoot)' == '' And '$(ZIGOTE_ROOT)' != ''">$(ZIGOTE_ROOT)</ZigoteRoot>
                 <ZigoteRoot Condition="'$(ZigoteRoot)' == ''">$(MSBuildThisFileDirectory){Identifier.XmlEscape(engine)}</ZigoteRoot>
             </PropertyGroup>

             <ItemGroup>
                 <ProjectReference Include="$(ZigoteRoot)/Zigote.UI/Zigote.UI.csproj"/>
                 <ProjectReference Include="$(ZigoteRoot)/Zigote.UI.Adwaita/Zigote.UI.Adwaita.csproj"/>
             </ItemGroup>

             <ItemGroup>
                 <!-- Fonts, which the iOS resource pipeline will not take from a referenced project's
                      Content. Without them the engine cannot initialize at all. -->
                 <BundleResource Include="$(ZigoteRoot)/Zigote.UI/Fonts/Inter/static/Inter_18pt-Regular.ttf"
                                 Link="Fonts\Inter-Regular.ttf"/>
                 <BundleResource Include="$(ZigoteRoot)/Zigote.UI/Fonts/Inter/static/Inter_18pt-Medium.ttf"
                                 Link="Fonts\Inter-Medium.ttf"/>
                 <BundleResource Include="$(ZigoteRoot)/Zigote.UI/Fonts/Inter/static/Inter_18pt-SemiBold.ttf"
                                 Link="Fonts\Inter-SemiBold.ttf"/>
                 <BundleResource Include="$(ZigoteRoot)/Zigote.UI/Fonts/Inter/static/Inter_18pt-Bold.ttf"
                                 Link="Fonts\Inter-Bold.ttf"/>
                 <BundleResource Include="$(ZigoteRoot)/Zigote.UI/Fonts/MaterialIcons/MaterialIcons-Regular.ttf"
                                 Link="Fonts\MaterialIcons-Regular.ttf"/>
                 <BundleResource Include="$(ZigoteRoot)/Zigote.UI/Fonts/Noto_Emoji/static/NotoEmoji-Regular.ttf"
                                 Link="Fonts\NotoEmoji-Regular.ttf"/>
             </ItemGroup>

             <!--
               DEVICE builds link the engine INTO the app binary. AMFI rejects a loose dylib in a
               bundle ("invalid code signature"), and a device .NET app links everything else — Mono
               included — statically anyway. ForceLoad is mandatory: every zigote_* export is reached
               through runtime DllImport resolution, so the linker sees no reference to any of them
               and would drop the whole archive.
             -->
             <Target Name="AddZigoteStaticArchives" BeforeTargets="ResolveAssemblyReferences"
                     Condition="'$(RuntimeIdentifier)' == 'ios-arm64'">
                 <ItemGroup>
                     <NativeReference Include="$(ZigoteRoot)/Zigote.Engine/zig-out/lib/libzigote.a">
                         <Kind>Static</Kind>
                         <IsCxx>True</IsCxx>
                         <ForceLoad>True</ForceLoad>
                         <Frameworks>UIKit Metal QuartzCore Foundation CoreGraphics CoreVideo CoreMotion GameController CoreAudio AudioToolbox AVFoundation</Frameworks>
                         <WeakFrameworks>CoreHaptics</WeakFrameworks>
                         <LinkerFlags>-liconv -Wl,-export_dynamic</LinkerFlags>
                     </NativeReference>
                     <_ZigoteDepArchive Include="$(ZigoteRoot)/Zigote.Engine/zig-out/lib/*.a"
                                        Exclude="$(ZigoteRoot)/Zigote.Engine/zig-out/lib/libzigote.a"/>
                     <NativeReference Include="@(_ZigoteDepArchive)">
                         <Kind>Static</Kind>
                         <IsCxx>True</IsCxx>
                     </NativeReference>
                 </ItemGroup>
             </Target>
         """;

    /// <summary>
    ///     The plist. Every key here is one SDL or UIKit asks for, and the launch-screen key is the
    ///     one whose absence is invisible on a simulator and letterboxes the app on a real phone.
    /// </summary>
    public static string IosPlist(string name, string appId) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
         <plist version="1.0">
         <dict>
             <key>CFBundleName</key>
             <string>{name}</string>
             <key>CFBundleIdentifier</key>
             <string>{appId}</string>
             <!-- Modern launch screen (iOS 14+): an empty dict gives a plain system launch view;
                  with NO launch-screen key at all UIKit letterboxes the app at legacy resolutions. -->
             <key>UILaunchScreen</key>
             <dict/>
             <key>UISupportedInterfaceOrientations</key>
             <array>
                 <string>UIInterfaceOrientationPortrait</string>
                 <string>UIInterfaceOrientationLandscapeLeft</string>
                 <string>UIInterfaceOrientationLandscapeRight</string>
             </array>
             <key>UIRequiresFullScreen</key>
             <false/>
             <!-- SDL asks for this: indirect input (trackpad/mouse pointers on iPadOS, the
                  simulator's mouse) delivered as events rather than emulated touches. -->
             <key>UIApplicationSupportsIndirectInputEvents</key>
             <true/>
         </dict>
         </plist>

         """;

    public static string AndroidManifest(string appId, string name) =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <manifest xmlns:android="http://schemas.android.com/apk/res/android" package="{appId}">
             <uses-sdk android:minSdkVersion="26" android:targetSdkVersion="34"/>

             <uses-permission android:name="android.permission.INTERNET"/>

             <!-- A phone always has one; SDL's controller layer queries this and it must not become
                  an install requirement on a TV or a Chromebook. -->
             <uses-feature android:name="android.hardware.touchscreen" android:required="false"/>

             <application android:label="{name}"
                          android:hardwareAccelerated="true"
                          android:supportsRtl="true">

                 <!-- The launcher is SDL's activity subclass (pure Java): SDL owns the window, the
                      Vulkan surface and the event thread. The managed runtime is already up by then
                      — .NET for Android creates the Application object first, which is where the
                      engine gets its app-main.

                      singleInstance + alwaysRetainTaskState: relaunching from the launcher must
                      return to the running app, not start a second copy fighting the first for the
                      audio device.

                      The configChanges list is SDL's recommended set. Every one of these would
                      otherwise destroy and recreate the activity, and the app cannot re-enter main
                      twice in one process. -->
                 <activity android:name="com.zigote.app.ZigoteActivity"
                           android:label="{name}"
                           android:exported="true"
                           android:launchMode="singleInstance"
                           android:alwaysRetainTaskState="true"
                           android:configChanges="keyboard|keyboardHidden|orientation|screenSize|screenLayout|smallestScreenSize|uiMode|density|navigation|fontScale|layoutDirection|locale">
                     <intent-filter>
                         <action android:name="android.intent.action.MAIN"/>
                         <category android:name="android.intent.category.LAUNCHER"/>
                     </intent-filter>
                 </activity>
             </application>
         </manifest>

         """;

    public static string AndroidApplication(string name) =>
        $$"""
          using Android.App;
          using Android.Runtime;
          using Zigote.Core.Native;

          namespace {{name}};

          /// <summary>
          ///     The Android entry point.
          ///     <para>
          ///         Android inverts the entry point the opposite way from a desktop: Java owns the
          ///         process, and SDL's <c>nativeRunMain</c> looks <c>zigote_android_main</c> up out of
          ///         libzigote.so and runs it on the SDL thread. That native function needs a managed
          ///         callback to invoke, and it can only get one if managed code has already run —
          ///         which is what an Application subclass guarantees: .NET for Android initializes
          ///         the runtime and calls <see cref="OnCreate" /> before the launcher activity is
          ///         created.
          ///     </para>
          ///     <para>
          ///         So this is the phone's <c>Program.Main</c>. It does not run the app — SDL does
          ///         that, later, on its own thread.
          ///     </para>
          /// </summary>
          [Application(Label = "{{name}}", Theme = "@android:style/Theme.Material.NoActionBar")]
          public class {{name}}Application : Application
          {
              /// <summary>The one place Android-side code can reach an app context from.</summary>
              public static Application Instance { get; private set; } = null!;

              public {{name}}Application(IntPtr handle, JniHandleOwnership ownership)
                  : base(handle, ownership)
              {
              }

              public override void OnCreate()
              {
                  base.OnCreate();
                  Instance = this;
                  StageFonts();
                  MobileHost.SetAndroidMain(() => new {{name}}App().Run());
              }

              /// <summary>
              ///     Copy the font assets out of the APK into <c>Fonts/</c> under the app's base
              ///     directory. The engine opens fonts through FreeType with a plain file path, and an
              ///     APK asset has no such path — it lives compressed inside the package. Runs once per
              ///     install; an existing file is left alone.
              /// </summary>
              private void StageFonts()
              {
                  try
                  {
                      var dir = Path.Combine(AppContext.BaseDirectory, "Fonts");
                      Directory.CreateDirectory(dir);
                      foreach (var asset in Assets?.List("Fonts") ?? [])
                      {
                          var target = Path.Combine(dir, asset);
                          // AssetManager streams do not report Length, so an existing file is trusted.
                          if (File.Exists(target)) continue;
                          using var src = Assets!.Open($"Fonts/{asset}");
                          using var dst = File.Create(target);
                          src.CopyTo(dst);
                      }
                  }
                  catch (Exception ex)
                  {
                      // Without fonts the engine cannot initialize, so make the reason obvious in
                      // logcat rather than letting it surface as an opaque FreeType failure.
                      global::Android.Util.Log.Error("{{name.ToLowerInvariant()}}", $"font staging failed: {ex}");
                  }
              }
          }

          """;

    // ── plugin ────────────────────────────────────────────────────────────────

    public static string PluginCsproj(string name, string? engine, string version, bool android, bool ios)
    {
        string tfms = "net10.0"
                      + (android ? ";net10.0-android" : "")
                      + (ios ? ";net10.0-ios" : "");
        // The floors match the engine's own: AAudio needs Android 26, the iOS head targets 15.0.
        string floors =
            (android
                ? "\n        <SupportedOSPlatformVersion Condition=\"'$(TargetFramework)' == 'net10.0-android'\">26</SupportedOSPlatformVersion>"
                : "")
            + (ios
                ? "\n        <SupportedOSPlatformVersion Condition=\"'$(TargetFramework)' == 'net10.0-ios'\">15.0</SupportedOSPlatformVersion>"
                : "");
        string platformIncludes =
            (android
                ? "\n        <Compile Include=\"Platforms/Android/**/*.cs\" Condition=\"'$(TargetFramework)' == 'net10.0-android'\"/>"
                : "")
            + (ios
                ? "\n        <Compile Include=\"Platforms/iOS/**/*.cs\" Condition=\"'$(TargetFramework)' == 'net10.0-ios'\"/>"
                : "");

        return $"""
                <Project Sdk="Microsoft.NET.Sdk">

                    <!--
                      Platform plugin {name}: one shared API over Zigote's PlatformChannel, with the
                      per-platform implementation chosen at BUILD time — each target framework compiles
                      exactly one Platforms/ folder, and NuGet hands every consumer the build matching
                      its head. There is no runtime platform switch anywhere, and there must not be:
                      the whole point of the channel is that shared code never knows who answered.
                    -->
                    <PropertyGroup>
                        <TargetFrameworks>{tfms}</TargetFrameworks>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                        <LangVersion>latest</LangVersion>
                        <RootNamespace>{name}</RootNamespace>{floors}
                        <!-- `dotnet pack` metadata — the plugin ships as a NuGet package as-is. -->
                        <Version>0.1.0</Version>
                        <Description>A Zigote platform plugin.</Description>
                    </PropertyGroup>

                    {(engine is null
                        ? $"""
                           <PropertyGroup>
                                   <ZigoteSdkVersion Condition="'$(ZigoteSdkVersion)' == ''">{version}</ZigoteSdkVersion>
                               </PropertyGroup>

                               <ItemGroup>
                                   <!-- Plugins talk to the platform through channels, so Core is the only
                                        reference. A plugin that also ships widgets is two packages: this one,
                                        and a UI package built over it — keeping the platform half usable from
                                        apps that do not want the widgets. -->
                                   <PackageReference Include="Zigote.Core" Version="$(ZigoteSdkVersion)"/>
                               </ItemGroup>
                           """
                        : $"""
                           <!-- Where the engine lives. Override with -p:ZigoteRoot=<path> or $ZIGOTE_ROOT. -->
                               <PropertyGroup>
                                   <ZigoteRoot Condition="'$(ZigoteRoot)' == '' And '$(ZIGOTE_ROOT)' != ''">$(ZIGOTE_ROOT)</ZigoteRoot>
                                   <ZigoteRoot Condition="'$(ZigoteRoot)' == ''">$(MSBuildThisFileDirectory){Identifier.XmlEscape(engine)}</ZigoteRoot>
                               </PropertyGroup>

                               <ItemGroup>
                                   <ProjectReference Include="$(ZigoteRoot)/Zigote.Core/Zigote.Core.csproj"/>
                               </ItemGroup>
                           """)}

                    <ItemGroup>
                        <!-- Exactly one platform implementation per framework: every Platforms/ file
                             leaves the default glob first, then the right folder returns for the
                             framework being compiled. All implementations share one class name, which
                             is what lets the shared {name}Plugin.Start() bind statically. -->
                        <Compile Remove="Platforms/**/*.cs"/>
                        <Compile Include="Platforms/Desktop/**/*.cs" Condition="'$(TargetFramework)' == 'net10.0'"/>{platformIncludes}
                    </ItemGroup>

                </Project>

                """;
    }

    public static string PluginShared(string name) =>
        $$"""
          using Zigote.Core.Platform;

          namespace {{name}};

          /// <summary>
          ///     {{name}} — a Zigote platform plugin.
          ///     <para>
          ///         This file is the plugin's shared face: it compiles identically for every target
          ///         framework, and the <c>{{name}}Channels</c> class it starts comes from
          ///         <c>Platforms/</c>, where the csproj selects exactly one implementation per
          ///         framework. Consumers register the plugin once, in each head, before the App:
          ///         <c>PluginHost.Register(new {{name}}Plugin());</c>
          ///     </para>
          /// </summary>
          public sealed class {{name}}Plugin : IPlatformPlugin
          {
              /// <summary>
              ///     Channel names are the plugin's wire surface — prefixed with the plugin's identity
              ///     so two plugins can never collide, and stable once shipped, because a native
              ///     implementation registers the same string.
              /// </summary>
              internal const string VersionChannel = "{{name.ToLowerInvariant()}}/version";

              public string Name => "{{name.ToLowerInvariant()}}";

              public void Start() => {{name}}Channels.Register();

              public void Stop() => {{name}}Channels.Unregister();

              /// <summary>
              ///     The template's example API: the OS name and version, answered by whichever
              ///     platform implementation was compiled in. Null when nothing implements it here —
              ///     callers carry on without it. Replace this (and the handler in each Platforms/
              ///     file) with the plugin's real surface; for structured payloads use
              ///     <c>JsonChannel&lt;TArgs, TReply&gt;</c>, and for answers that need a dialog or a
              ///     permission prompt use <c>PlatformChannel.Request</c> — see docs/plugins.md in the
              ///     Zigote checkout.
              /// </summary>
              public static string? GetPlatformVersion() => PlatformChannel.Invoke(VersionChannel);
          }

          """;

    public static string PluginDesktopChannels(string name) =>
        $$"""
          using System.Runtime.InteropServices;
          using Zigote.Core.Platform;

          namespace {{name}};

          /// <summary>
          ///     Desktop implementation — Windows, macOS and Linux, the base <c>net10.0</c> build.
          ///     Every Platforms/ folder defines a class with this exact name; the csproj compiles one
          ///     of them per target framework, so the shared plugin binds to the right one with no
          ///     runtime check. Handlers run on the invoking thread and must not block — work that
          ///     takes time reports back with <c>PlatformChannel.Send</c> or a request handler.
          /// </summary>
          internal static class {{name}}Channels
          {
              public static void Register() =>
                  PlatformChannel.Handle(
                      channel: {{name}}Plugin.VersionChannel,
                      handler: _ => RuntimeInformation.OSDescription
                  );

              public static void Unregister() =>
                  PlatformChannel.Unhandle({{name}}Plugin.VersionChannel);
          }

          """;

    public static string PluginAndroidChannels(string name) =>
        $$"""
          using Zigote.Core.Platform;

          namespace {{name}};

          /// <summary>
          ///     Android implementation (<c>net10.0-android</c>). Anything the Android SDK offers is
          ///     reachable from here — and Kotlin/Java sources can ride along this project as
          ///     <c>AndroidJavaSource</c> items, registering their own channels natively via
          ///     <c>zigote_channel_register</c> when the work belongs in Java.
          /// </summary>
          internal static class {{name}}Channels
          {
              public static void Register() =>
                  PlatformChannel.Handle(
                      channel: {{name}}Plugin.VersionChannel,
                      handler: _ => $"Android {global::Android.OS.Build.VERSION.Release}"
                  );

              public static void Unregister() =>
                  PlatformChannel.Unhandle({{name}}Plugin.VersionChannel);
          }

          """;

    public static string PluginIosChannels(string name) =>
        $$"""
          using Zigote.Core.Platform;

          namespace {{name}};

          /// <summary>
          ///     iOS implementation (<c>net10.0-ios</c>). UIKit and the rest of the platform bindings
          ///     are reachable from here; Swift/Objective-C libraries arrive as binding projects, and
          ///     can also register channels natively via <c>zigote_channel_register</c>.
          /// </summary>
          internal static class {{name}}Channels
          {
              public static void Register() =>
                  PlatformChannel.Handle(
                      channel: {{name}}Plugin.VersionChannel,
                      handler: _ => $"iOS {UIKit.UIDevice.CurrentDevice.SystemVersion}"
                  );

              public static void Unregister() =>
                  PlatformChannel.Unhandle({{name}}Plugin.VersionChannel);
          }

          """;

    public static string PluginExampleProgram(string name) =>
        $$"""
          using {{name}};
          using Zigote.Core.Platform;

          namespace {{name}}Example;

          /// <summary>The desktop entry point.</summary>
          public static class Program
          {
              public static void Main(string[] args)
              {
                  // The one line any consumer adds per plugin: registered before the App exists, so
                  // the plugin starts with the app and its channels are live by the first frame.
                  PluginHost.Register(new {{name}}Plugin());
                  new {{name}}ExampleApp().Run();
              }
          }

          """;

    public static string PluginExampleShell(string name) =>
        $$"""
          using {{name}};
          using Zigote.Core.Paint;
          using Zigote.UI.Adwaita;
          using Zigote.UI.Theme;
          using Zigote.UI.Widgets;
          using Zigote.UI.Widgets.Controls;
          using Zigote.UI.Widgets.Layout;

          namespace {{name}}Example;

          /// <summary>The example app: proves the plugin end to end on every platform it targets.</summary>
          public sealed class {{name}}ExampleApp : AdwaitaApp
          {
              public {{name}}ExampleApp() : base(
                  home: new HomePage(),
                  title: "{{name}} example",
                  theme: AdwTheme.Dark
              )
              {
                  Width = 420;
                  Height = 720;
              }
          }

          internal sealed class HomePage : ComposedWidget
          {
              protected override Widget Build(BuildContext context)
              {
                  var theme = ThemeProvider.Of(context);

                  return new SafeArea(new AdwToolbarView(
                      new Center(
                          new Column(
                              mainAxisAlignment: MainAxisAlignment.Center,
                              children:
                              [
                                  new Text(
                                      "GetPlatformVersion() says:",
                                      new TextStyle(color: theme.TextSecondary)
                                  ),
                                  new SizedBox(height: 8),
                                  new Text(
                                      {{name}}Plugin.GetPlatformVersion() ?? "not implemented here",
                                      new TextStyle(24, fontWeight: FontWeight.SemiBold)
                                  )
                              ]
                          )
                      )
                  ) { TopBars = { new AdwHeaderBar { Title = "{{name}}" } } });
              }
          }

          """;

    public static string PluginReadme(string name, bool android, bool ios) =>
        $"""
         # {name}

         A [Zigote](https://github.com/zigote) platform plugin: desktop (Windows/macOS/Linux){(android ? ", Android" : "")}{(ios ? ", iOS" : "")}.

         ## Try it

         ```
         cd example && zigote run
         ```

         ## Use it in an app

         Reference the project (or the packed NuGet package) from each head, and register the
         plugin before constructing the App:

         ```csharp
         PluginHost.Register(new {name}Plugin());
         new MyApp().Run();
         ```

         ```csharp
         string? version = {name}Plugin.GetPlatformVersion();
         ```

         ## Layout

         | Path | What it is |
         |---|---|
         | `{name}/{name}Plugin.cs` | The shared API and plugin contract — identical on every platform. |
         | `{name}/Platforms/` | One folder per platform; the csproj compiles exactly one per target framework. |
         | `example/` | An app wired to the plugin, for developing and demoing it. |

         ## Ship it

         ```
         dotnet pack {name}
         ```

         The package multi-targets, so consumers get the implementation matching their head
         automatically. For the full interop story — typed `JsonChannel`s, awaitable requests,
         binary payloads, native (C/Zig/Rust) plugins — see `docs/plugins.md` in the Zigote
         checkout.

         """;
}
