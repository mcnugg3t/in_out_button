# Brief: UI candidates for two new tabs in the In / Out Button

Shared by the two builders. Each builds ONE html page holding THREE candidates. Caleb
picks one per tab (he may mix: usage from A, tasks from C). He iterates by rejection,
so the three candidates must be genuinely different ideas, not three colorways.

## The host app (what the mockups must look like they live in)

A .NET 8 WinForms desktop app, light flat theme, built in code. The new tabs sit in
the app's middle row, roughly 1100 x 560 px, between a top button row (Sign in /
Sign out) and a dark log panel at the bottom. Render each candidate inside a
framed "tab panel" of that size so it reads as a screen of the app, not a web page.

App theme tokens (from Form1.cs `Theme`), use these as the LIGHT palette:

| token | rgb | role |
|---|---|---|
| WindowBg | 241,243,246 | page background |
| CardBg | 255,255,255 | cards |
| Text | 24,33,47 | primary text |
| SubtleText | 120,128,140 | secondary text |
| Border | 216,220,227 | 1 px card borders |
| Accent | 37,99,235 | primary action, selection |
| Slate | 51,65,85 | secondary strong (Sign out button) |
| Hover | 237,241,247 | hover fill |
| Ok | 21,128,61 | status good |
| Warn | 180,83,9 | status warning |
| Fail | 220,38,38 | status critical |
| Selection | 219,234,254 | selected row |
| AltRow | 248,249,251 | zebra |
| LogBg | 15,23,42 | the dark log panel |
| LogFg | 203,213,225 | log text |

Fonts: `"Segoe UI", system-ui, -apple-system, sans-serif` at 13 px base (the app uses
Segoe UI 9.75 pt), semibold for headings, `Consolas, ui-monospace, monospace` for
timestamps and log-like text. Tabular numerals everywhere digits align. Radius: small
(4 to 6 px). Cards: white, 1 px Border, no shadow. Buttons: flat, Accent fill white
text for primary, white with Border for secondary.

DARK theme: required by the page contract (tokens on :root, redefined under the two
dark blocks). Base it on LogBg navy (15,23,42) with cards at about (22,32,55), text
LogFg, and keep Accent legible on it. Caleb likes dark, neon-accented styling for his
own tools, so the dark variant may be slightly more vivid than the light, but it is
the same design, not a different one.

## Page mechanics (the Artifact page contract, non-negotiable)

- Author plain HTML content: `<title>` and `<style>` at the top, then markup, then
  `<script>`. No `<!doctype>`, `<html>`, `<head>`, `<body>` tags.
- `<title>` is a NAME, two to four words, no colon or dash explainer. Usage page:
  `In / Out Usage Candidates`. Tasks page: `In / Out Tasks Candidates`.
- Put `<meta name="claude:viewport-fit-sides" content="cover">` beside the title AND
  give the outer wrapper `padding-left: max(16px, env(safe-area-inset-left, 0px))`
  and `padding-right: max(16px, env(safe-area-inset-right, 0px))` with vertical
  padding via `padding-block`.
- All colors are CSS custom properties first defined on bare `:root` (light), then
  redefined under `@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) {...; color-scheme: dark} }`
  and again under `:root[data-theme="dark"] {...; color-scheme: dark}`. No literal
  color in any component rule. `body { background: var(--bg); color: var(--fg) }`.
- No external scripts or fonts. Inline SVG for any chart. No libraries.
- The page body must never scroll horizontally at 400 px width. Each 1100 px
  candidate frame sits in its own `overflow-x: auto` container (a UI mockup counts
  as a diagram), so narrow screens scroll the frame, not the page.
- Complete at rest: everything visible on load, no scroll-triggered reveals.
- Respect `prefers-reduced-motion`; visible keyboard focus on any control.
- Hover tooltips on data marks (the dataviz skill's rule): hovering a meter shows
  the exact percent, the absolute reset time, and the window length.
- Keep the page under 200 KB.

## Page layout

Top: a one-line page header (the page name, and one sentence: what the reader is
choosing). Then the three candidates stacked vertically. Each candidate has:

1. A label row: `Candidate A`, a 3 to 6 word concept name, and ONE sentence saying
   what this design makes easiest to see. No marketing copy.
2. The 1100 x 560 framed panel.
3. Under the frame, a compact "trade-offs" line: `Best at: ... / Costs: ...` in one
   or two short sentences. Blunt and technically correct.

Nothing else. No methodology, no legend of the page itself, no footer.

## Writing rules

Short, plain, specific. Sentence case. No em dashes. No "not X but Y". Status is
never color alone: pair every colored state with a word or glyph. Numbers in the UI
are the real numbers given below, never invented ones, except where the brief says
"illustrative".

## Dataviz rules that bind here

Read these two files from the dataviz skill before building:
`C:\Users\caleb\AppData\Local\Temp\claude\bundled-skills\2.1.301\df5aa95a2c0596e02ece0d7155b3b33b\dataviz\references\marks-and-anatomy.md`
and `...\dataviz\references\anti-patterns.md`. The rules that matter most: thin marks,
recessive grid, text wears text tokens (never the series color), status colors
reserved for state and always paired with a label, one scale per axis, direct labels
selectively, hover layer by default.

If you place the three status colors (Ok, Warn, Fail) next to each other as fills,
run the validator once for each mode and report the result in your summary:
`node "...\dataviz\scripts\validate_palette.js" "#15803d,#b4530a,#dc2626" --mode light`
(and `--mode dark` with the dark card surface via its flag if it has one; read the
script header for flags). If a check fails, snap the hue to a passing step and say
what you changed. Provider identity (Anthropic / OpenAI / Gemini) is carried by
position and name, never by a color.

## Verify before you report

Screenshot the finished file with headless Edge and LOOK at the PNG with the Read
tool, once, then make one pass of fixes for anything it shows (clipped text,
overlapping labels, a frame that overflows, a dark-mode color that disappears):

```
& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --headless --disable-gpu --hide-scrollbars --window-size=1300,2300 --screenshot="<out>.png" "file:///<path to html>"
```

Take a second screenshot with `--force-dark-mode` appended, so the dark theme is
checked too. Report the screenshot paths. Do not loop: one look, one fix pass, done.

## Output contract

Write only the one html file you were assigned, in
`C:\Users\caleb\AppData\Local\Temp\claude\c--Users-caleb-Tools-in-out-button\b67374c0-077e-4796-9255-673844590120\scratchpad\mock\`.
Return a summary of at most 300 words: the three concepts in one line each, the
validator result, the screenshot paths, and anything you could not do. Do not commit,
stage, or push. Do not touch the app repo at C:\Users\caleb\Tools\in_out_button.
