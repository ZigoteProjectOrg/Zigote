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
                "Zigote",
                "MIT",
                LicenseTexts.Mit("Copyright (c) 2026 Zigote Project Developers")
            ),

            new LicenseEntry(
                "SDL3",
                "zlib",
                LicenseTexts.Zlib(
                    "Copyright (C) Sam Lantinga <slouken@libsdl.org> and the SDL contributors"
                )
            ) { Homepage = "https://libsdl.org" },

            new LicenseEntry(
                "SDL3 Zig binding",
                "MIT",
                LicenseTexts.Mit("Copyright (c) 2026 7Games Entertainment LLC")
            ),

            new LicenseEntry(
                "wgpu-native",
                "MIT OR Apache-2.0",
                "wgpu-native is dual-licensed under MIT or Apache-2.0; the MIT license is reproduced here.\n\n" +
                LicenseTexts.Mit("Copyright (c) the gfx-rs developers")
            ) { Homepage = "https://github.com/gfx-rs/wgpu-native" },

            new LicenseEntry(
                "FreeType",
                "FreeType License (FTL)",
                "Portions of this software are copyright © The FreeType Project (www.freetype.org). " +
                "All rights reserved.\n\nFreeType is licensed under the FreeType License; " +
                "see https://freetype.org/license.html"
            ) { Homepage = "https://freetype.org" },

            new LicenseEntry(
                "HarfBuzz",
                "MIT (\"Old MIT\")",
                "Copyright © the HarfBuzz project authors.\n\nHarfBuzz is licensed under the so-called " +
                "\"Old MIT\" license; see https://github.com/harfbuzz/harfbuzz/blob/main/COPYING"
            ) { Homepage = "https://harfbuzz.github.io" },

            new LicenseEntry(
                "zlib",
                "zlib",
                LicenseTexts.Zlib("Copyright (C) 1995-2024 Jean-loup Gailly and Mark Adler")
            ) { Homepage = "https://zlib.net" },

            new LicenseEntry(
                "Jolt Physics",
                "MIT",
                LicenseTexts.Mit("Copyright (c) 2021 Jorrit Rouwe")
            ) { Homepage = "https://github.com/jrouwe/JoltPhysics" },

            new LicenseEntry(
                "flecs",
                "MIT",
                LicenseTexts.Mit("Copyright (c) Sander Mertens")
            ) { Homepage = "https://github.com/SanderMertens/flecs" },

            new LicenseEntry(
                "miniaudio",
                "public domain (unlicense) OR MIT-0",
                "Copyright (c) David Reid.\n\nminiaudio is dual-licensed: public domain " +
                "(www.unlicense.org) or MIT No Attribution (MIT-0), at your option."
            ) { Homepage = "https://miniaud.io" },

            new LicenseEntry(
                "Assimp",
                "BSD-3-Clause",
                LicenseTexts.Bsd3("Copyright (c) 2006-2021, assimp team\nAll rights reserved.")
            ) { Homepage = "https://github.com/assimp/assimp" },

            new LicenseEntry(
                "libwebp",
                "BSD-3-Clause",
                LicenseTexts.Bsd3("Copyright (c) 2010, Google Inc. All rights reserved.")
            ) { Homepage = "https://chromium.googlesource.com/webm/libwebp" },

            new LicenseEntry(
                "zig-gamedev libraries (zmath, zmesh, zpool, zaudio, zflecs, zphysics)",
                "MIT",
                LicenseTexts.Mit("Copyright (c) Michal Ziulek and the zig-gamedev contributors")
            ) { Homepage = "https://github.com/zig-gamedev" },

            new LicenseEntry(
                "zigimg",
                "MIT",
                LicenseTexts.Mit("Copyright (c) the zigimg contributors")
            ) { Homepage = "https://github.com/zigimg/zigimg" },

            new LicenseEntry(
                "meshoptimizer",
                "MIT",
                LicenseTexts.Mit("Copyright (c) 2016-2022 Arseny Kapoulkine")
            ) { Homepage = "https://github.com/zeux/meshoptimizer" },

            new LicenseEntry(
                "par_shapes",
                "MIT",
                LicenseTexts.Mit("Copyright (c) Philip Rideout")
            ) { Homepage = "https://github.com/prideout/par" },

            new LicenseEntry(
                "cgltf",
                "MIT",
                LicenseTexts.Mit("Copyright (c) Johannes Kuhlmann")
            ) { Homepage = "https://github.com/jkuhlmann/cgltf" },
        ];
    }
}
