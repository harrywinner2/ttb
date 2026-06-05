# TTB Label Verifier

An AI-powered prototype that checks an alcohol beverage **label image** against the
**application data** an agent has on file, and verifies the mandatory **Government
Health Warning** — in about five seconds, with a deliberately simple interface.

Built as a take-home prototype for the TTB (Alcohol and Tobacco Tax and Trade Bureau)
Compliance Division.

> **Live demo:** <https://ttb-label-verifier.happywave-a4140b3f.eastus2.azurecontainerapps.io>
>
> Try it with the ready-made images in [`samples/`](samples/) (drag one in, type a few
> expected values, click **Verify**), or load all six at once in **Check many at once**
> and upload [`samples/manifest.csv`](samples/manifest.csv) as the expected-values file.

---

## What it does

Given a photo of a label plus what the application claims, it reports a clear
**Pass / Needs attention / Please take a look** verdict and a per-field breakdown:

| Check | How it's judged |
|---|---|
| **Brand name** | Fuzzy match — tolerant of case/punctuation so `STONE'S THROW` matches `Stone's Throw`, but a genuinely different brand fails. |
| **Class / type** | Fuzzy + partial match (e.g. application says "Bourbon", label says "Kentucky Straight Bourbon Whiskey"). |
| **Alcohol content (ABV)** | Parses the percentage and compares with a small tolerance; also sanity-checks that Proof = 2 × ABV. |
| **Net contents** | Normalises units (mL / L / cl / fl oz) before comparing. |
| **Bottler / producer, Country of origin** | Optional fuzzy checks. |
| **Government Warning** | **Strict.** Must be present, **word-for-word** the statutory text, with the heading **`GOVERNMENT WARNING`** in **all capital letters**. (See note on *bold* below.) |

The Government Warning is the most-bent rule in real review (an agent's example: a label
using "Government Warning" in title case is a rejection), so it gets its own dedicated,
unforgiving check while the routine fields stay tolerant of trivial formatting noise.

The exact text enforced (27 CFR Part 16):

> **GOVERNMENT WARNING:** (1) According to the Surgeon General, women should not drink
> alcoholic beverages during pregnancy because of the risk of birth defects. (2) Consumption
> of alcoholic beverages impairs your ability to drive a car or operate machinery, and may
> cause health problems.

**Verdict policy (compliance-first).** The tool only **auto-clears (Pass)** an exact match —
after normalising case, punctuation, accents and whitespace — or a clean containment. *Any*
residual textual difference is escalated to **Review** for a human, never silently passed,
because on an official record even a small discrepancy can be disqualifying. A real example
that drove this — application "Van Winkle" vs label "Van Winkle Special Reserve" — is shown in
[docs/REAL-WORLD-FINDINGS.md](docs/REAL-WORLD-FINDINGS.md). The machine narrows the routine
load (Sarah's "drowning" problem); the agent makes the call.

**Single** and **batch** modes are both supported. Batch handles the "an importer dumped
300 applications on us" case: it accepts an optional **CSV or JSON** manifest of expected
values, runs products on an **asynchronous, parallel** job (the client polls for incremental
results, so no single request is held open), supports **multiple images per product**
(up to 4, any sides/angles), lets you filter to the failures, and exports to CSV.

---

## Why these technology choices

The discovery notes shaped every major decision:

- **Azure + .NET + Azure OpenAI.** The team already runs on Azure (post-2019 migration) and
  the COLA system is .NET. Building the prototype on the **same stack reuses existing
  resources and minimises future migration friction** — if this informs a real procurement,
  it already speaks the environment's language. The vision model is **Azure OpenAI
  `gpt-4o`**, which lives *inside* the Azure boundary rather than a third-party endpoint.

- **A fallback engine, because of the firewall.** IT warned that the network blocks outbound
  traffic to many ML endpoints — that's what sank the previous scanning vendor. So the label
  reader is **pluggable**: the primary engine is Azure OpenAI vision, and if it's
  unreachable (or unconfigured), the app **automatically falls back to fully-offline OCR
  (Tesseract)** bundled into the container. The same app keeps working behind the firewall;
  you can also force the offline engine to demo it.

- **Speed over cleverness.** The prior vendor lost the room at 30–40 seconds per label. The
  vision call is capped with a timeout and the batch path runs concurrently, targeting the
  "results in ~5 seconds" bar.

- **A UI a 73-year-old can use.** Half the team is over 50 with mixed tech comfort, so the
  interface is large-type, high-contrast, two obvious steps, plain-language verdicts, big
  green/red results, and no hunting for buttons. Keyboard- and screen-reader-friendly.

---

## Architecture

```
Browser (static HTML/CSS/JS in wwwroot)
   │  multipart upload: image(s) + expected fields (+ optional CSV/JSON manifest)
   ▼
ASP.NET Core 8 minimal API  (src/LabelVerifier)
   │  security headers · per-IP rate limit · non-root container
   │
   ├── single:  /api/verify
   └── batch:   /api/batch/jobs  →  job id        (async)
                /api/batch/jobs/{id}  →  progress + results   (poll)
                       │
                       ▼  bounded parallel workers
   ┌───────────────────────────────────────────────┐
   │ per product (1–4 images):                      │
   │   FallbackLabelReader ─► AzureOpenAiLabelReader │ (primary: gpt-4o vision → JSON)
   │                       └─► TesseractLabelReader  │ (offline OCR fallback)
   │   → merge readings (front=brand, back=warning)  │
   │   → LabelVerificationService                    │
   │        ├── TextMatching      (fuzzy match)      │
   │        └── GovernmentWarning (strict check)     │
   └───────────────────────────────────────────────┘
```

Key design points:

- **`ILabelReader`** makes the reading engine swappable; `FallbackLabelReader` owns the
  primary→fallback decision and reports which engine actually ran.
- **Multiple images per product are merged** into a single reading before verification, so one
  image can supply the brand while another supplies the Government Warning.
- **Batch is asynchronous and parallel.** A job is processed by bounded parallel workers and the
  client polls for incremental results, so no single request is held open (configurable degree;
  default 8).
- **Warning caps are derived from the model's *literal transcription*, not its self-report.**
  `gpt-4o` faithfully copies the heading's casing into the text but is unreliable when asked
  "is this all caps?" — so capitalisation is judged from the transcribed characters.
- **Bold is advisory.** Typographic weight can't be read by OCR and the vision model judges
  it unreliably, so a missing/uncertain bold never hard-fails an otherwise-correct label; it
  surfaces as a note for a human glance. (See *Trade-offs*.)

---

## Running it locally

### Option A — Docker (matches production; enables the offline fallback)

```bash
cd src/LabelVerifier
docker build -t labelverifier .

# With the cloud engine:
docker run -p 8080:8080 \
  -e AzureOpenAI__Endpoint="https://<your-resource>.openai.azure.com" \
  -e AzureOpenAI__Deployment="gpt-4o" \
  -e AzureOpenAI__ApiKey="<key>" \
  labelverifier

# Or offline-only (no credentials needed — uses bundled Tesseract):
docker run -p 8080:8080 labelverifier
```

Open <http://localhost:8080>.

### Option B — .NET SDK directly

Requires the .NET 8 SDK. For the offline fallback, also install the `tesseract-ocr` binary
(`apt-get install tesseract-ocr` / `brew install tesseract`).

```bash
cd src/LabelVerifier
export AzureOpenAI__Endpoint="https://<your-resource>.openai.azure.com"
export AzureOpenAI__Deployment="gpt-4o"
export AzureOpenAI__ApiKey="<key>"
dotnet run
```

Credentials come from environment variables / App settings only — **nothing secret is
committed**.

### Tests

```bash
cd tests/LabelVerifier.Tests
dotnet test
```

The suite covers the tricky correctness: fuzzy matching, ABV/proof parsing, and every branch
of the Government Warning check (missing, title-case heading, creative wording, the
model-says-caps-but-text-says-otherwise case, and advisory bold).

---

## Generating more test labels

[`samples/`](samples/) ships six labels (compliant bourbon/gin/wine plus deliberate
violations: title-case warning, missing warning, ABV mismatch) and a `manifest.csv` of their
expected values. Regenerate or extend them:

```bash
pip install Pillow
python3 samples/generate_labels.py
```

---

## Real-world testing

Beyond the synthetic samples, the **live deployment was tested with actual product
photographs** (sourced from Wikimedia Commons) by driving the real site through a headless
Chromium with Puppeteer — uploading each photo and reading the on-screen verdict, once with
the cloud engine and once with the **firewall toggle** (offline OCR). Harness:
[`realtest/drive.js`](realtest/drive.js).

> 📊 **See the results without running anything:** [**docs/REAL-WORLD-FINDINGS.md**](docs/REAL-WORLD-FINDINGS.md)
> shows each test photo next to what the cloud engine and the offline OCR read.
> 🛠️ The deeper write-up — **replacing the OCR**, plus **security** and **scalability** — is in
> [**docs/ENGINEERING-NOTES.md**](docs/ENGINEERING-NOTES.md).

Photos used (all genuinely hard — angles, reflections, script fonts, dim light, small text):
Evan Williams (two bottles, angled), Gentleman Jack (small label across a counter), Old Rip
Van Winkle (handheld, dim, serif). Fields each engine read **correctly**:

| Photo | Cloud `gpt-4o` | Offline OCR (firewall) |
|---|---|---|
| Evan Williams — angled, script | brand ✓, class ✓ | nothing legible |
| Gentleman Jack — small, distant | brand ✓, class ✓, net contents ✓ (missed tiny ABV) | nothing legible |
| Old Rip Van Winkle — dim, serif | class ✓, ABV 45.2% ✓, net ✓, bottler ✓ | brand only |

What the real test surfaced:

1. **Cloud vision dramatically outperforms OCR on real photos.** `gpt-4o` read curved,
   reflective, script labels accurately; Tesseract recovered almost nothing from the same
   images. This validates the cloud-primary design — and is the honest limit of the offline
   fallback: a free, always-available safety net, not an equal. Behind a real firewall you'd
   pair OCR with a *local* vision model.
2. **The Government Warning was correctly flagged missing on all three** — front-label photos
   don't show the back-of-bottle warning. No false positives; the tool behaves like an agent
   who'd ask for the back label.
3. **`gpt-4o` fails honestly.** On Gentleman Jack's tiny, distant text it reported
   "no alcohol content could be read" rather than inventing a number — the right behavior for
   compliance.
4. **A real edge case drove a fix.** `gpt-4o` read Old Rip Van Winkle's brand as
   *"Van Winkle Special Reserve"* while the application said *"Van Winkle"*. The strict brand
   check originally hard-failed this human-obvious match. It now flags a case where the
   application brand is a whole-word **subset** of the fuller on-label name for **Review**
   (confirm same product) — not a hard fail, and deliberately **not** an auto-pass
   (*"Crown"* vs *"Crown Royal"* are different products). Covered by a unit test and verified
   live.
5. **Latency** on full-resolution phone photos was **5.5–9.7s** for `gpt-4o` (above the 5s
   target on large images) vs **0.2–1.7s** for OCR. Downscaling images client-side before
   upload would bring the cloud path back under target — a worthwhile next step.

---

## Recommendation: require multiple images per product

The real-world testing made a structural point clear: **a single front-label photograph is
insufficient evidence for a full compliance check.** The mandatory Government Warning lives on
the *back* of the container, so a front-only image can never satisfy it — every real photo
tested was correctly flagged as "warning not present," which is accurate but not actionable.

The recommendation is therefore to **require submitters (importers, producers) to provide
multiple images per product** — up to **four** views that together capture **every labeled
surface**, whichever sides those happen to be (a tall bottle's front and back, or all four
faces of a boxy bottle or carton). The point is full coverage of the required text, not a
fixed set of shots. Each required element (brand, class/type, ABV, net contents, bottler,
country of origin, Government Warning) can then be located on whichever image carries it.

**Representing multiple images per product — CSV vs JSON.** A flat CSV maps one row to one
file, which does not express grouping cleanly. A **JSON manifest** is the better fit, because
each product owns an explicit list of images:

```json
[
  {
    "product": "Old Tom Bourbon 750",
    "images": ["oldtom_front.jpg", "oldtom_back.jpg"],
    "brand_name": "Old Tom Distillery",
    "class_type": "Kentucky Straight Bourbon Whiskey",
    "alcohol_content": "45% Alc./Vol.",
    "net_contents": "750 mL"
  }
]
```

**This is implemented.** The batch endpoint accepts either a CSV (one image per row,
back-compatible) or this JSON manifest. For a multi-image product, every image is read and the
results are **merged** into one verification — the front supplies the brand/ABV while the back
supplies the Government Warning — before the verdict is produced. (CSV remains supported for
the simple one-image-per-row case.)

---

## Deployment

The container image is hosted in **Azure Container Registry** and runs on **Azure Container
Apps** (serverless containers, public HTTPS, scales on demand). Azure OpenAI credentials are
injected as Container App secrets/environment variables — never baked into the image. The
container runs as a non-root user, sets HTTP security headers, and rate-limits per client IP.
See [`deploy/deploy.sh`](deploy/deploy.sh) for the exact, repeatable provisioning steps.

---

## Assumptions & scope

- **Prototype, not a system of record.** Per IT's guidance, it does **not** integrate with
  COLA and stores nothing: images are processed in memory and discarded. No PII retention, no
  database.
- The "application data" is entered by the agent (single mode) or supplied as a CSV/JSON
  manifest (batch mode), standing in for what COLA would provide.
- Warning enforcement targets the standard statement under 27 CFR Part 16; the many
  beverage-specific type-size/placement rules are out of scope for a prototype.

## Trade-offs & limitations

- **Bold detection is best-effort.** Reliably judging type weight needs layout/glyph
  geometry analysis; the vision model's self-report is too noisy to gate on, so bold is
  advisory. **Caps and wording — the violations agents actually catch — are enforced
  strictly** and proved out by tests. A production version would add a dedicated typographic
  check (font weight, type size, contrast).
- **The cloud engine isn't deterministic.** Field extraction can vary slightly run to run;
  the verdict logic is built to be tolerant where it should be and strict only where the rules
  are bright-line.
- **Quotas.** The demo runs on Azure Container Apps because the fresh subscription had zero
  App Service dedicated-VM quota; on an established TTB subscription, App Service (or AKS) is
  equally viable and the container is unchanged.
- **Firewall reality.** In TTB's real network the cloud call would be blocked; that's the
  whole point of the offline fallback. Production would either run fully offline (OCR + a
  local model) or call Azure OpenAI from *within* the FedRAMP boundary.
- **Batch state is in-memory.** The async job store lives in the app process — fine for a
  prototype; production swaps it for a durable queue + result store (see
  [docs/ENGINEERING-NOTES.md](docs/ENGINEERING-NOTES.md#scalability)).

---

## Project layout

```
src/LabelVerifier/        ASP.NET Core app
  Models/                 LabelApplication, LabelReading, VerificationResult, Batch (jobs/products)
  Engines/                ILabelReader, Azure OpenAI + Tesseract, fallback orchestrator
  Services/               TextMatching, GovernmentWarning, LabelVerificationService,
                          BatchJobStore, BatchProcessor (bounded parallel)
  Endpoints/              /api/verify, /api/batch/jobs, /api/verify/batch, /api/health
  wwwroot/                the single-page UI
  Dockerfile              (multi-stage; tesseract; non-root)
tests/LabelVerifier.Tests/ xUnit tests for the verification logic
samples/                  generated test labels + manifest.csv + generator script
realtest/                 Puppeteer harness for live testing with real photos
docs/                     real-world findings (with images) + engineering notes
deploy/                   provisioning script
```
