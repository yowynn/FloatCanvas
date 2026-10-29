# Installer language resources

`ChineseSimplified.isl` is an unmodified copy of the Simplified Chinese translation
listed in the [official Inno Setup translation catalog](https://jrsoftware.org/files/istrans/).
Its maintainer attribution remains in the file. English uses the pinned compiler's
`Default.isl` directly.

- Upstream repository: [jrsoftware/issrc](https://github.com/jrsoftware/issrc).
- Pinned revision: `6ef32198ef1f7b7b375cd4b6b90896c2a58eb4c2`.
- Source: [`Files/Languages/ChineseSimplified.isl`](https://github.com/jrsoftware/issrc/blob/6ef32198ef1f7b7b375cd4b6b90896c2a58eb4c2/Files/Languages/ChineseSimplified.isl).
- SHA-256: `e0b0b350e2245f3c5e65586dfe43d574f6e7f06f2261149aba284954b3fc9a8d`.
- License: [Inno Setup License](Inno-Setup-LICENSE.txt), copied from the same revision; SHA-256 `2e5346868c2a18434489824e11d65c3031620f792fefc415d05f19cd441abf5c`.

The translation covers all 296 active keys in the `LangOptions`, `Messages` and
`CustomMessages` sections of Inno Setup 6.7.4's `Default.isl`, with matching
substitution placeholders. Keep that coverage when changing compiler versions.
Project-specific installer messages belong in `../Messages.iss` and must have both
English and Simplified Chinese variants.
