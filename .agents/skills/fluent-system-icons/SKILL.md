---
name: fluent-system-icons
description: Resolve Microsoft Fluent System Icons from a semantic request into the official icon name, size, regular/filled variant, Unicode code point, SVG URL, and ready-to-paste WinUI XAML. Trigger whenever the user asks for a Fluent icon, icon ID, glyph, Unicode value, SVG path, PathIcon, FontIcon, SymbolIcon, AppBarButton icon, sidebar icon, navigation icon, or asks to find/choose an icon from Fluent System Icons. Treat this as the default lookup workflow instead of guessing glyph IDs.
compatibility: Requires Python 3.9+; network access is used on the first lookup to cache the official Microsoft icon metadata. Works from PowerShell, bash, and project repositories.
---

# Fluent System Icons lookup

Use the official [microsoft/fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons) repository as the source of truth. The repository exposes names such as `ic_fluent_panel_left_24_regular`, not a single universal “icon ID”. A glyph code point is only valid with the matching `FluentSystemIcons-Regular` or `FluentSystemIcons-Filled` font.

## Select the right Fluent collection first

Fluent 2 has three different icon collections:

- **System icons** — navigation, command bars, status, and common actions. This Skill and `icons_regular.md` target this collection.
- **Product launch icons** — Microsoft product or capability identity, often shown in the colorful product-icon reference image. They are not interchangeable with system icons and should not replace toolbar or navigation controls.
- **File type icons** — formats such as PDF, Word, or ZIP. They are a separate collection for identifying files.

If the request mentions a product logo, product launch artwork, or the colorful Microsoft product-icon sheet, stop using this system-icon lookup and route to the appropriate product-launch asset or image workflow. Do not return a system icon merely because its name is semantically close.

The generated `icons_regular.md` catalog is an index, not the icon package itself. Its preview column selects the largest available SVG for each icon and displays it at 24px, while the same row can list 16/20/24/28/32/48 variants. Compare the exact `size` file when visual fidelity matters.

## Workflow

1. Convert the user's intent into a short English concept. Keep the UI action separate from the visual style: “收起侧栏” may be `panel-left` or `panel-right-expand`, while “regular” and “filled” are variant choices.
2. Run the bundled lookup script instead of guessing names or hexadecimal values:

   ```powershell
   python "$env:USERPROFILE\.agents\skills\fluent-system-icons\scripts\fluent_icon_lookup.py" "收起侧栏" --size 24 --style regular --format all
   ```

3. Return two or three candidates when the intent is ambiguous. Explain the semantic difference and recommend one; do not dump the entire result set.
4. Prefer the implementation that matches the host platform:
   - `SymbolIcon` or `AppBarButton` with a known WinUI `Symbol` is simplest and does not require a font asset.
   - `PathIcon` is the safest way to use an official Fluent SVG in a WinUI app that does not package the Fluent System Icons font.
   - `FontIcon` with `FontFamily="FluentSystemIcons"` is valid only when that exact font is installed or included in the app package. Do not substitute `Segoe Fluent Icons` and do not reuse MDL2 glyph values.

## Size and geometry consistency

Treat visual consistency as an acceptance requirement whenever several icons appear in one toolbar, navigation area, button group, or stateful control.

1. Choose one **source size** for the whole component group, such as `20_regular`, before looking up individual icons. Do not mix files such as `*_20_regular.svg` and `*_28_regular.svg` even when XAML renders both at `20×20`; Fluent size variants use separately drawn geometry rather than a shared path scaled uniformly.
2. Use the same style and weight within the group (`regular`, `filled`, or `light`) unless a selected state intentionally changes weight. A deliberate weight change still requires checking its visual bounds.
3. Give every icon the same render box and container metrics: identical `Width`, `Height`, padding, alignment, and button hit area. For a WinUI title-bar button, prefer a `20×20` icon centered in the existing `40×40` button unless the project defines another token.
4. For two states of the same control, require the same `viewBox` and the exact same outer-frame geometry. Only the internal modifier—arrow, plus, dismiss mark, and similar—should change. Using equal source sizes is necessary but not sufficient because two official variants can still have different outer bounds.
5. If official state variants have different height, corner radius, divider position, or outer bounds, normalize them by reusing one base frame and changing only the modifier, or choose a different matched pair. Do not silently accept the mismatch and do not fix it by arbitrary independent scaling.
6. Before returning or applying icons, include a consistency check in the result: `source: 20 regular; viewBox: 20×20; render: 20×20; frame: shared`. If any value differs, warn before editing.

## Output contract

For every recommendation, provide:

- official icon name, size, and style;
- decimal code point, hexadecimal code point, and XAML entity when available;
- official SVG URL;
- one copyable snippet for the requested target (`PathIcon`, `FontIcon`, or `SymbolIcon`);
- the shared source size, viewBox, render size, and outer-frame consistency result when multiple icons are involved;
- a one-sentence note about font/package requirements.

When the user asks for a path, rerun with `--fetch-svg --format xaml` and use the generated `PathIcon.Data`. Preserve the SVG's geometry; do not redraw the icon or replace it with an emoji, Unicode symbol, or hand-made approximation.

## Lookup examples

```powershell
# Search Chinese aliases and return compact candidates
python .\scripts\fluent_icon_lookup.py "刷新" --size 24 --style regular

# Produce WinUI snippets and fetch official SVG geometry
python .\scripts\fluent_icon_lookup.py "panel left" --size 20 --style regular --fetch-svg --format xaml

# Inspect exact metadata in automation-friendly form
python .\scripts\fluent_icon_lookup.py "arrow download" --format json
```

The script caches the two official font maps under `~/.cache/fluent-system-icons/`. Use `--refresh` when the repository releases new icons. If network access is unavailable, use the cached map and report that SVG geometry could not be refreshed.

## WinUI cautions

`FluentSystemIcons-Regular.json` and `FluentSystemIcons-Filled.json` are not the WinUI `Symbol` enum and are not the `Segoe Fluent Icons` font map. If a requested icon is missing from the official map, say so and offer the nearest semantic alternative. Do not invent a code point.
