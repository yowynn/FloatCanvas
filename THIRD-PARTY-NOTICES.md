# Third-party notices

FloatCanvas is independently implemented. ppInk and ShareX were behavior and public-source research references; their applications, source code and assemblies are not included in this distribution.

The Windows distribution includes the components below. Full original license texts and upstream notices are in the accompanying `licenses` directory. Package versions and copyright metadata were read from the installed NuGet `.nuspec` files; those metadata files are retained alongside the licenses. `licenses/SOURCES.json` records the collection sources.

| Component | Version | License / included notice |
| --- | --- | --- |
| SkiaSharp | 4.157.0 | MIT, `licenses/SkiaSharp-4.157.0/LICENSE.txt` |
| SkiaSharp.HarfBuzz | 4.157.0 | MIT, `licenses/SkiaSharp.HarfBuzz-4.157.0/LICENSE.txt` |
| SkiaSharp.NativeAssets.Win32 / native Skia and bundled libraries | 4.157.0 | MIT wrapper plus full upstream `THIRD-PARTY-NOTICES.txt` in `licenses/SkiaSharp.NativeAssets.Win32-4.157.0/` |
| HarfBuzzSharp | 14.4.0.200 | MIT, `licenses/HarfBuzzSharp-14.4.0.200/LICENSE.txt` |
| HarfBuzzSharp.NativeAssets.Win32 / native HarfBuzz and bundled libraries | 14.4.0.200 | MIT wrapper plus full upstream `THIRD-PARTY-NOTICES.txt` in `licenses/HarfBuzzSharp.NativeAssets.Win32-14.4.0.200/` |
| Vortice.DXGI, Vortice.Direct3D11, Vortice.DirectX | 3.8.3 | MIT; license obtained from the exact repository commit identified in each package's `.nuspec` |
| Vortice.Mathematics | 2.1.0 | MIT; license obtained from the exact repository commit identified in the package's `.nuspec` |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | MIT; license obtained from the exact repository commit identified in each package's `.nuspec` |
| .NET apphost native executable launcher | Supplied by the .NET SDK | MIT, `licenses/Microsoft.NET.AppHost/LICENSE.TXT` |
| Unicode Emoji keyboard data and CLDR English / Simplified Chinese annotations | Emoji 18.0, CLDR 49 | Unicode License v3, `licenses/Unicode-Emoji-CLDR/LICENSE.txt` |
| Noto Color Emoji COLRv1 (downloadable font) | v2026-09-24-unicode18_0 | SIL Open Font License 1.1, `licenses/Noto-Color-Emoji/LICENSE.txt` |
| OpenMoji Color COLRv0 (downloadable font) | 18.0 | CC BY-SA 4.0, `licenses/OpenMoji/LICENSE.txt` |
| Blobmoji COLRv1 vector build (downloadable derivative font) | 1.001 | SIL Open Font License 1.1, Apache License 2.0, Comic Neue OFL 1.1 and region-flags notices in `licenses/Blobmoji/` |

The default Windows package does not bundle the .NET Windows Desktop Runtime. Install the required runtime separately from Microsoft when it is not already available on the computer.

Native Skia and HarfBuzz notices contain additional licenses for their bundled components. The emoji picker embeds a catalog generated from [Unicode Emoji](https://www.unicode.org/Public/18.0.0/emoji/emoji-test.txt) and [CLDR annotations](https://github.com/unicode-org/cldr/tree/release-49/common). Emoji font binaries are not included in the source tree or application package. The application resolves system fonts and fonts downloaded into the user's FloatCanvas profile. Noto Color Emoji COLRv1 is sourced from [Google Noto Emoji](https://github.com/googlefonts/noto-emoji/blob/v2026-09-24-unicode18_0/2D/fonts/Noto-COLRv1.ttf). OpenMoji COLRv0 is sourced from [OpenMoji](https://github.com/hfg-gmuend/openmoji). The Blobmoji entry is an independently generated COLRv1-format modification of [Blobmoji](https://github.com/C1710/blobmoji) and is not the upstream project's official binary release.

For the Vortice and SharpGen packages, NuGet declares the MIT license expression but does not embed the complete license text. The following immutable upstream sources supplied `LICENSE.upstream.txt` in the corresponding component folders:

- [Vortice.Windows license, commit 9e609cb9439c9872aa1b339f177e40ec96f77239](https://github.com/amerkoleci/Vortice.Windows/blob/9e609cb9439c9872aa1b339f177e40ec96f77239/LICENSE)
- [Vortice.Mathematics license, commit fa05ec6dcba48f3f7331791da6dc7f3d866b2ad6](https://github.com/amerkoleci/Vortice.Mathematics/blob/fa05ec6dcba48f3f7331791da6dc7f3d866b2ad6/LICENSE)
- [SharpGenTools license, commit 6990bcafe124a4c22515ad19cee5a081da8db67b](https://github.com/SharpGenTools/SharpGenTools/blob/6990bcafe124a4c22515ad19cee5a081da8db67b/LICENSE.txt)

FloatCanvas's own source code is licensed under the MIT license in `LICENSE`. The third-party components above retain their respective licenses and notices.
