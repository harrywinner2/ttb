# Engineering notes

Technical detail on three areas a reviewer is likely to probe: the limits of the offline OCR
engine (and how to lift them), and how the architecture handles **security** and
**scalability**. Items are split into what the prototype **implements** and what is
**recommended** for a production system.

---

## Offline OCR: why it fails on photographs

### Current behaviour
The offline engine invokes the Tesseract CLI over the **entire uploaded image** (`--psm 3`),
with **no label localisation and no preprocessing**.

### The core problem: no region-of-interest selection
Running OCR on the whole photo is a poor fit for real product shots, where the label is a
small, angled, often glossy region inside a larger scene. A controlled experiment
([`realtest/`](../realtest/)) on the test photographs makes the failure modes precise:

| Input to Tesseract | Result |
|---|---|
| Gentleman Jack — whole image | noise only |
| Gentleman Jack — cropped to the label | still noise (low-contrast **embossed silver** lettering) |
| Old Rip Van Winkle — whole image | mostly noise |
| Old Rip Van Winkle — cropped to the label | recovers real words ("Kentucky", "Special Reserve") |
| Either — crop + naïve global threshold | no improvement, sometimes worse |

Two conclusions follow. First, the **absence of cropping is a major factor** — isolating the
label recovers readable text on a moderate-contrast label. Second, **cropping alone is not
sufficient**: low-contrast or embossed lettering defeats Tesseract regardless, and a single
global threshold is the wrong preprocessing tool (it destroys low-contrast detail).

### Recommended pipeline (fully offline, no outbound traffic)
1. **Region-of-interest localisation** — a lightweight detector (YOLO-class, trained on
   bottles/labels) or classical CV (largest bright quadrilateral, MSER/EAST text regions) to
   crop the label before recognition.
2. **Geometric correction** — deskew, perspective-correct, and unwrap cylindrical labels.
3. **Adaptive preprocessing** — grayscale, CLAHE, **adaptive/Sauvola** thresholding (not a
   global cutoff), and upscaling of small text.
4. **A scene-text OCR engine** — PaddleOCR, EasyOCR, or docTR in place of Tesseract; these are
   built for angled, stylised, low-contrast text and outperform Tesseract on photographs.
5. **A local vision-language model** inside the FedRAMP boundary — near-cloud accuracy with
   zero egress; the production-grade offline engine.

Each option is a drop-in behind the **`ILabelReader`** interface, with no change to the
matching logic, warning checks, or UI.

### Context: the actual TTB input
Tesseract performs well on clean, high-contrast artwork — in testing it transcribed the full
Government Warning **verbatim** from generated label art, and matched the cloud engine on that
input. COLA stores print-ready artwork (vector/PDF proofs), not phone snapshots, so the
offline engine is already adequate for the system's primary input; the photograph case above
is the degraded fallback that the upgrades target.

> Bold-type detection remains advisory because neither OCR nor a vision model judges stroke
> weight reliably. A localisation step would also enable stroke-width analysis on the cropped
> heading, allowing the "bold `GOVERNMENT WARNING`" rule to be enforced rather than flagged.

---

## Security

### Implemented in the prototype
- **Secrets isolated.** The Azure OpenAI key is held as a Container App secret (`secretref:`),
  never baked into the image or committed. `.env*`, the specification PDF, and third-party test
  images are gitignored; the working tree is checked for secrets before each commit.
- **No data at rest.** Images are processed in memory and discarded — no database, blob
  storage, or logging of image content.
- **HTTP security headers** on every response: `Content-Security-Policy`,
  `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy`, and
  `Strict-Transport-Security`.
- **Per-IP rate limiting** (fixed window) in front of the costly AI endpoint, partitioned by
  the forwarded client IP (`X-Forwarded-For` honoured behind the Container Apps ingress).
- **Least privilege.** The container runs as a non-root user (UID 1654).
- **Input validation.** Content-type allowlist, 25 MB per-file cap, multipart/value limits;
  Azure OpenAI content filters active.

### Recommended for production
- **Microsoft Entra ID** authentication with role-based access for agents.
- **Managed identity** for both Azure OpenAI and the container registry — eliminating the API
  key and registry admin credentials entirely.
- **Private Endpoint + VNet integration** so model traffic never leaves the FedRAMP boundary;
  this is the production answer to the firewall constraint.
- **Immutable audit log** of every determination, for the compliance record.
- **Azure Front Door + WAF**, per-user quotas, **Key Vault**, and formal PII/retention
  governance.

---

## Scalability

### Implemented in the prototype
- **Stateless service.** Azure Container Apps autoscales **0→3** replicas on HTTP concurrency
  (KEDA); scale-to-zero keeps idle cost near zero.
- **Asynchronous, parallel batch.** `POST /api/batch/jobs` returns a job id immediately;
  products are processed by a **bounded parallel worker** (default degree 8, configurable via
  `Batch:MaxParallelism`), and the client **polls** for incremental results. This decouples
  throughput from any single request's lifetime — directly addressing the earlier failure where
  a large synchronous batch exceeded the client timeout on a cold start.
- **Multiple images per product** (up to four, any sides/angles) are read and **merged** into a
  single verification, so the brand can come from one image while the Government Warning comes
  from another.

### Recommended for production scale
- **Durable queue + autoscaling workers.** Replace the in-memory job store with Azure Storage
  Queue / Service Bus and KEDA queue-length scaling, persisting results to storage. This
  absorbs the bursty "200–300 at once" pattern (≈150k applications/year, submitted unevenly).
- **Model throughput.** Raise quota, adopt **Provisioned Throughput Units** for predictable peak
  latency, and/or distribute across deployments/regions. Current deployment: `gpt-4o` at
  **30K TPM (Standard)**.
- **Latency.** Client-side image downscaling before upload reduces both latency and vision-token
  cost (cost scales with image size); 5–10s was observed on full-resolution photographs.
- **SLA.** Production keeps min-replicas ≥ 1 (or pre-warms) to hold the ~5s target.

### The enabling property
Two seams carry the design: **`ILabelReader`** (swap the engine without touching anything
downstream) and **pure, stateless verification** (parallelises trivially and is the part
covered by unit tests).
