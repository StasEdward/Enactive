# Enactive — Brand Guidelines

**v1.0 · September 2026 · enactive.dev**

Everything in this folder is the source of truth for how Enactive looks and sounds.
Tokens live in `brand.css`; logo files in `logos/`; raster exports in `png/`.

---

## 1. What the brand has to say

Enactive is not another chat client. It is a **desktop environment for AI agents** —
the model is the engine, Enactive is the environment the agent works in.

The whole product is one sentence:

> **One intent becomes real, reviewable, recorded action.**

Three ideas the identity has to carry:

| Idea | Where it comes from | How the identity shows it |
|---|---|---|
| **Action, not conversation** | You assign a task; you don't hand-drive a chat | A single hot accent colour that only ever means "something is happening" |
| **A closed loop** | `Intent → Plan → Tool → Artifact → Event → Memory` | The mark is an open loop that folds back on itself |
| **A place, not a window** | Workspace, timeline, environment model | Deep neutral ink surfaces; the accent is the only warmth in the room |

### Positioning lines

- **Primary:** A desktop environment for AI agents.
- **Long:** The operating environment for AI work. The model is the engine — Enactive is where it works.
- **Taglines:** *Assign the task. Review the result.* / *Real, reviewable, recorded action.* / *Not a chat. A workspace.*

### Voice

Direct, technical, unhyped. Enactive talks like a good engineer writing release notes:
concrete nouns, active verbs, no adjectives doing work a fact could do.

- Say **"the planner emits step dependencies"**, not "powerful AI-driven planning".
- Say **"asks before it runs a command"**, not "safety-first architecture".
- Never anthropomorphise the agent. It plans, runs, and reports; it does not "think", "want" or "understand".
- Never claim autonomy the permission model does not grant.

---

## 2. Logo

### The primary mark — **the Loop** (`mark-loop.svg`)

An open circle that turns almost all the way round, ends in an arrowhead pointing back
into its own gap, and holds a solid dot at the centre. The ring is the run loop; the gap
is the point where a human decision fits; the dot is the artifact — the result the whole
loop exists to produce.

**Locked geometry**, on the 64-unit grid (everything else in the system is drawn to match):

| | |
|---|---|
| Centre / radius | `32, 32` / `21` |
| Stroke | `8`, round caps |
| Gap | `62°`, centred on `47°` (up and to the right) |
| Arrowhead | length `11.5`, half-width `6.8`, base sunk `2` into the stroke |
| Centre dot | radius `6.5` |

The sunk arrowhead base matters: sitting the triangle flush on the end of the arc leaves
the round cap poking out sideways and the head reads as a part glued on. Sunk, it reads as
the stroke tapering to a point.

### The full set

| File | Direction | Use |
|---|---|---|
| `mark-loop.svg` | Action loop | **Primary mark.** Product, favicon, avatars |
| `mark-loop-mono.svg` | Action loop | One-colour (`currentColor`) — stamps, embroidery, single-ink print |
| `mark-rotor.svg` | Action loop | Alternate: three arcs in motion (plan / execute / review) |
| `mark-enso.svg` | Action loop | Alternate: two arcs holding one result |
| `mark-e-slab.svg` | E monogram | Three bars = a three-step plan, the short middle bar running |
| `mark-e-tile.svg` | E monogram | Filled app tile version of the slab E |
| `mark-e-spine.svg` | E monogram | E whose middle arm is an execution arrow |
| `mark-workspace.svg` | Environment | The three-panel environment: rail, artifact, console |
| `mark-dag.svg` | Environment | The plan as a DAG: fork, run in parallel, join on the artifact |
| `mark-breakout.svg` | Environment | The agent reaching outside the window onto the real machine |
| `wordmark.svg` / `-light` / `-mono` | Wordmark | Text only. Outlined paths — no font dependency |
| `lockup-horizontal.svg` / `-mono` | Lockup | **Default logo.** Site header, README, slides |
| `lockup-stacked.svg` | Lockup | Square-ish spaces: splash screen, sticker, social avatar |
| `favicon.svg`, `favicon.ico` | Icon | Browser tab, taskbar |

### Rules

1. **Clear space** on every side equals the diameter of the mark's centre dot at the size in
   use — `13` grid units, or ~20% of the mark's width. Nothing enters it, including the page edge.
2. **Minimum size:** mark `20px`; horizontal lockup `120px` wide. Below that, use the mark alone.
3. **The mark is the only element that may carry the gradient.** It runs
   `--ember-500 → --amber-400` at 135°. On anything smaller than 32px, use flat `--ember-500`.
4. **On busy or photographic backgrounds** use `mark-loop-mono.svg` / `lockup-horizontal-mono.svg`
   in `--ink-50` or `--ink-900`, never the gradient.
5. **Do not:** re-colour the mark outside the ember/amber ramp; add a stroke, bevel, or shadow;
   rotate it; put it in a circle badge; stretch it; re-typeset the wordmark in another face;
   change the gap between mark and wordmark; write *EnActive*, *enActive*, or *Enactive.dev*
   with a capital D.
6. **Naming:** the product is **Enactive** (capital E, always). The domain is **enactive.dev**,
   always lowercase. In prose, the first mention is *Enactive*; `enactive` lowercase is only
   ever the CLI/assembly name. Never call it *AIClient* — that name was retired in 2026-09.

---

## 3. Colour

### Ember — the accent

`--ember-500 #FF5F26` is the only saturated warm colour in the system, and it carries one
meaning: **action**. A running step, a primary button, the live indicator, the artifact.
That meaning is the reason the palette works — if ember is sprinkled decoratively, the UI
loses the ability to say "look here, something is happening".

**Ember never marks a destructive action.** Delete, revoke and reject use `--danger`.

### Ink — the environment

A cool neutral ramp from `#0A0B0D` to `#F2F4F7`. Nine-tenths of the interface is ink.
Light mode is built on **Bone `#FAF8F5`**, a warm paper tone rather than pure white, so
that ember sits on it without vibrating.

### Contrast (measured, WCAG 2.1)

| Colour | on `--ink-950` | on `--bone` |
|---|---|---|
| `--ember-500 #FF5F26` | **6.49** | 2.86 — UI/large only |
| `--ember-300 #FF9C72` | **9.61** | — |
| `--ember-700 #BC380F` | 3.48 | **5.34** |
| `--ink-50 #F2F4F7` | **17.87** | — |
| `--ink-300 #8A93A3` | **6.36** | — |
| `--ink-900 #101216` | — | **17.69** |
| `--success #34D399` | **10.24** | 1.81 — use `--success-on-light` |
| `--danger #FF5D7A` | **6.66** | 2.79 — use `--danger-on-light` |

Rule of thumb: on dark use the `300–500` steps, on light use the `600–700` steps.
Body text is never set in ember at any size.

### Product-specific colour semantics

**Autonomy tiers** escalate in warmth, so the colour itself says how much rope the agent has:

| Tier | Token | Colour |
|---|---|---|
| Observe | `--autonomy-observe` | `#8A93A3` |
| Suggest | `--autonomy-suggest` | `#4C8DFF` |
| Execute | `--autonomy-execute` | `#FF5F26` |
| Autonomous | `--autonomy-autonomous` | `#FFB020` |

**Plan step status:** pending `--ink-400` · running `--ember-500` · done `--success` ·
skipped `--ink-500` · failed `--danger`.

**Worker roles** (avatars, timeline chips): Developer `#4C8DFF` · Reviewer `#34D399` ·
Ops `#A78BFA` · Writer `#FFB020`.

---

## 4. Typography

| Role | Face | Setting |
|---|---|---|
| Display / headings / wordmark | **Space Grotesk** 500 · 700 | tracking `-0.03em`, line-height 1.1 |
| UI and body | **Inter** 400 · 500 · 600 | tracking 0, line-height 1.55 |
| Code, paths, logs, model names | **JetBrains Mono** 400 · 500 | line-height 1.5 |

Both text faces are free and available via `@fontsource` (`space-grotesk`, `inter`,
`jetbrains-mono`) — bundle them with the app rather than hot-linking, so the desktop UI
renders identically offline.

Scale: `13 / 15 / 17 / 22 / 30 / 44 / 64` (`--step--1` … `--step-5`).

Uppercase is reserved for eyebrow labels — `13px`, weight 600, tracking `0.12em`, in
`--text-muted`. Never set a heading in all caps.

The wordmark is **Space Grotesk 700 outlined to paths**. Do not set it live in text.

---

## 5. Geometry and motion

Radii: `6 / 10 / 14 / 20 / 999`. Cards are `14px`; the app icon uses `14` on the 64-unit
grid (≈22% of the side) so it matches platform icon masks.

Spacing is a 4px grid: `4 8 12 16 24 32 48 64`.

Motion is short and mechanical: `120ms` for state, `180ms` for most transitions, `320ms`
for a panel. Easing `cubic-bezier(.2,.6,.2,1)`. The only continuous animation permitted is
the running-step indicator — a 1.6s ember pulse. Nothing else loops; a workspace that
shimmers is a workspace nobody can read.

---

## 6. Applying it

```html
<link rel="stylesheet" href="/brand/brand.css">
<link rel="icon" href="/brand/logos/favicon.svg" type="image/svg+xml">
<link rel="alternate icon" href="/brand/favicon.ico">
<link rel="apple-touch-icon" href="/brand/png/apple-touch-icon-180.png">
```

- Reference tokens only: `background: var(--bg)`, `color: var(--text)`. No raw hex in
  component code.
- In the Avalonia UI, mirror `brand.css` as a `ResourceDictionary` with the same token names
  so both surfaces stay in step.
- Dark is the default theme. Light mode is a supported equal, not an afterthought — check
  both before shipping a screen.

---

## 7. Checklist before shipping a surface

- [ ] Ember appears only where something is running, primary, or is the result.
- [ ] No destructive action is ember-coloured.
- [ ] Every text/background pair reaches 4.5:1 (3:1 for text ≥ 24px).
- [ ] The logo has its clear space and is above minimum size.
- [ ] The gradient appears on the mark only.
- [ ] The surface reads correctly in both themes.
- [ ] Copy names the product *Enactive*, the domain *enactive.dev*.
