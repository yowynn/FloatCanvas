# Third-party notices

FloatCanvas is independently implemented. ppInk and ShareX were behavior and public-source research references; their applications, source code and assemblies are not included in this distribution.

The Windows distribution includes the components below. Full original license texts and upstream notices are in the accompanying `licenses` directory. Package versions and copyright metadata were read from the installed NuGet `.nuspec` files; those metadata files are retained alongside the licenses. `licenses/SOURCES.json` records the collection sources.

| Component | Version | License / included notice |
| --- | --- | --- |
| SkiaSharp | 4.153.1 | MIT, `licenses/SkiaSharp-4.153.1/LICENSE.txt` |
| SkiaSharp.HarfBuzz | 4.153.1 | MIT, `licenses/SkiaSharp.HarfBuzz-4.153.1/LICENSE.txt` |
| SkiaSharp.NativeAssets.Win32 / native Skia and bundled libraries | 4.153.1 | MIT wrapper plus full upstream `THIRD-PARTY-NOTICES.txt` in `licenses/SkiaSharp.NativeAssets.Win32-4.153.1/` |
| HarfBuzzSharp | 14.2.1.301 | MIT, `licenses/HarfBuzzSharp-14.2.1.301/LICENSE.txt` |
| HarfBuzzSharp.NativeAssets.Win32 / native HarfBuzz and bundled libraries | 14.2.1.301 | MIT wrapper plus full upstream `THIRD-PARTY-NOTICES.txt` in `licenses/HarfBuzzSharp.NativeAssets.Win32-14.2.1.301/` |
| Vortice.DXGI, Vortice.Direct3D11, Vortice.DirectX | 3.8.3 | MIT; license obtained from the exact repository commit identified in each package's `.nuspec` |
| Vortice.Mathematics | 2.1.0 | MIT; license obtained from the exact repository commit identified in the package's `.nuspec` |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | MIT; license obtained from the exact repository commit identified in each package's `.nuspec` |
| Microsoft.NETCore.App.Runtime.win-x64 | Actual included version is recorded in `PACKAGE-INFO.json` | Runtime package's full LICENSE and THIRD-PARTY-NOTICES copied during publishing |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 | Actual included version is recorded in `PACKAGE-INFO.json` | Runtime package's full LICENSE copied during publishing |

Native Skia and HarfBuzz notices contain additional licenses for their bundled components. Their complete notice files are preserved verbatim rather than replaced by the wrapper's MIT license. Bundled fonts or user-imported images are not supplied by FloatCanvas; system fonts remain provided by the operating system.

For the Vortice and SharpGen packages, NuGet declares the MIT license expression but does not embed the complete license text. The following immutable upstream sources supplied `LICENSE.upstream.txt` in the corresponding component folders:

- [Vortice.Windows license, commit 9e609cb9439c9872aa1b339f177e40ec96f77239](https://github.com/amerkoleci/Vortice.Windows/blob/9e609cb9439c9872aa1b339f177e40ec96f77239/LICENSE)
- [Vortice.Mathematics license, commit fa05ec6dcba48f3f7331791da6dc7f3d866b2ad6](https://github.com/amerkoleci/Vortice.Mathematics/blob/fa05ec6dcba48f3f7331791da6dc7f3d866b2ad6/LICENSE)
- [SharpGenTools license, commit 6990bcafe124a4c22515ad19cee5a081da8db67b](https://github.com/SharpGenTools/SharpGenTools/blob/6990bcafe124a4c22515ad19cee5a081da8db67b/LICENSE.txt)

FloatCanvas's own source code is licensed under the MIT license in `LICENSE`. The third-party components above retain their respective licenses and notices.
