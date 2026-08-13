using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Zigote.Editor.Prefab;
using Zigote.Editor.Vfx;
using Zigote.Runtime.Content;
using Zigote.Runtime.Prefab;
using Zigote.Runtime.Scene;
using Zigote.Vfx;

namespace Zigote.Editor.Export;

/// <summary>
///     Builds a distributable game from an open project: stages content (with VFX graphs baked to
///     <see cref="VfxAssetJson" />), generates a player csproj + static script registration, publishes
///     per RID (the Zig native lib cross-compiles via build/Zigote.Native.targets), and packages the
///     result (.app on macOS, plain folder elsewhere). No UI types — hosts feed an IProgress log.
/// </summary>
public static class GameExporter
{
    public static async Task<bool> ExportAsync(ExportInput input, ExportOptions options,
        IProgress<string> log,
        IProgress<ExportJobUpdate>? jobs = null)
    {
        string projDir = Path.GetDirectoryName(Path.GetFullPath(input.ProjectPath))!;
        string name = string.IsNullOrWhiteSpace(input.Project.Name)
            ? "ZigoteGame"
            : input.Project.Name.Trim();
        string exeName = SanitizeExeName(name);

        string? sdkRoot = FindSdkRoot();
        if (sdkRoot is null)
        {
            log.Report(
                "Export failed: Zigote SDK root not found (set ZIGOTE_SDK to the repo root)."
            );
            return false;
        }

        // Stage under the system temp dir, not the project: staging churns thousands of files, and
        // doing that inside the project tree spams IDE/file watchers (and pollutes the project).
        // macOS TMPDIR is a /var → /private/var symlink; MSBuild resolves project-reference relative
        // paths across it incorrectly, so pin the real path.
        string tempRoot = Path.GetTempPath();
        if (OperatingSystem.IsMacOS() && tempRoot.StartsWith("/var/"))
            tempRoot = "/private" + tempRoot;
        string staging = Path.Combine(path1: tempRoot, path2: "zigote-export", path3: exeName);
        if (Directory.Exists(staging)) Directory.Delete(path: staging, recursive: true);
        Directory.CreateDirectory(staging);

        log.Report($"Staging content from {input.Project.AssetRoot}/ …");
        string contentDir = Path.Combine(path1: staging, path2: "Content");
        var stagedScene = StageContent(
            input: input,
            projDir: projDir,
            contentDir: contentDir,
            log: log
        );

        log.Report("Generating player project …");
        var sceneScripts = CollectScriptClasses(stagedScene);

        Directory.CreateDirectory(options.OutputDir);
        bool ok = true;
        foreach (string rid in options.Rids)
        foreach (var mode in options.Modes)
        {
            // Regenerated per RID: mobile heads need a platform target framework and
            // SDK-specific items (the iOS static-link native references), which desktop must not
            // carry. Writing the project is a few hundred lines of text — cheap to redo.
            GeneratePlayerProject(
                input: input,
                sdkRoot: sdkRoot,
                playerDir: Path.Combine(path1: staging, path2: "player"),
                exeName: exeName,
                sceneScriptClasses: sceneScripts,
                rid: rid
            );

            var job = new ExportJob(Rid: rid, Mode: mode);
            if (IsMobile(rid) && !MobileHostAvailable(rid: rid, reason: out string why))
            {
                log.Report($"[{rid}] {why} — skipped.");
                continue;
            }

            if (mode == ExportMode.NativeAot && !CanAotFor(rid))
            {
                // Cross-OS AOT is impossible. When JIT is also requested the platform is already
                // covered — skip; otherwise fall back so the platform ships something.
                if (options.Modes.Contains(ExportMode.SelfContained))
                {
                    log.Report(
                        $"[{rid}] NativeAOT needs a {RidOs(rid)} host — skipped (JIT build covers this platform)."
                    );
                    jobs?.Report(
                        new ExportJobUpdate(
                            Job: job,
                            State: ExportJobState.Skipped,
                            Detail: "needs matching host OS"
                        )
                    );
                    continue;
                }

                log.Report(
                    $"[{rid}] NativeAOT unavailable from this host OS — falling back to self-contained JIT."
                );
            }

            jobs?.Report(new ExportJobUpdate(Job: job, State: ExportJobState.Running));
            bool aot = mode == ExportMode.NativeAot && CanAotFor(rid);
            bool jobOk = await PublishAndPackageAsync(
                options: options,
                staging: staging,
                rid: rid,
                exeName: exeName,
                name: name,
                aot: aot,
                log: log
            );
            jobs?.Report(
                new ExportJobUpdate(
                    Job: job,
                    State: jobOk ? ExportJobState.Succeeded : ExportJobState.Failed
                )
            );
            ok &= jobOk;
        }

        log.Report(ok ? $"Export finished → {options.OutputDir}" : "Export finished with errors.");
        return ok;
    }

    private static string RidOs(string rid)
    {
        if (rid.StartsWith("osx")) return "macOS";
        if (rid.StartsWith("win")) return "Windows";
        if (rid.StartsWith("ios")) return "iOS";
        if (rid.StartsWith("android")) return "Android";
        return "Linux";
    }

    /// <summary>
    ///     A mobile RID — the app is packaged by the platform SDK, not by copying a publish
    ///     directory, and the managed head targets a platform TFM rather than plain net10.0.
    /// </summary>
    private static bool IsMobile(string rid) => rid.StartsWith("ios") || rid.StartsWith("android");

    /// <summary>
    ///     Target framework for the generated player project. Mobile needs the platform TFM so the
    ///     iOS/Android SDK targets (app packaging, the native-reference pipeline, the registrar)
    ///     participate at all.
    /// </summary>
    private static string RidTfm(string rid)
    {
        if (rid.StartsWith("ios")) return "net10.0-ios";
        if (rid.StartsWith("android")) return "net10.0-android";
        return "net10.0";
    }

    /// <summary>
    ///     Whether this host can package for a mobile RID at all: iOS needs the Apple toolchain
    ///     (macOS + Xcode), Android needs the NDK the native build reads. The managed workload
    ///     (`dotnet workload install ios|android`) is checked by the publish itself, which fails
    ///     with a clear message — this only screens what cannot work in principle.
    /// </summary>
    private static bool MobileHostAvailable(string rid, out string reason)
    {
        if (rid.StartsWith("ios") && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            reason = "iOS packaging requires a macOS host with Xcode";
            return false;
        }

        if (rid.StartsWith("android"))
        {
            string? ndk = Environment.GetEnvironmentVariable("ANDROID_NDK_ROOT");
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Android",
                "sdk",
                "ndk"
            );
            if (string.IsNullOrEmpty(ndk) && !Directory.Exists(fallback))
            {
                reason = "Android packaging requires the NDK (set ANDROID_NDK_ROOT)";
                return false;
            }
        }

        reason = "";
        return true;
    }

    // ── Staging ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Stage the shipped manifest, the (VFX-baked) scene, and — via
    ///     <see cref="AssetDependencyGraph" /> — only the asset files the scene actually reaches,
    ///     instead of the whole asset tree. Returns the staged scene for script-class collection.
    /// </summary>
    private static SceneGraph StageContent(ExportInput input, string projDir, string contentDir,
        IProgress<string> log)
    {
        // The shipped manifest points at the staged scene and carries no script project — the player
        // gets scripts statically, via the generated registration.
        Directory.CreateDirectory(contentDir);
        var shipped = ZigoteProject.Load(input.ProjectPath);
        shipped.ScriptProject = null;
        shipped.Save(Path.Combine(path1: contentDir, path2: "game.zigoteproj"));

        string sceneSrc = Path.Combine(path1: projDir, path2: shipped.StartupScene);
        string sceneDst = Path.Combine(path1: contentDir, path2: shipped.StartupScene);
        Directory.CreateDirectory(Path.GetDirectoryName(sceneDst)!);
        File.Copy(sourceFileName: sceneSrc, destFileName: sceneDst);
        BakeVfxGraphs(scenePath: sceneDst, log: log);

        // Reachability-based staging: everything the (baked) scene references, nothing else.
        // Paths are canonical (project-relative, '/') since import writes them that way; the rooted
        // guard in the copy loop below is the backstop for hand-edited scenes.
        var scene = SceneGraph.Load(sceneDst);
        var graph = AssetDependencyGraph.Build(scene);

        // Runtime-spawnable prefabs: ship every .prefab under assets/prefabs — World.Spawn loads them
        // by path at play time, so reachability can't be derived from the scene (the reference lives
        // in script code) — and fold their templates' asset references into the staging graph.
        string prefabRoot = Path.Combine(path1: projDir, path2: PrefabService.PrefabDir);
        int prefabs = 0;
        if (Directory.Exists(prefabRoot))
        {
            foreach (string prefabSrc in Directory.EnumerateFiles(
                         path: prefabRoot,
                         searchPattern: "*" + PrefabDocument.Extension,
                         searchOption: SearchOption.AllDirectories
                     ))
            {
                string rel = Path.GetRelativePath(relativeTo: projDir, path: prefabSrc)
                    .Replace(oldChar: '\\', newChar: '/');
                string prefabDst = Path.Combine(path1: contentDir, path2: rel);
                Directory.CreateDirectory(Path.GetDirectoryName(prefabDst)!);
                CopyShared(src: prefabSrc, dst: prefabDst);
                if (PrefabDocument.Load(prefabSrc) is { } prefabDoc)
                    graph.AddTree(prefabDoc.Template);
                prefabs++;
            }
        }

        if (prefabs > 0) log.Report($"Staged {prefabs} prefab(s) for runtime spawning.");

        long stagedBytes = 0;
        int staged = 0;
        int missing = 0;
        foreach (string path in graph.Files)
        {
            if (Path.IsPathRooted(path))
            {
                // Survived normalization = points outside the project; unshippable by definition.
                log.Report($"  ! unportable absolute path (skipped): {path}");
                missing++;
                continue;
            }

            string src = Path.Combine(path1: projDir, path2: path);
            if (!File.Exists(src))
            {
                // Missing on disk = already broken in the editor; surface it rather than fail the export.
                log.Report($"  ! missing asset (skipped): {path}");
                missing++;
                continue;
            }

            // Keep the scene's own spelling for the destination: the runtime opens files by that string,
            // and source filesystems may be case-insensitive while the target machine's is not.
            string dst = Path.Combine(path1: contentDir, path2: path);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            // Engine-native binaries the C# runtime reads itself ship zstd-compressed (ContentFiles
            // resolves '<file>.zst' transparently). Textures/audio are opened natively by path — and
            // are already-compressed formats — so they stay loose.
            string ext = Path.GetExtension(path).ToLowerInvariant();
            stagedBytes += ext is ".zmesh" or ".hdr"
                ? ContentFiles.WriteCompressed(src: src, dst: dst)
                : CopyShared(src: src, dst: dst);
            staged++;
        }

        long totalBytes =
            DirectorySize(Path.Combine(path1: projDir, path2: input.Project.AssetRoot));
        log.Report(
            $"Staged {staged} reachable asset file(s), {stagedBytes / (1024.0 * 1024.0):F1} MB " +
            $"(asset tree is {totalBytes / (1024.0 * 1024.0):F1} MB" +
            (missing > 0 ? $"; {missing} missing reference(s)" : "") + ")."
        );

        // Registry travels with the content (asset-id lookups stay valid for future streaming use).
        string registry = Path.Combine(path1: projDir, path2: "assets.registry");
        if (File.Exists(registry))
        {
            File.Copy(
                sourceFileName: registry,
                destFileName: Path.Combine(path1: contentDir, path2: "assets.registry")
            );
        }

        return scene;
    }

    internal static HashSet<string> CollectScriptClasses(SceneGraph scene)
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);

        void Walk(SceneNode node)
        {
            if (!string.IsNullOrEmpty(node.ScriptClass)) classes.Add(node.ScriptClass);
            foreach (var c in node.Children) Walk(c);
        }

        Walk(scene.Root);
        return classes;
    }

    /// <summary>
    ///     Copy tolerant of transient EAGAIN locks: IDE indexers / Spotlight briefly take
    ///     exclusive locks while scanning (the staging wipe itself triggers a re-scan storm), and a
    ///     source file may be open in the running editor. Reads with full sharing + bounded retries.
    /// </summary>
    private static long CopyShared(string src, string dst)
    {
        const int maxAttempts = 10;
        for (int attempt = 1;; attempt++)
        {
            try
            {
                using var s = new FileStream(
                    path: src,
                    mode: FileMode.Open,
                    access: FileAccess.Read,
                    share: FileShare.ReadWrite | FileShare.Delete
                );
                using var d = new FileStream(
                    path: dst,
                    mode: FileMode.Create,
                    access: FileAccess.Write,
                    share: FileShare.None
                );
                s.CopyTo(d);
                return s.Length;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static long DirectorySize(string dir)
    {
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(
                    path: dir,
                    searchPattern: "*",
                    searchOption: SearchOption.AllDirectories
                )
                .Sum(f => new FileInfo(f).Length)
            : 0;
    }

    /// <summary>
    ///     Replace each VFX emitter's node graph with its compiled emitter asset so the player
    ///     never needs the graph compiler (Zigote.Graphs.* stays editor-only).
    /// </summary>
    internal static void BakeVfxGraphs(string scenePath, IProgress<string> log)
    {
        if (!File.Exists(scenePath)) return;
        var scene = SceneGraph.Load(scenePath);
        int baked = 0;

        void Walk(SceneNode node)
        {
            if (node.Kind == NodeKind.VfxEmitter)
            {
                node.VfxBakedJson = VfxAssetJson.Serialize(VfxNodeEditor.Compile(node).Asset);
                node.VfxGraphJson = null;
                baked++;
            }

            foreach (var c in node.Children) Walk(c);
        }

        Walk(scene.Root);
        if (baked > 0)
        {
            scene.Save(scenePath);
            log.Report($"Baked {baked} VFX emitter graph(s).");
        }
    }

    // ── Generated player project ──────────────────────────────────────────────

    internal static void GeneratePlayerProject(ExportInput input, string sdkRoot, string playerDir,
        string exeName,
        IReadOnlySet<string>? sceneScriptClasses = null,
        string rid = "")
    {
        Directory.CreateDirectory(playerDir);

        string? scriptCsproj = input.Project.ScriptProject is { Length: > 0 } sp
            ? Path.GetFullPath(
                Path.Combine(path1: Path.GetDirectoryName(input.ProjectPath)!, path2: sp)
            )
            : null;
        string? scriptAsm = input.ScriptAssemblyName
                            ?? (scriptCsproj is null
                                ? null
                                : Path.GetFileNameWithoutExtension(scriptCsproj));

        string name = string.IsNullOrWhiteSpace(input.Project.Name) ? exeName : input.Project.Name;
        var csproj = new StringBuilder();
        csproj.AppendLine("""<Project Sdk="Microsoft.NET.Sdk">""");
        csproj.AppendLine("    <PropertyGroup>");
        csproj.AppendLine("        <OutputType>Exe</OutputType>");
        csproj.AppendLine($"        <TargetFramework>{RidTfm(rid)}</TargetFramework>");
        // Staged outside the repo, so it can't inherit Directory.Build.props — pin the same C# level.
        csproj.AppendLine("        <LangVersion>14.0</LangVersion>");
        if (IsMobile(rid))
        {
            // The app identity the platform installers key on, plus the phone-shaped defaults.
            csproj.AppendLine($"        <ApplicationTitle>{name}</ApplicationTitle>");
            csproj.AppendLine(
                $"        <ApplicationId>{BundleId(input.Project.Name)}</ApplicationId>"
            );
            csproj.AppendLine($"        <RuntimeIdentifier>{rid}</RuntimeIdentifier>");
        }

        if (rid.StartsWith("ios"))
        {
            csproj.AppendLine(
                "        <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>"
            );
            // SdkOnly, never None: @(NativeReference) is collected by the managed linker pipeline,
            // and the engine archives would silently never reach the native link without it.
            csproj.AppendLine("        <MtouchLink>SdkOnly</MtouchLink>");
        }

        if (rid.StartsWith("android"))
        {
            csproj.AppendLine(
                "        <SupportedOSPlatformVersion>26</SupportedOSPlatformVersion>"
            );
            csproj.AppendLine("        <AndroidPackageFormat>apk</AndroidPackageFormat>");
            // Assemblies INSIDE the apk: a hand-installed apk (adb install) otherwise aborts at
            // startup with "No assemblies found" (Fast Deployment pushes them out-of-band).
            csproj.AppendLine("        <EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>");
            // The player leans on reflection (DevTools late-bind); don't chase trimmer ghosts.
            csproj.AppendLine("        <AndroidLinkMode>None</AndroidLinkMode>");
            // Release defaults to profiled Mono AOT, which trips the class-init instance_size
            // assertion at startup (explicit-layout FFI structs); this is the JIT flavor anyway.
            csproj.AppendLine("        <RunAOTCompilation>false</RunAOTCompilation>");
            csproj.AppendLine("        <AndroidEnableProfiledAot>false</AndroidEnableProfiledAot>");
            csproj.AppendLine("        <AndroidManifest>AndroidManifest.xml</AndroidManifest>");
        }

        csproj.AppendLine($"        <AssemblyName>{exeName}</AssemblyName>");
        csproj.AppendLine("        <RootNamespace>ZigoteExportedGame</RootNamespace>");
        csproj.AppendLine("        <Nullable>enable</Nullable>");
        csproj.AppendLine("        <ImplicitUsings>enable</ImplicitUsings>");
        csproj.AppendLine("        <InvariantGlobalization>true</InvariantGlobalization>");
        csproj.AppendLine("    </PropertyGroup>");
        csproj.AppendLine("""    <PropertyGroup Condition="'$(GameAot)' == 'true'">""");
        csproj.AppendLine("        <PublishAot>true</PublishAot>");
        csproj.AppendLine(
            "        <SuppressTrimAnalysisWarnings>true</SuppressTrimAnalysisWarnings>"
        );
        csproj.AppendLine("    </PropertyGroup>");
        // Homebrew's .NET build links Homebrew-provided libs (ssl/crypto/brotli/…) into NativeAOT
        // binaries but omits their search paths; keg-only openssl needs its own entry.
        csproj.AppendLine(
            """    <ItemGroup Condition="'$(GameAot)' == 'true' AND $([MSBuild]::IsOSPlatform('OSX'))">"""
        );
        csproj.AppendLine(
            """        <LinkerArg Include="-L/opt/homebrew/lib" Condition="Exists('/opt/homebrew/lib')" />"""
        );
        csproj.AppendLine(
            """        <LinkerArg Include="-L/opt/homebrew/opt/openssl@3/lib" Condition="Exists('/opt/homebrew/opt/openssl@3/lib')" />"""
        );
        csproj.AppendLine(
            """        <LinkerArg Include="-L/usr/local/lib" Condition="Exists('/usr/local/lib')" />"""
        );
        csproj.AppendLine(
            """        <LinkerArg Include="-L/usr/local/opt/openssl@3/lib" Condition="Exists('/usr/local/opt/openssl@3/lib')" />"""
        );
        csproj.AppendLine("    </ItemGroup>");
        csproj.AppendLine("""    <ItemGroup Condition="'$(GameAot)' == 'true'">""");
        csproj.AppendLine("""        <TrimmerRootAssembly Include="Zigote.Runtime" />""");
        csproj.AppendLine("""        <TrimmerRootAssembly Include="Zigote.Scripting" />""");
        csproj.AppendLine("""        <TrimmerRootAssembly Include="Zigote.ECS" />""");
        if (scriptAsm is not null)
            csproj.AppendLine($"""        <TrimmerRootAssembly Include="{scriptAsm}" />""");
        // Rooted so the player's reflection late-bind (Type.GetType) survives AOT trimming.
        if (input.Project.DevToolsEnabled)
            csproj.AppendLine("""        <TrimmerRootAssembly Include="Zigote.UI.DevTools" />""");
        csproj.AppendLine("    </ItemGroup>");
        // Mobile resource pipelines do not lift referenced-project Content into the package, so
        // the fonts Zigote.UI ships as Content — and the staged game Content — are declared here
        // (same set + layout as the Gallery mobile heads; App probes Fonts/ under the base dir).
        string contentDir = Path.Combine(
            path1: Path.GetDirectoryName(playerDir)!,
            path2: "Content"
        );
        (string Src, string Dst)[] fonts = [
            (@"Fonts\Inter\static\Inter_18pt-Regular.ttf", @"Fonts\Inter-Regular.ttf"),
            (@"Fonts\PkgTTC-SGr-Iosevka-34\SGr-Iosevka-Regular.ttc", @"Fonts\Iosevka-Regular.ttc"),
            (@"Fonts\Noto_Emoji\static\NotoEmoji-Regular.ttf", @"Fonts\NotoEmoji-Regular.ttf"),
            (@"Fonts\MaterialIcons\MaterialIcons-Regular.ttf", @"Fonts\MaterialIcons-Regular.ttf"),
            (@"Fonts\Inter\OFL.txt", @"Fonts\LICENSE-Inter-OFL.txt"),
            (@"Fonts\PkgTTC-SGr-Iosevka-34\OFL.txt", @"Fonts\LICENSE-Iosevka-OFL.txt"),
        ];

        if (rid.StartsWith("ios"))
        {
            csproj.AppendLine("    <ItemGroup>");
            foreach ((string src, string dst) in fonts)
            {
                csproj.AppendLine(
                    $"""        <BundleResource Include="{Path.Combine(path1: sdkRoot, path2: "Zigote.UI", path3: src)}" Link="{dst}" />"""
                );
            }

            csproj.AppendLine(
                $"""        <BundleResource Include="{contentDir}\**\*" Link="Content\%(RecursiveDir)%(Filename)%(Extension)" />"""
            );
            csproj.AppendLine("    </ItemGroup>");
        }

        if (rid == "ios-arm64")
        {
            // iOS DEVICE links the engine INTO the app binary: AMFI rejects a bundled dylib, and a
            // device .NET app links every other native piece statically anyway. ForceLoad keeps the
            // engine's exports alive — every zigote_* entry is bound at runtime by DllImport, so
            // the linker sees no reference to any of them. The archives are produced by the
            // engine's `static-lib` step during this same publish (see Zigote.Native.targets).
            // Simulator builds keep the bundled dylib instead.
            string zigOut = Path.Combine(
                path1: sdkRoot,
                path2: "Zigote.Engine",
                path3: "zig-out",
                path4: "lib"
            );
            csproj.AppendLine("    <ItemGroup>");
            csproj.AppendLine(
                $"""        <NativeReference Include="{Path.Combine(path1: zigOut, path2: "libzigote.a")}" Kind="Static" IsCxx="True" ForceLoad="True" Frameworks="UIKit Metal QuartzCore Foundation CoreGraphics CoreVideo CoreMotion GameController CoreAudio AudioToolbox AVFoundation" WeakFrameworks="CoreHaptics" LinkerFlags="-liconv -Wl,-export_dynamic" />"""
            );
            csproj.AppendLine(
                $"""        <_ZigoteDepArchive Include="{Path.Combine(path1: zigOut, path2: "*.a")}" Exclude="{Path.Combine(path1: zigOut, path2: "libzigote.a")}" />"""
            );
            csproj.AppendLine(
                """        <NativeReference Include="@(_ZigoteDepArchive)" Kind="Static" IsCxx="True" />"""
            );
            csproj.AppendLine("    </ItemGroup>");
        }

        if (rid.StartsWith("android"))
        {
            string abi = rid.EndsWith("x64") ? "x86_64" : "arm64-v8a";
            csproj.AppendLine("    <ItemGroup>");
            // SDL's Java half + our SDLActivity subclass; all of SDL's JNI-registered classes
            // must reach the dex or the first JNI lookup throws.
            csproj.AppendLine(
                $"""        <AndroidJavaSource Include="{Path.Combine(path1: sdkRoot, path2: "mobile", path3: "android", path4: "JavaSources")}\**\*.java" Bind="false" />"""
            );
            csproj.AppendLine(
                $"""        <AndroidNativeLibrary Include="{Path.Combine(sdkRoot, "Zigote.Engine", "zig-out", "lib", "libzigote.so")}" Abi="{abi}" />"""
            );
            foreach ((string src, string dst) in fonts)
            {
                csproj.AppendLine(
                    $"""        <AndroidAsset Include="{Path.Combine(path1: sdkRoot, path2: "Zigote.UI", path3: src)}" Link="{dst}" />"""
                );
            }

            csproj.AppendLine(
                $"""        <AndroidAsset Include="{contentDir}\**\*" Link="Content\%(RecursiveDir)%(Filename)%(Extension)" />"""
            );
            csproj.AppendLine("    </ItemGroup>");
        }

        csproj.AppendLine("    <ItemGroup>");
        csproj.AppendLine(
            $"""        <ProjectReference Include="{Path.Combine(path1: sdkRoot, path2: "Zigote.Player", path3: "Zigote.Player.csproj")}" />"""
        );
        // The DevTools overlay is opt-in per project: only an enabled export bundles the assemblies
        // (PlayerMain late-binds the install, so their absence is a clean no-op).
        if (input.Project.DevToolsEnabled)
        {
            csproj.AppendLine(
                $"""        <ProjectReference Include="{Path.Combine(path1: sdkRoot, path2: "Zigote.UI.DevTools", path3: "Zigote.UI.DevTools.csproj")}" />"""
            );
        }

        if (scriptCsproj is not null)
            csproj.AppendLine($"""        <ProjectReference Include="{scriptCsproj}" />""");
        csproj.AppendLine("    </ItemGroup>");
        // Subset the bundled text fonts at publish (Iosevka alone is ~10 MB unsubset); skips
        // gracefully when no subsetter is installed.
        csproj.AppendLine(
            $"""    <Import Project="{Path.Combine(path1: sdkRoot, path2: "build", path3: "Zigote.Fonts.targets")}" />"""
        );
        if (rid.StartsWith("ios"))
            // libzigote.dylib flows as Content from Zigote.Core through EVERY referencing project;
            // the iOS SDK keeps each copy and the parallel install_name_tool runs then race on the
            // same output file. Collapse the duplicates before the tool runs (see the Gallery head).
        {
            csproj.AppendLine(
                """
                    <Target Name="DedupeNativeLibrariesToReidentify"
                            AfterTargets="_ComputeDynamicLibrariesToReidentify"
                            BeforeTargets="_InstallNameTool">
                        <RemoveDuplicates Inputs="@(_DynamicLibraryToReidentify)">
                            <Output TaskParameter="Filtered" ItemName="_DedupedDynamicLibraryToReidentify" />
                        </RemoveDuplicates>
                        <ItemGroup>
                            <_DynamicLibraryToReidentify Remove="@(_DynamicLibraryToReidentify)" />
                            <_DynamicLibraryToReidentify Include="@(_DedupedDynamicLibraryToReidentify)" />
                        </ItemGroup>
                    </Target>
                """
            );
        }

        csproj.AppendLine("</Project>");
        File.WriteAllText(
            path: Path.Combine(path1: playerDir, path2: "Game.csproj"),
            contents: csproj.ToString()
        );
        WriteMobilePlatformFiles(
            playerDir: playerDir,
            rid: rid,
            name: name,
            projectName: input.Project.Name
        );

        var reg = new StringBuilder();
        reg.AppendLine(
            "// Generated by Zigote game export — static component registration (NativeAOT-safe"
        );
        reg.AppendLine("// replacement for the editor's assembly-scan discovery).");
        reg.AppendLine("using Zigote.Scripting.Metadata;");
        reg.AppendLine();
        reg.AppendLine("static class GameScripts");
        reg.AppendLine("{");
        reg.AppendLine("    public static void Register(ScriptRegistry r)");
        reg.AppendLine("    {");
        int kept = 0;
        int dropped = 0;
        foreach (var meta in input.Scripts.All.OrderBy(m => m.FullName))
        {
            // Nested/generic component types can't be spelled via typeof(global::…) — none exist today.
            if (meta.FullName.Contains('+') || meta.FullName.Contains('`')) continue;

            // Keep the game's own components wholesale (its assembly is statically linked, and game code
            // may attach them at runtime by name); engine samples ship only if the scene references them.
            bool fromGameAssembly = scriptAsm is not null &&
                                    meta.Type.Assembly.GetName().Name == scriptAsm;
            if (!fromGameAssembly && sceneScriptClasses is not null &&
                !sceneScriptClasses.Contains(meta.FullName))
            {
                dropped++;
                continue;
            }

            reg.AppendLine($"        r.Register(typeof(global::{meta.FullName}));");
            kept++;
        }

        reg.AppendLine(
            $"        // {kept} component(s) registered; {dropped} unreferenced sample(s) trimmed."
        );

        reg.AppendLine("    }");
        reg.AppendLine("}");
        File.WriteAllText(
            path: Path.Combine(path1: playerDir, path2: "ScriptRegistration.g.cs"),
            contents: reg.ToString()
        );

        // A class-based Main, not top-level statements: the Android SDK compiles the app as a
        // Library under the hood (Java owns the process), where top-level statements are illegal.
        // On Android this Main is never called — MainApplication registers the game body instead.
        File.WriteAllText(
            path: Path.Combine(path1: playerDir, path2: "Program.g.cs"),
            contents: """
                      static class Program
                      {
                          public static int Main()
                          {
                              return Zigote.Player.PlayerMain.Run(GameScripts.Register);
                          }
                      }
                      """ + "\n"
        );
    }

    // ── Publish + package ─────────────────────────────────────────────────────

    private static async Task<bool> PublishAndPackageAsync(ExportOptions options,
        string staging, string rid, string exeName, string name, bool aot, IProgress<string> log)
    {
        // AOT artifacts carry an explicit suffix so a single pass can ship both flavors side by side.
        string artifact = aot ? $"{exeName}-{rid}-aot" : $"{exeName}-{rid}";
        string publishDir = Path.Combine(
            path1: staging,
            path2: "publish",
            path3: aot ? $"{rid}-aot" : rid
        );
        // JIT bundles ship single-file on Windows/Linux: the ~230 managed assemblies fold into the
        // exe (memory-mapped at runtime, compressed on disk) leaving exe + native lib + Fonts +
        // Content. Native libs and content stay loose — the engine loads both by path. macOS stays
        // multi-file: the .app hides the file count and appended-bundle Mach-Os complicate signing.
        bool singleFile = !aot && !rid.StartsWith("osx") && !IsMobile(rid);
        // The iOS SDK hard-refuses `publish` for simulator architectures ("a device architecture
        // must be specified") — simulator apps are a `build` product. Same output shape: the .app
        // lands in the -o directory either way.
        string verb = rid.StartsWith("iossimulator") ? "build" : "publish";
        var args =
            new StringBuilder(
                    $"{verb} \"{Path.Combine(path1: staging, path2: "player", path3: "Game.csproj")}\""
                )
                // Mobile heads are self-contained by construction (the platform SDK decides the
                // runtime packaging), and passing --self-contained derails that pipeline.
                .Append(IsMobile(rid) ? " -c Release" : " -c Release --self-contained true")
                .Append($" -r {rid} -p:ZigTargetRid={rid}")
                // Exported games never import source models (assets ship pre-baked .zmesh), so the
                // native lib builds without the Assimp importer (~3 MB stripped).
                .Append(" -p:Enable3D=false")
                // Zigote.Native.targets defaults mobile to the lean UI set (no Jolt) — games need
                // physics; a global property overrides the targets' assignment. Jolt cross-compiles
                // fine for ios/android despite the "not yet validated" note there.
                .Append(IsMobile(rid) ? " -p:EnablePhysics3D=true" : "")
                .Append(" -p:DebugType=none -p:DebugSymbols=false")
                // Mobile SDKs require their trimmer pipeline (it collects the native references);
                // link depth is governed by MtouchLink/AndroidLinkMode in the generated project.
                .Append(aot ? " -p:GameAot=true" : IsMobile(rid) ? "" : " -p:PublishTrimmed=false")
                .Append(
                    singleFile
                        // Extracted at first start rather than memory-mapped: an assembly inside a
                        // bundle reports no Assembly.Location, and anything reading metadata off
                        // disk — Roslyn scripting, which Minemake's in-game computers are — fails
                        // with "Can't create a metadata reference to an assembly without location".
                        // Costs one extraction into ~/.net the first time the game runs.
                        ? " -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true"
                          + " -p:IncludeAllContentForSelfExtract=true"
                        : ""
                )
                .Append($" -o \"{publishDir}\"");

        log.Report($"[{artifact}] dotnet publish ({(aot ? "NativeAOT" : "self-contained JIT")}) …");
        int exit = await RunAsync(
            exe: "dotnet",
            args: args.ToString(),
            workDir: staging,
            log: log
        );
        if (exit != 0)
        {
            log.Report($"[{artifact}] publish FAILED (exit {exit}).");
            return false;
        }

        string outDir = Path.Combine(path1: options.OutputDir, path2: artifact);
        if (Directory.Exists(outDir)) Directory.Delete(path: outDir, recursive: true);

        if (IsMobile(rid))
        {
            // The platform SDK produced the installable artifact itself (a signed .app for iOS,
            // an .apk for Android). Copy the whole publish output — it holds that artifact plus
            // the symbols and manifests — and point the log at the package.
            CopyTree(src: publishDir, dst: outDir);
            string? package = Directory.EnumerateFileSystemEntries(outDir)
                .FirstOrDefault(e => e.EndsWith(".apk") || e.EndsWith(".ipa") || e.EndsWith(".app")
                );
            log.Report($"[{artifact}] → {package ?? outDir}");
            return true;
        }

        if (rid.StartsWith("osx"))
        {
            string app = Path.Combine(path1: outDir, path2: $"{exeName}.app");
            PackageMacApp(
                appDir: app,
                publishDir: publishDir,
                contentDir: Path.Combine(path1: staging, path2: "Content"),
                name: name,
                exeName: exeName
            );
            await BundleHomebrewDylibs(appDir: app, exeName: exeName, log: log);
            log.Report($"[{artifact}] codesigning (ad-hoc) …");
            await RunAsync(
                exe: "codesign",
                args: $"--force --deep --sign - \"{app}\"",
                workDir: outDir,
                log: log
            );
            log.Report($"[{artifact}] → {app}");
        }
        else
        {
            CopyTree(src: publishDir, dst: outDir);
            CopyTree(
                src: Path.Combine(path1: staging, path2: "Content"),
                dst: Path.Combine(path1: outDir, path2: "Content")
            );
            string zip = Path.Combine(path1: options.OutputDir, path2: $"{artifact}.zip");
            File.Delete(zip);
            ZipFile.CreateFromDirectory(
                sourceDirectoryName: outDir,
                destinationArchiveFileName: zip,
                compressionLevel: CompressionLevel.Optimal,
                includeBaseDirectory: true
            );
            log.Report($"[{artifact}] → {zip}");
        }

        return true;
    }

    /// <summary>
    ///     Make a Homebrew-SDK NativeAOT binary self-contained: Homebrew's .NET links its own
    ///     OpenSSL/brotli dylibs by absolute /opt/homebrew paths, which would only resolve on machines
    ///     with the same formulas installed. Copy the dependency closure into Contents/MacOS and
    ///     rewrite the load commands to @executable_path. No-op for binaries with no Homebrew deps
    ///     (JIT apphosts, Microsoft-SDK builds). Runs before codesign, which re-signs everything.
    /// </summary>
    private static async Task BundleHomebrewDylibs(string appDir, string exeName,
        IProgress<string> log)
    {
        string macos = Path.Combine(path1: appDir, path2: "Contents", path3: "MacOS");
        var queue = new Queue<string>();
        queue.Enqueue(Path.Combine(path1: macos, path2: exeName));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int bundled = 0;

        while (queue.Count > 0)
        {
            string binary = queue.Dequeue();
            foreach (string dep in await MachODependencies(binary))
            {
                // Two shapes need rewriting: absolute Homebrew paths, and @rpath/ deps between the
                // Homebrew dylibs themselves (nothing sets an rpath in the shipped app).
                bool isHomebrew = dep.StartsWith("/opt/homebrew/") || dep.StartsWith("/usr/local/");
                bool isRpath = dep.StartsWith("@rpath/");
                if (!isHomebrew && !isRpath) continue;

                string leaf = Path.GetFileName(dep);
                string local = Path.Combine(path1: macos, path2: leaf);
                if (seen.Add(leaf) && !File.Exists(local))
                {
                    string? src = isHomebrew ? dep : ResolveHomebrewLeaf(leaf);
                    if (src is null)
                    {
                        log.Report($"  ! unresolvable dylib dependency (left as-is): {dep}");
                        continue;
                    }

                    File.Copy(sourceFileName: src, destFileName: local, overwrite: true);
                    // The copied dylib advertises itself and its own deps by absolute path too.
                    await RunAsync(
                        exe: "install_name_tool",
                        args: $"-id @executable_path/{leaf} \"{local}\"",
                        workDir: macos,
                        log: log
                    );
                    queue.Enqueue(local);
                    bundled++;
                }

                await RunAsync(
                    exe: "install_name_tool",
                    args: $"-change \"{dep}\" @executable_path/{leaf} \"{binary}\"",
                    workDir: macos,
                    log: log
                );
            }
        }

        if (bundled > 0)
            log.Report($"Bundled {bundled} Homebrew dylib(s) into the .app (@executable_path).");
    }

    private static string? ResolveHomebrewLeaf(string leaf)
    {
        string[] candidates = [
            $"/opt/homebrew/lib/{leaf}",
            $"/usr/local/lib/{leaf}",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<List<string>> MachODependencies(string binary)
    {
        var deps = new List<string>();
        var psi = new ProcessStartInfo(fileName: "otool", arguments: $"-L \"{binary}\"") {
            RedirectStandardOutput = true,
        };
        using var proc = Process.Start(psi)!;
        while (await proc.StandardOutput.ReadLineAsync() is { } line)
        {
            string trimmed = line.Trim();
            // Absolute paths AND @rpath/ entries — Homebrew dylibs reference each other via @rpath.
            // (A dylib's own install-name ID also matches; downstream handling is a no-op for it.)
            if ((trimmed.StartsWith('/') || trimmed.StartsWith("@rpath/")) &&
                trimmed.Contains(" (compatibility"))
            {
                deps.Add(
                    trimmed[..trimmed.IndexOf(
                        value: " (compatibility",
                        comparisonType: StringComparison.Ordinal
                    )]
                );
            }
        }

        await proc.WaitForExitAsync();
        return deps;
    }

    private static void PackageMacApp(string appDir, string publishDir, string contentDir,
        string name, string exeName)
    {
        string macos = Path.Combine(path1: appDir, path2: "Contents", path3: "MacOS");
        string resources = Path.Combine(path1: appDir, path2: "Contents", path3: "Resources");
        CopyTree(src: publishDir, dst: macos);
        CopyTree(src: contentDir, dst: Path.Combine(path1: resources, path2: "Content"));

        // NativeAOT publish drops a dSYM debug bundle next to the binary — dev-only, ~20 MB.
        foreach (string dsym in Directory.GetDirectories(path: macos, searchPattern: "*.dSYM"))
            Directory.Delete(path: dsym, recursive: true);

        File.WriteAllText(
            path: Path.Combine(path1: appDir, path2: "Contents", path3: "Info.plist"),
            contents: $"""
                       <?xml version="1.0" encoding="UTF-8"?>
                       <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                       <plist version="1.0">
                       <dict>
                           <key>CFBundleName</key><string>{name}</string>
                           <key>CFBundleDisplayName</key><string>{name}</string>
                           <key>CFBundleExecutable</key><string>{exeName}</string>
                           <key>CFBundleIdentifier</key><string>com.zigote.{exeName.ToLowerInvariant()}</string>
                           <key>CFBundlePackageType</key><string>APPL</string>
                           <key>CFBundleShortVersionString</key><string>1.0</string>
                           <key>CFBundleVersion</key><string>1</string>
                           <key>NSHighResolutionCapable</key><true/>
                           <key>LSMinimumSystemVersion</key><string>12.0</string>
                       </dict>
                       </plist>
                       """
        );
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     The engine repo root (contains Zigote.sln) — exports reference SDK projects from it.
    ///     ZIGOTE_SDK overrides; otherwise walk up from the running editor's directory.
    /// </summary>
    private static string? FindSdkRoot()
    {
        if (Environment.GetEnvironmentVariable("ZIGOTE_SDK") is { Length: > 0 } env &&
            File.Exists(Path.Combine(path1: env, path2: "Zigote.sln")))
            return Path.GetFullPath(env);

        for (string? dir = AppContext.BaseDirectory;
             dir is not null;
             dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(path1: dir, path2: "Zigote.sln")))
                return dir;
        }

        return null;
    }

    private static bool CanAotFor(string rid)
    {
        // NativeAOT cannot cross OS boundaries. macOS additionally supports same-OS cross-arch
        // (osx-arm64 ↔ osx-x64) via the universal toolchain, so OS match alone suffices there.
        // Linux/Windows AOT have no assumed cross-arch toolchain, so they also require a matching-arch
        // host — otherwise a cross-arch request (e.g. linux-x64 host → linux-arm64) falls back to JIT,
        // which is arch-agnostic (the Zig native lib still cross-compiles fine).
        if (rid.StartsWith("osx")) return RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        if (rid.StartsWith("win"))
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RidArchMatchesHost(rid);
        if (rid.StartsWith("linux"))
            return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RidArchMatchesHost(rid);
        return false;
    }

    private static bool RidArchMatchesHost(string rid)
    {
        return RuntimeInformation.OSArchitecture switch {
            Architecture.Arm64 => rid.EndsWith("arm64"),
            Architecture.X64 => rid.EndsWith("x64"),
            _ => false,
        };
    }

    /// <summary>
    ///     Platform side-files next to the generated csproj. Written (and stale ones removed)
    ///     per RID because the same player dir is reused across the RID loop — the default
    ///     Compile glob would happily feed a leftover Android file to a desktop build.
    /// </summary>
    private static void WriteMobilePlatformFiles(string playerDir, string rid, string name,
        string? projectName)
    {
        File.Delete(Path.Combine(path1: playerDir, path2: "MainApplication.g.cs"));

        if (rid.StartsWith("ios"))
            // Same plist as the Gallery head: modern launch screen (without ANY launch-screen key
            // UIKit letterboxes the app at legacy resolutions), all orientations, indirect input.
        {
            File.WriteAllText(
                path: Path.Combine(path1: playerDir, path2: "Info.plist"),
                contents: """
                          <?xml version="1.0" encoding="UTF-8"?>
                          <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                          <plist version="1.0">
                          <dict>
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
                              <key>UIApplicationSupportsIndirectInputEvents</key>
                              <true/>
                          </dict>
                          </plist>
                          """
            );
        }

        if (!rid.StartsWith("android")) return;

        string label = SecurityElement.Escape(name);
        File.WriteAllText(
            path: Path.Combine(path1: playerDir, path2: "AndroidManifest.xml"),
            contents: $"""
                       <?xml version="1.0" encoding="utf-8"?>
                       <manifest xmlns:android="http://schemas.android.com/apk/res/android" package="{BundleId(projectName)}">
                           <uses-sdk android:minSdkVersion="26" android:targetSdkVersion="34" />
                           <uses-feature android:name="android.hardware.touchscreen" android:required="false" />
                           <application android:label="{label}" android:hardwareAccelerated="true">
                               <!-- SDL's activity (pure Java) owns the window and surface; the managed runtime
                                    is already up by then — MainApplication registered the game body. -->
                               <activity android:name="com.zigote.app.ZigoteActivity"
                                         android:label="{label}"
                                         android:exported="true"
                                         android:launchMode="singleInstance"
                                         android:configChanges="keyboard|keyboardHidden|orientation|screenSize|screenLayout|smallestScreenSize|uiMode|density">
                                   <intent-filter>
                                       <action android:name="android.intent.action.MAIN" />
                                       <category android:name="android.intent.category.LAUNCHER" />
                                   </intent-filter>
                               </activity>
                           </application>
                       </manifest>
                       """
        );

        File.WriteAllText(
            path: Path.Combine(path1: playerDir, path2: "MainApplication.g.cs"),
            contents: """
                      // Generated by Zigote game export — Android head. Java owns the process (SDLActivity
                      // starts the SDL thread and calls zigote_android_main), so the game body is registered
                      // here, in the one managed place guaranteed to run before the launcher activity.
                      using Zigote.Core.Native;

                      [global::Android.App.Application]
                      public class MainApplication : global::Android.App.Application
                      {
                          public MainApplication(IntPtr handle, global::Android.Runtime.JniHandleOwnership ownership)
                              : base(handle, ownership)
                          {
                          }

                          public override void OnCreate()
                          {
                              base.OnCreate();
                              // The engine opens fonts and content natively by plain file path; an APK asset
                              // has no path, so both trees are copied out at launch. Always overwritten:
                              // an app update keeps the files dir, and stale content is worse than
                              // re-copying a few MB at startup.
                              foreach (var root in new[] { "Fonts", "Content" })
                                  try
                                  {
                                      Stage(root);
                                  }
                                  catch (Exception ex)
                                  {
                                      global::Android.Util.Log.Error("zigote", $"asset staging failed ({root}): {ex}");
                                  }

                              MobileHost.SetAndroidMain(() => Zigote.Player.PlayerMain.Run(GameScripts.Register));
                          }

                          private void Stage(string rel)
                          {
                              // AssetManager has no is-directory query: a non-empty List means directory.
                              var children = Assets?.List(rel) ?? [];
                              if (children.Length == 0)
                              {
                                  var target = Path.Combine(AppContext.BaseDirectory, rel);
                                  Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                                  using var src = Assets!.Open(rel);
                                  using var dst = File.Create(target);
                                  src.CopyTo(dst);
                                  return;
                              }

                              foreach (var child in children) Stage($"{rel}/{child}");
                          }
                      }
                      """
        );
    }

    /// <summary>
    ///     Reverse-DNS application id for the mobile package, derived from the project name
    ///     (installers key on it, and it must be stable across exports).
    /// </summary>
    private static string BundleId(string? projectName)
    {
        string slug = new string(
            (projectName ?? "game").ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '.')
            .ToArray()
        ).Trim('.');
        while (slug.Contains("..")) slug = slug.Replace(oldValue: "..", newValue: ".");
        return $"com.zigote.{(slug.Length == 0 ? "game" : slug)}";
    }

    private static string SanitizeExeName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }

        return sb.Length > 0 ? sb.ToString() : "ZigoteGame";
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.EnumerateFiles(
                     path: src,
                     searchPattern: "*",
                     searchOption: SearchOption.AllDirectories
                 ))
        {
            string target = Path.Combine(
                path1: dst,
                path2: Path.GetRelativePath(relativeTo: src, path: file)
            );
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourceFileName: file, destFileName: target);
        }
    }

    private static async Task<int> RunAsync(string exe, string args, string workDir,
        IProgress<string> log)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo(fileName: exe, arguments: args) {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) log.Report(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) log.Report(e.Data);
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }
}
