# Real-world testing harness

[`drive.js`](drive.js) drives the **live** TTB Label Verifier through a real Chromium
browser (via the DevTools protocol / Puppeteer), uploading actual product-label photographs
and reading the on-screen verdict — once with the cloud engine and once with the in-app
**"Simulate agency firewall"** toggle (offline OCR). It's how the *Real-world testing*
section of the top-level [README](../README.md) was produced.

The product photos themselves are **not** committed (they're third-party images from
Wikimedia Commons). To reproduce, download a few real bottle-label photos into
`/tmp/realtest/` (and into the browser's filesystem if it runs in a container), then:

```bash
NODE_PATH=$(npm root -g) node drive.js   # or point NODE_PATH at a local puppeteer-core install
```

`drive.js` connects to a Chromium exposing CDP on `http://127.0.0.1:9222`. Adjust the
`URL` and `CASES` constants at the top of the file for your deployment and images.
