namespace Zigote.Core.Licenses;

/// <summary>
///     Attributions for Zigote itself and every third-party component compiled into the native
///     engine (<c>libzigote</c>). Registered as a collector by <see cref="LicenseRegistry" />'s
///     static constructor, so every app gets them without opting in. Must stay in sync with
///     THIRD-PARTY-NOTICES.md — that file is the audit trail, this is what apps display.
/// </summary>
internal static class ZigoteLicenses
{
    internal static IEnumerable<LicenseEntry> Create()
    {
        return [
            new LicenseEntry(
                Component: "Zigote",
                License: "MIT",
                Text: LicenseTexts.Mit("Copyright (c) 2026 Zigote Project Developers")
            ),

            new LicenseEntry(
                Component: "SDL3",
                License: "zlib",
                Text: LicenseTexts.Zlib(
                    "Copyright (C) Sam Lantinga <slouken@libsdl.org> and the SDL contributors"
                )
            ) { Homepage = "https://libsdl.org" },

            new LicenseEntry(
                Component: "SDL3 Zig binding",
                License: "MIT",
                Text: LicenseTexts.Mit("Copyright (c) 2026 7Games Entertainment LLC")
            ),

            new LicenseEntry(
                Component: "wgpu-native",
                License: "MIT OR Apache-2.0",
                Text:
                "wgpu-native is dual-licensed under MIT or Apache-2.0; the MIT license is reproduced here.\n\n" +
                LicenseTexts.Mit("Copyright (c) the gfx-rs developers")
            ) { Homepage = "https://github.com/gfx-rs/wgpu-native" },

            new LicenseEntry(
                Component: "FreeType",
                License: "FreeType License (FTL)",
                Text:
                "Portions of this software are copyright © The FreeType Project (www.freetype.org). " +
                "All rights reserved.\n\nFreeType is licensed under the FreeType License; " +
                "see https://freetype.org/license.html"
            ) { Homepage = "https://freetype.org" },

            new LicenseEntry(
                Component: "HarfBuzz",
                License: "MIT (\"Old MIT\")",
                Text:
                "Copyright © the HarfBuzz project authors.\n\nHarfBuzz is licensed under the so-called " +
                "\"Old MIT\" license; see https://github.com/harfbuzz/harfbuzz/blob/main/COPYING"
            ) { Homepage = "https://harfbuzz.github.io" },

            new LicenseEntry(
                Component: "zlib",
                License: "zlib",
                Text: LicenseTexts.Zlib("Copyright (C) 1995-2024 Jean-loup Gailly and Mark Adler")
            ) { Homepage = "https://zlib.net" },

            new LicenseEntry(
                Component: "Jolt Physics",
                License: "MIT",
                Text: LicenseTexts.Mit("Copyright (c) 2021 Jorrit Rouwe")
            ) { Homepage = "https://github.com/jrouwe/JoltPhysics" },

            new LicenseEntry(
                Component: "flecs",
                License: "MIT",
                Text: LicenseTexts.Mit("Copyright (c) Sander Mertens")
            ) { Homepage = "https://github.com/SanderMertens/flecs" },

            new LicenseEntry(
                Component: "miniaudio",
                License: "public domain (unlicense) OR MIT-0",
                Text: "Copyright (c) David Reid.\n\nminiaudio is dual-licensed: public domain " +
                      "(www.unlicense.org) or MIT No Attribution (MIT-0), at your option."
            ) { Homepage = "https://miniaud.io" },

            new LicenseEntry(
                Component: "Assimp",
                License: "BSD-3-Clause",
                Text: LicenseTexts.Bsd3(
                    "Copyright (c) 2006-2021, assimp team\nAll rights reserved."
                )
            ) { Homepage = "https://github.com/assimp/assimp" },

            new LicenseEntry(
                Component: "libwebp",
                License: "BSD-3-Clause",
                Text: LicenseTexts.Bsd3("Copyright (c) 2010, Google Inc. All rights reserved.")
            ) { Homepage = "https://chromium.googlesource.com/webm/libwebp" },

            new LicenseEntry(
                Component: "zig-gamedev libraries (zmath, zmesh, zpool, zaudio, zflecs, zphysics)",
                License: "MIT",
                Text: LicenseTexts.Mit(
                    "Copyright (c) Michal Ziulek and the zig-gamedev contributors"
                )
            ) { Homepage = "https://github.com/zig-gamedev" },

            new LicenseEntry(
                Component: "zigimg",
                License: "MIT",
                Text: LicenseTexts.Mit("Copyright (c) the zigimg contributors")
            ) { Homepage = "https://github.com/zigimg/zigimg" },

            new LicenseEntry(
                Component: "meshoptimizer",
                License: "MIT",
                Text: LicenseTexts.Mit("Copyright (c) 2016-2022 Arseny Kapoulkine")
            ) { Homepage = "https://github.com/zeux/meshoptimizer" },

            new LicenseEntry(
                Component: "par_shapes",
                License: "MIT",
                Text: LicenseTexts.Mit("Copyright (c) Philip Rideout")
            ) { Homepage = "https://github.com/prideout/par" },

            new LicenseEntry(
                Component: "cgltf",
                License: "MIT",
                Text: LicenseTexts.Mit("Copyright (c) Johannes Kuhlmann")
            ) { Homepage = "https://github.com/jkuhlmann/cgltf" },
        ];
    }
}
