# AI Gym brand guide

<img src="avatars/01-spring.png" alt="Spring, a mint-green flying squirrel with one paw raised" width="180" align="right">

AI Gym is a Windows app for practising with local AI: shape a dataset, train an adapter, compare held-out answers from two models and keep the evidence on your own PC. The identity is about repetition that leads somewhere. The logo is an open G, a loop that ends in a small lifted square, and the tagline finishes the thought: **Practice makes possibility.**

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="logo-kit/png/brand-reversed-1024w.png">
  <img src="logo-kit/png/brand-primary-outlined-1024w.png" alt="AI Gym logo: an open green G with a small lifted square, beside the AI Gym wordmark" width="320">
</picture>

## The mascot

**Spring** is a flying squirrel modelled in soft, matte clay: mint fur, evergreen ears and tail, a cream face and large dark eyes. The tail curls like the G and the raised paw stands in for its lifted square. Spring is alert, encouraging and slightly cheeky, the training partner who thinks you have one more attempt in you.

Four companions share the same clay and colours. **Ripple** the axolotl is quietly observant, **Tempo** the owl is patient and methodical, **Pivot** the turtle is thoughtful and resourceful, and **Zip** the rabbit is playful and hard to discourage.

<img src="avatars/01-spring.png" alt="Spring" width="88"> <img src="avatars/02-ripple.png" alt="Ripple" width="88"> <img src="avatars/03-tempo.png" alt="Tempo" width="88"> <img src="avatars/04-pivot.png" alt="Pivot" width="88"> <img src="avatars/05-zip.png" alt="Zip" width="88">

Where Spring appears in the app:

- **Title bar and app icon.** Inside the app Spring is the logo: a 32 px Spring sits next to the wordmark, and the taskbar icon is Spring too.
- **Home.** Spring cheers beside the tagline ("You got this") and shrinks when the window is narrow.
- **Empty states.** A reaction sits above a short title and one next step. "You got this" invites a start, "I'm listening" waits for a file or a search, and "Let me think" marks a comparison or an experiment that has not been opened.
- **Results.** "That worked" plays once as a short animation when an export or a training run finishes, then settles to a still. "Try again" replaces the artwork when something fails. If animations are turned off in Windows, only the stills are shown.

Eight reactions ship with the app; the pages use five of them today. The companions are for profiles, documentation and community spaces, and do not appear in the app.

Please don't:

- Put Spring on the working surfaces, such as charts, tables and training logs.
- Let Spring claim a result. A celebration appears only after something has actually finished.
- Recolour Spring, crop the ears or tail, or place the round avatars on a busy background.
- Loop the animations. Each one plays once.

## Logo

| File (in `logo-kit/svg`, PNG copies in `logo-kit/png`) | Use it for |
| --- | --- |
| `brand-primary-outlined` | The default: the G and the wordmark side by side, on light backgrounds. |
| `brand-reversed` | The same lockup in white, for dark backgrounds. |
| `brand-symbol` | The G alone: favicons, profile pictures and other square spaces. |
| `brand-mono` | One-colour printing, in ink. |
| `brand-wordmark-outlined` | The lettering alone. The app places it next to Spring. |

- **Minimum size.** In marketing and print, keep the logo at least 180 px wide and the symbol at least 24 px. The app's 48 px title bar uses a compact lockup instead: a 32 px Spring and a 78 px wide wordmark, which works there because the window frames it.
- **Clear space.** Leave a margin as wide as the stroke of the G on every side.
- **Do** use the SVG wherever you can, the PNG where you cannot, and the reversed logo on dark backgrounds.
- **Don't** stretch, rotate or recolour the logo, add shadows or outlines, or set the name in a font yourself. The wordmark is drawn artwork (Manrope at weight 750, converted to outlines).

`logo-kit/preview/logo-usage.png` shows the four main versions on one sheet.

## Colour

Greens throughout: a pale, cool paper, deep evergreen ink and one strong green for action. The dark theme is a separate set of colours, not the light one inverted. `tokens.json` and `tokens.css` hold the same values for code.

**Light theme**

| Swatch | Hex | Used for |
| --- | --- | --- |
| Paper | `#F4F8F6` | Page background |
| White | `#FFFFFF` | Cards and panels; text on green buttons |
| Mist | `#EAF2EE` | Quiet inset areas, such as loading placeholders |
| Ink | `#102C2B` | Text and the wordmark |
| Muted | `#405F5D` | Secondary text and captions |
| Accent | `#006B58` | Buttons, links, selected items, progress, and the G itself |
| Line | `#667F79` | Strong borders; ordinary borders use it at 30% opacity |
| Ochre | `#7A4E00` | Warning text |
| Brick | `#A3302B` | Error text |
| Gold | `#C98A1B` | The held-out share in dataset split charts |

**Dark theme**

| Swatch | Hex | Used for |
| --- | --- | --- |
| Night | `#0B2020` | Page background; text on mint buttons |
| Raised | `#153332` | Cards and panels |
| Deep | `#0F2828` | Quiet inset areas |
| Paper | `#F4F8F6` | Text |
| Silver | `#B7D2CB` | Secondary text and strong borders; ordinary borders use it at 20% opacity |
| Bright | `#76E4C0` | Buttons, links, selected items and progress, with Night text |
| Sand | `#F2CC7A` | Warning text and the held-out share in charts |
| Blush | `#FFA59E` | Error text |

Contrast, measured with the WCAG formula: Ink on Paper is 13.8:1, Muted on Paper 6.5:1, Accent on Paper 6.0:1 and white on Accent 6.5:1. In dark, Paper on Night is 15.8:1, Silver on Night 10.6:1 and Night on Bright 11.0:1. Line on Paper is 4.0:1, enough for borders and not for text. When Windows high contrast is on, the app drops the brand colours and uses the system ones.

## Typography

- **Manrope** (SIL Open Font License 1.1) for the wordmark and for headings, which are set bold. The font file and its licence are in `logo-kit/fonts`.
- **Segoe UI Variable**, the Windows system font, for controls and body text. It is part of Windows and is not shipped with the app.

## Voice

Encouraging and exact. AI Gym tells you what to do next and what has really happened, and never dresses up a result.

| Write | Rather than |
| --- | --- |
| "Bring some conversations" | "No dataset loaded." |
| "No training runs yet. Fill in the inputs, then choose Check & prepare." | "Start crushing it with your first run!" |
| "Nothing is downloaded until you review and confirm a download." | "Downloads are safe and secure." |
| "Nothing is shown until responses are actually observed." | "Results will appear here shortly." |
| "Existing bundles are never overwritten." | "Don't worry, your data is fine." |
| "Both answers, exact matches and measured latency go into an evidence file you can reopen any time." | "See which model is smarter." |

Say what is saved, where it goes and what still needs your approval. A saved connection is not a running model, and a finished run is not proof that the model got better.

## Files in this folder

| Path | What it holds |
| --- | --- |
| `tokens.json`, `tokens.css` | The palette, light and dark theme colours and fonts, for code |
| `logo-kit/svg`, `logo-kit/png` | The logo versions listed above (PNG at 512 to 4096 px wide; the symbol from 64 to 2048 px) |
| `logo-kit/favicon` | Favicons, touch icons and a web manifest |
| `logo-kit/preview` | The logo usage sheet |
| `logo-kit/fonts` | Manrope and its licence |
| `avatars` | Spring and the four companions on round backdrops, 512 px |
| `graphics` | Share images as SVG and PNG: 1200 × 630 Open Graph, 1080 × 1080 square, 1500 × 500 header, a 1024 px profile picture and a 512 px maskable icon |

The app keeps its own copies of the art it shows, including the reactions, in [`src/AiGym/Assets`](../src/AiGym/Assets). The website is in [`site`](../site).

## Licence

Everything in this repository, code and artwork alike, is under the [MIT licence](../LICENSE). Manrope is under the [SIL Open Font License 1.1](logo-kit/fonts/LICENSE-Manrope.txt).
