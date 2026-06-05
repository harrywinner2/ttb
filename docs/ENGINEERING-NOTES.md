# Engineering notes

Deeper notes on three things an evaluator will rightly poke at: why the offline OCR fails so
badly (and how to fix it), and how the architecture handles **security** and **scalability**.

---

## Why the offline OCR fails so badly

### What we do today
The offline engine shells out to the **Tesseract** CLI over the **entire uploaded image** at
`--psm 3` (automatic page segmentation). There is **no label localisation, no cropping, and no
image preprocessing**.

### The cropping question — answer: no, we were *not* cropping the right region
We OCR the **whole photo**. On a real picture where the label is a small, angled, glossy patch
in a large scene — the Gentleman Jack shot, where the label is ~5% of the frame — Tesseract
(which is built for flat, high-contrast *document scans*) has almost nothing to lock onto:
counter, reflections, the metal tray, background. The zoom crop on the
[findings page](REAL-WORLD-FINDINGS.md#2-gentleman-jack--small-label-shot-across-a-counter)
proves the text **is** legible — the engine just never saw it isolated. So a large part of the
failure is **our pipeline, not only the OCR engine**.

### How to make the offline path degrade gracefully (cheapest first)
1. **Label localisation (ROI crop).** Find the label/bottle and crop to it *before* OCR. Either
   a lightweight detector (a YOLO-class model trained on bottles/labels) or classical CV
   (largest bright quadrilateral, or MSER/east text-region detection). Even a rough crop removes
   most of the noise — this alone would have rescued the Gentleman Jack case.
2. **Geometric correction.** Deskew and perspective-correct; for cylindrical bottles, *label
   unwrapping* (dewarp) to flatten curved text.
3. **Image cleanup.** Grayscale → denoise → CLAHE/contrast → adaptive/Otsu threshold → upscale
   small text. Tesseract accuracy is extremely sensitive to this.
4. **A modern OCR engine instead of Tesseract.** **PaddleOCR**, **EasyOCR**, or **docTR** are
   deep-learning detect-then-recognise pipelines built for *scene text* (angled, stylised,
   low-contrast). They run fully offline and dramatically outperform Tesseract on photographs.
5. **The real on-prem answer: a local vision-language model.** A quantised small VLM
   (Qwen2-VL / Llama-3.2-Vision class) running **inside the FedRAMP boundary** gives near-cloud
   quality with **zero outbound traffic** — the true firewall-compatible equivalent of `gpt-4o`.
   Heavier (needs a GPU), but it's the production-grade offline engine.

**Recommended order for TTB:** (a) add ROI-crop + preprocessing now (big win, low cost);
(b) swap Tesseract → PaddleOCR for the no-GPU offline tier; (c) stand up a local VLM within the
Azure boundary as the high-accuracy offline engine. Thanks to the **`ILabelReader` seam**, each
is a drop-in replacement — **no change to the matching logic, warning checks, or UI**.

> Side benefit: a localisation step also unlocks **real bold detection**. Today "is the
> `GOVERNMENT WARNING` heading bold?" is advisory because neither OCR nor the VLM judges type
> weight reliably; stroke-width analysis on the *cropped heading* could enforce it properly.

---

## Security

### In place (appropriate for a prototype)
- **Secrets out of code and git.** The Azure OpenAI key is a **Container App secret** referenced
  via `secretref:` — never baked into the image or committed. `.env*` is gitignored, as are the
  proprietary spec PDF and the third-party test images. The working tree is grep-checked for
  secrets before every push.
- **No data at rest.** Images are processed **in memory and discarded** — no database, no blob
  storage, no logging of image content. This matches IT's "don't store anything sensitive"
  guidance and minimises PII/retention exposure.
- **Transport security.** HTTPS-only via the Container Apps managed certificate.
- **Input hardening.** Content-type allowlist (JPG/PNG/WEBP…), 25 MB per-file cap, batch-count
  and multipart limits, so a malicious upload can't trivially exhaust the host.
- **Model-side safety.** Azure OpenAI content filters are active (we had to phrase prompts to
  avoid the prompt-shield's *jailbreak* false-positive — documented in the commit history).

### Gaps and the production hardening path (stated honestly)
- **AuthN/Z.** Prototype endpoints are public. Production: **Microsoft Entra ID** in front of the
  app (the org is already on Azure AD), with role-based access for agents.
- **Eliminate the key entirely.** Give the Container App a **managed identity** and call Azure
  OpenAI with Entra tokens — no API key to leak. Same for ACR (managed-identity pull instead of
  the admin user we used for speed).
- **Keep the call inside the boundary.** **Private Endpoint + VNet integration** so traffic to
  Azure OpenAI never crosses the public internet. *This* — not the OCR fallback — is the real
  production answer to the firewall.
- **Audit trail.** Every determination logged immutably (who / what / when / result) as a
  compliance record. The prototype is stateless by design; production needs this.
- **Edge protection.** Azure Front Door + WAF, per-user rate limiting/quota to prevent
  cost-abuse of an AI endpoint, and security headers (CSP, HSTS) on the static UI.
- **Data governance.** Formal PII handling + retention alignment, FedRAMP controls, Key Vault for
  any residual secrets.

---

## Scalability

### Today
- **Stateless app → free horizontal scale.** Azure Container Apps autoscales **0→3** replicas on
  HTTP concurrency (KEDA). We set **min-replicas 0** for a cost-free idle demo (~10s cold start).
- **Bounded batch concurrency** (`SemaphoreSlim` of 6) so a 300-label batch doesn't blow the
  model's rate limit.

### Where it breaks at TTB scale — and the fix
- **The workload is bursty.** ~150k applications/year ≈ ~600 per business day on average, but the
  real pattern is "an importer dumps 200–300 at once" (Janet, Seattle). You must absorb bursts,
  not just the average.
- **The batch bottleneck is the request lifetime.** Today one HTTP request holds all 300 images
  open while they process — we literally hit a 90s client timeout on a cold start. The right
  design is **asynchronous**:

  ```
  upload → enqueue (Azure Storage Queue / Service Bus)
        → worker pool (Container Apps scaling on queue length via KEDA)
        → results to storage, polled/streamed back to the UI
  ```

  This decouples throughput from the request and lets workers scale elastically to swallow a
  burst.
- **Model throughput is the true ceiling.** We deployed `gpt-4o` at **30K TPM (Standard)**. Scale
  via higher quota, **Provisioned Throughput Units (PTUs)** for predictable peak latency, and/or
  multiple deployments/regions behind a load balancer. The work is embarrassingly parallel and
  per-label cost is a few cents, so cost scales roughly linearly with volume.
- **Latency lever.** 5–10s per full-resolution photo today. **Client-side downscaling** before
  upload (a label doesn't need 12 MP) cuts both latency and token cost — vision tokens scale with
  image size — and keeps us under the ~5s bar.
- **Cold start vs cost.** Scale-to-zero is right for an evaluator demo; production keeps
  **min-replicas ≥ 1** (or pre-warms) to hold the ~5s SLA.

### The property that makes all of this cheap
Two seams do the heavy lifting:
- **`ILabelReader`** — swap the engine (cloud VLM ↔ on-prem VLM ↔ OCR pipeline) without touching
  matching or UI.
- **Pure, stateless verification** — the verdict logic is pure functions over a reading, so it
  parallelises trivially and is exactly the part covered by unit tests.
