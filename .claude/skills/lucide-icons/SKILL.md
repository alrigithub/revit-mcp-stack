---
name: lucide-icons
description: Use when building any UI, artifact, HTML page, dashboard, or mockup that needs icons (buttons, nav, status indicators, empty states), or when the user asks for a Lucide icon by name. Fetches official Lucide SVGs on demand — no npm install needed.
---

# Lucide Icons (fetch on demand)

## Overview

Fetch any of the ~2,000 official Lucide icons as an SVG with one curl (Windows ships `curl.exe` natively — the `-L` is required, unpkg redirects to the versioned URL):

```
curl.exe -sL https://unpkg.com/lucide-static/icons/<icon-name>.svg
```

Example: `curl.exe -sL https://unpkg.com/lucide-static/icons/arrow-right.svg`

This serves the official `lucide-static` npm package via CDN. A wrong name returns an unpkg "Cannot find" error page instead of an `<svg>` — check the name, don't retry.

## Finding an icon name

`catalog.txt` in this skill's folder has one line per icon: `name: search tags` (Lucide's official tags — synonyms and concepts). To find a *fitting* icon rather than an exact name, grep 2–3 concept words in one pass and pick the best match from the results:

```
Grep pattern "repair|configure|maintenance" in ~/.claude/skills/lucide-icons/catalog.txt
```

The icon name is everything before the first `:` on a matched line. Grep case-insensitively; tags are lowercase.

Naming conventions worth knowing:
- Variants are numbered: `trash` / `trash-2`
- Shape-wrapped variants are prefixed: `circle-check`, `square-pen` (not `check-circle`)
- Directions are suffixed: `chevron-down`, `arrow-up-right`, `panel-left-open`

## Using an icon

Inline the fetched `<svg>` element (drop the `<!-- @license -->` comment line). Every icon is:

- 24×24 `viewBox`, `fill="none"`, `stroke="currentColor"`, `stroke-width="2"`

Customization (same four knobs as lucide.dev's Customizer):

- **Color**: inherits CSS `color` from its parent — no edits needed (or set `stroke` directly)
- **Size**: override `width`/`height` attributes or via CSS
- **Stroke width**: adjust `stroke-width` (1.5 for lighter, 2.5 for bolder)
- **Absolute stroke width**: strokes are in viewBox units, so they scale with rendered size (bigger icon = visually thicker stroke). To keep the stroke visually ~2px at any size, set `stroke-width = 2 × 24 ÷ size`: 16px → `3`, 24px → `2`, 48px → `1`, 64px → `0.75`

Fetching several icons: batch them in one shell call (loop over names, concatenate output) rather than one call per icon. For many icons on one page, build a `<symbol>`/`<use>` sprite from the fetched SVGs to avoid repetition.

## Common mistakes

- Installing `lucide`/`lucide-react`/`lucide-static` via npm — unnecessary, just curl
- Omitting `-L` — you get a redirect notice, not the SVG
- Guessing names like `check-circle` (it's `circle-check`) — grep `catalog.txt` first
- Reading all of `catalog.txt` into context — grep it; matches are enough to choose from
- Setting `fill` to color an icon — these are stroke-based; set `color`/`stroke` instead

## License

Lucide is ISC-licensed (permissive) — safe to embed in any project.
