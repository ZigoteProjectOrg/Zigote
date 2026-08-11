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
    public static string AppCsproj(string name, string engine) =>
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
             </PropertyGroup>

             <!-- Where the engine lives. Override with -p:ZigoteRoot=<path> or $ZIGOTE_ROOT. -->
             <PropertyGroup>
                 <ZigoteRoot Condition="'$(ZigoteRoot)' == '' And '$(ZIGOTE_ROOT)' != ''">$(ZIGOTE_ROOT)</ZigoteRoot>
                 <ZigoteRoot Condition="'$(ZigoteRoot)' == ''">$(MSBuildThisFileDirectory){engine}</ZigoteRoot>
             </PropertyGroup>

             <ItemGroup>
                 <ProjectReference Include="$(ZigoteRoot)/Zigote.UI/Zigote.UI.csproj"/>
                 <ProjectReference Include="$(ZigoteRoot)/Zigote.UI.Material/Zigote.UI.Material.csproj"/>
             </ItemGroup>

             <!-- Debug-only: the Shift+D widget inspector and perf overlay. A release must not
                  carry the HUD, and the DEBUG-gated Install call compiles out with it. -->
             <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                 <ProjectReference Include="$(ZigoteRoot)/Zigote.UI.DevTools/Zigote.UI.DevTools.csproj"/>
             </ItemGroup>

         </Project>

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
          using Zigote.UI.Material;
          using Zigote.UI.Theme;
          using Zigote.UI.Widgets;
          using Zigote.UI.Widgets.Controls;
          using Zigote.UI.Widgets.Layout;

          namespace {{name}};

          /// <summary>
          ///     The app. Everything here is shared by every platform it ships on — a platform head
          ///     (see <c>zigote add android</c>) starts this same class and adds nothing to it.
          /// </summary>
          public sealed class {{name}}App : MaterialApp
          {
              public {{name}}App() : base(
                  home: new HomePage(),
                  title: "{{name}}",
                  theme: ThemeData.Dark
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

                  return new SafeArea(new Scaffold(
                      new AppBar(new Text("{{name}}"), centerTitle: true),
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
                                  ))
                              ]
                          )
                      ),
                      new FloatingActionButton(
                          () => _taps.Value++,
                          new Icon(MaterialIcons.Add),
                          tooltip: "Tap"
                      )
                  ));
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
         dotnet run --project {name}
         ```

         ## Android

         ```
         zigote add android
         dotnet build {name}.Android -p:ZigTargetRid=android-arm64   # device
         dotnet build {name}.Android -p:ZigTargetRid=android-x64     # emulator
         ```

         The RID is not optional. It selects the managed runtime identifier *and* the engine's
         native cross-compile, and `zig-out` holds only one `libzigote.so` at a time — so building
         without it can package one architecture's engine under another's folder, which installs
         cleanly and then dies on load. The generated project refuses to build without it.

         """;

    // ── android head ──────────────────────────────────────────────────────────

    public static string AndroidCsproj(string name, string appId, string engine) =>
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

             <PropertyGroup>
                 <ZigoteRoot Condition="'$(ZigoteRoot)' == '' And '$(ZIGOTE_ROOT)' != ''">$(ZIGOTE_ROOT)</ZigoteRoot>
                 <ZigoteRoot Condition="'$(ZigoteRoot)' == ''">$(MSBuildThisFileDirectory){engine}</ZigoteRoot>
             </PropertyGroup>

             <ItemGroup>
                 <!-- The Android SDK's implicit usings put Android.App/Widget/Views in global scope,
                      where they collide by name with the toolkit's own Dialog, Switch, GridView,
                      Spinner and Toolbar. The app sources are shared verbatim with the desktop head,
                      so the platform namespaces come out of global scope and the few Android-facing
                      files here import them explicitly. -->
                 <Using Remove="Android.App"/>
                 <Using Remove="Android.Widget"/>
                 <Using Remove="Android.Views"/>
                 <Using Remove="Android.Content"/>
                 <Using Remove="Android.OS"/>
                 <Using Remove="Android.Runtime"/>
             </ItemGroup>

             <ItemGroup>
                 <ProjectReference Include="$(ZigoteRoot)/Zigote.UI/Zigote.UI.csproj"/>
                 <ProjectReference Include="$(ZigoteRoot)/Zigote.UI.Material/Zigote.UI.Material.csproj"/>
             </ItemGroup>
             <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                 <ProjectReference Include="$(ZigoteRoot)/Zigote.UI.DevTools/Zigote.UI.DevTools.csproj"/>
             </ItemGroup>

             <ItemGroup>
                 <!-- The desktop head's sources, minus its entry point: Android's entry point is
                      {name}Application.OnCreate, and two Mains in one assembly do not compile. -->
                 <Compile Include="../{name}/**/*.cs"
                          Exclude="../{name}/bin/**;../{name}/obj/**;../{name}/Program.cs"/>
             </ItemGroup>

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
                 <{name}AndroidAbi Condition="'$(ZigTargetRid)' == 'android-x64'">x86_64</{name}AndroidAbi>
                 <{name}AndroidAbi Condition="'$({name}AndroidAbi)' == ''">arm64-v8a</{name}AndroidAbi>
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
                     <_ExpectedMachine Condition="'$({name}AndroidAbi)' == 'arm64-v8a'">183</_ExpectedMachine>
                     <_ExpectedMachine Condition="'$({name}AndroidAbi)' == 'x86_64'">62</_ExpectedMachine>
                 </PropertyGroup>
                 <Error Condition="'$(_EngineMachine)' != '$(_ExpectedMachine)'"
                        Text="libzigote.so is built for ELF machine $(_EngineMachine) but is being packaged as $({name}AndroidAbi) (expects $(_ExpectedMachine)). The native build is stale — rebuild with -p:ZigTargetRid=$(ZigTargetRid)." />
             </Target>

             <ItemGroup>
                 <AndroidNativeLibrary Include="$(ZigoteRoot)/Zigote.Engine/zig-out/lib/libzigote.so">
                     <Abi>$({name}AndroidAbi)</Abi>
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

         </Project>

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
}
