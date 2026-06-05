# Real-world findings — results without running the app

We didn't just test the verifier on our own clean mock labels. We drove the **live site**
through a real browser (Puppeteer) using **actual product photographs** from Wikimedia
Commons — and ran each one **twice**: once with the cloud engine (`gpt-4o`) and once with the
in-app **"Simulate agency firewall"** toggle (offline OCR). Harness:
[`../realtest/drive.js`](../realtest/drive.js).

The photos are deliberately *unforgiving* — the kind of phone snaps Jenny described (angles,
glare, curved labels, dim light, tiny text). This page shows what came back, so an evaluator
can see the behaviour without standing the app up.

---

### 1. Evan Williams — two bottles, angled, reflective glass, script font

<img src="images/evan_williams.jpg" width="360" alt="Evan Williams bottles" />

| The application said | Cloud `gpt-4o` read | Offline OCR read |
|---|---|---|
| Brand: **Evan Williams** | ✅ *Evan Williams* (exact) | ❌ not found |
| Class: **Kentucky Straight Bourbon Whiskey** | ✅ exact | ❌ not found |
| Government Warning | ❌ not present (front label) | ❌ not present |

**Verdict: Needs attention** (warning not visible). The cloud engine read both fields off
curved, script-font labels; plain OCR recovered nothing.

---

### 2. Gentleman Jack — small label, shot across a counter

<img src="images/gentleman_jack.jpg" width="420" alt="Gentleman Jack bottle on a counter" />

| The application said | Cloud `gpt-4o` read | Offline OCR read |
|---|---|---|
| Brand: **Gentleman Jack** | ✅ exact | ❌ not found |
| Class: **Tennessee Whiskey** | ✅ (label: "Rare Tennessee Whiskey") | ❌ not found |
| Net contents: **750 mL** | ✅ matches | ❌ not found |
| ABV: **40%** | ⚠️ *"no alcohol content could be read"* | ❌ not found |
| Government Warning | ❌ not present (front label) | ❌ not present |

**Verdict: Needs attention.** Two things to notice:
- The label is *tiny* in the frame. Cropped and zoomed, it's perfectly legible — which is
  exactly why whole-image OCR fails here and a label-localisation step would help (see the
  [engineering notes](ENGINEERING-NOTES.md#why-the-offline-ocr-fails-so-badly)):

  <img src="images/gentleman_jack_label_zoom.jpg" width="300" alt="Zoom of the Gentleman Jack label" />

- `gpt-4o` **failed honestly** on the ABV — it said it couldn't read it rather than inventing
  a number. For a compliance tool, *not* hallucinating is the correct behaviour.

---

### 3. Old Rip Van Winkle — handheld, dim light, serif type — *the case for a human in the loop*

<img src="images/old_rip_van_winkle.jpg" width="360" alt="Old Rip Van Winkle bottle" />

| The application said | Cloud `gpt-4o` read | Result |
|---|---|---|
| Brand: **Van Winkle** | *Van Winkle Special Reserve* | ⚠️ **Review** — see below |
| Class: **Kentucky Straight Bourbon Whiskey** | exact | ✅ |
| ABV: **45.2%** | *45.2% Vol. (90.4 Proof)* | ✅ |
| Net contents: **750 mL** | *750 ml* | ✅ |
| Government Warning | not present (front label) | ❌ |

**This image is why the tool keeps a human in the loop.** The application named the brand
*"Van Winkle"*; the label actually prints *"Van Winkle Special Reserve."* A naïve exact-match
**fails** an obviously-related product; a naïve fuzzy-match might **wave through** a different
one. Because this is an **official record where the slightest discrepancy can be
disqualifying**, the tool does neither — it returns **Review** with a plain-language prompt:

> *"The application value is part of the fuller name on the label — please confirm it is the
> same product."*

(We deliberately do **not** auto-pass this: *"Crown"* vs *"Crown Royal"* are different
products. The machine narrows the work; the agent makes the call.)

---

## What the real test taught us

1. **Cloud vision vastly outperforms plain OCR on real photographs.** Across all three, `gpt-4o`
   read curved/reflective/script labels accurately; Tesseract recovered almost nothing from the
   same images. This is the honest limit of the offline fallback — a free, always-available
   safety net behind the firewall, **not** an equal substitute. (How to make it far better:
   [engineering notes](ENGINEERING-NOTES.md).)
2. **No false approvals.** Every front-label photo was correctly flagged because the Government
   Warning (a back-label item) wasn't visible — the tool behaves like an agent who'd request the
   back label, rather than passing an incomplete submission.
3. **The machine triages; the human decides.** Exact matches auto-clear (Sarah's "drowning in
   routine" load). *Any* residual discrepancy is escalated, never silently passed — the right
   posture for an official determination.

---

## Image credits

Test photographs are used under their Creative Commons licences; they are **not** redistributed
as part of the application (only shown here for documentation).

- *Evan Williams bottles* — Kenneth C. Zirkel, [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/) ([source](https://commons.wikimedia.org/wiki/File:Evan_Williams_white_label_and_black_label_whiskey_bottles.jpg))
- *Gentleman Jack* — Bruce Tuten, [CC BY 2.0](https://creativecommons.org/licenses/by/2.0/) ([source](https://commons.wikimedia.org/wiki/File:Gentleman_Jack_Whiskey_(8743148250).jpg))
- *Old Rip Van Winkle* — Joe Hall, [CC BY 2.0](https://creativecommons.org/licenses/by/2.0/) ([source](https://commons.wikimedia.org/wiki/File:Old_Rip_Van_Winkle_Whiskey_301243232.jpg))
