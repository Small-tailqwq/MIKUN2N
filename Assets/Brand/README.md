# MikuN2N logo

[English](README.md) | [简体中文](README.zh.md)

The C1 artwork combines an upright, angular M with twin-tail shapes. Red shoulder inserts and cyan, yellow, and orange facets on the lower right leg recall Racing Miku 2016. Grayscale and light grayscale versions preserve every colored face with solid gray tones, including the shoulder folds, central facets, and the orange facet's lower edge. Adjacent tones define the boundaries without transparent gaps.

| File | Use |
| --- | --- |
| `mikun2n-mark-color.svg` | Standalone full-color mark |
| `mikun2n-mark-gray.svg` | Grayscale mark for light backgrounds |
| `mikun2n-mark-light.svg` | Light grayscale mark for dark backgrounds |
| `mikun2n-mark-black.svg` | Dark single-ink mark with fine transparent cuts |
| `mikun2n-mark-white.svg` | White single-ink mark with fine transparent cuts |
| `mikun2n-lockup-color.svg` | Full-color mark with dark MikuN2N lettering |
| `mikun2n-lockup-gray.svg` | Grayscale mark and dark lettering |
| `mikun2n-lockup-light.svg` | Light grayscale mark and light lettering |
| `mikun2n-lockup-black.svg` | Dark single-ink mark and lettering |
| `mikun2n-lockup-white.svg` | White single-ink mark and lettering |
| `preview.png` | Color, grayscale, and light grayscale overview |

All standalone marks use an 800 × 800 square canvas with the original silhouette centered without stretching. Their preview tiles are also square. The lettering combinations use an 800 × 690 canvas.

All SVGs have transparent backgrounds and contain editable vector paths, including the lettering. They require no font installation, embedded bitmap, mask, or external resource. Scale proportionally. The black and white variants use 2.4-unit cuts on an 800-unit canvas for single-ink applications; changing their path fill preserves those transparent cuts.

The application uses the color mark. `App.xaml` contains its vector paths as the shared `MikuN2NLogo` drawing, displayed in the main window and About tab and returned by `Services/AppIconService.cs` for window icons. `MikuN2N.ico` contains 16, 20, 24, 32, 40, 48, 64, 128, and 256-pixel frames rendered from `mikun2n-mark-color.svg`; the executable and tray use this same file. Keep these derived assets synchronized with the color SVG when editing the mark.
