# NOTICE

Blobmoji Vector is derived from Blobmoji:

- Blobmoji repository: https://github.com/C1710/blobmoji
- Commit used: 7dd14d2b0141693485fd26bc35817bd290352a79
- Upstream license: Apache License 2.0 for tools and most image resources; SIL Open Font License 1.1 for emoji fonts
- The original font distribution was last released as v15.1-beta1

The following modifications were made for this build:

- Converted SVG artwork into a COLRv1/CPAL vector font.
- Converted SVG `<text>` elements to Comic Neue outlines.
- Renamed SVG files by Unicode sequence for font compilation.
- Added region flags and alias sequences.
- Removed three embedded raster texture elements.
- Repaired corrupt path data introduced by upstream skin-tone color substitution.
- Normalized SVG units, CSS classes, empty paths, off-canvas paths and malformed colors.
- Clamped one out-of-range radial-gradient coordinate to the F2Dot14 range.

Comic Neue is Copyright (c) 2014 The Comic Neue Project Authors, licensed under the SIL Open Font License 1.1.

The region flag SVG images are from the `region-flags` project and are public domain or otherwise exempt from copyright as described in `LICENSE-region-flags.txt`.

This project is not affiliated with Google, Microsoft, Emojipedia, or the original Blobmoji authors.
