// Drives the LIVE TTB Label Verifier site through a real Chromium (via CDP),
// uploading actual product-label photos and reading the on-screen verdict —
// once with the cloud engine and once with the firewall toggle (offline OCR).
const puppeteer = require("puppeteer-core");

const URL = "https://ttb-label-verifier.happywave-a4140b3f.eastus2.azurecontainerapps.io";

const CASES = [
  { img: "/tmp/realtest/evan_williams.jpg", label: "Evan Williams (2 bottles, angled, script font)",
    fields: { brandName: "Evan Williams", classType: "Kentucky Straight Bourbon Whiskey" } },
  { img: "/tmp/realtest/gentleman_jack.jpg", label: "Gentleman Jack (small, distant, on a counter)",
    fields: { brandName: "Gentleman Jack", classType: "Tennessee Whiskey", alcoholContent: "40%", netContents: "750 mL" } },
  { img: "/tmp/realtest/old_rip_van_winkle.jpg", label: "Old Rip Van Winkle (handheld, dim, serif)",
    fields: { brandName: "Van Winkle", classType: "Kentucky Straight Bourbon Whiskey", alcoholContent: "45.2%", netContents: "750 mL" } },
];

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function runCase(page, c, firewall) {
  await page.goto(URL, { waitUntil: "networkidle2", timeout: 90000 });
  await page.waitForSelector("#singleFile");

  // Toggle firewall (offline OCR) on/off deterministically.
  await page.evaluate(on => {
    const t = document.querySelector("#firewallToggle");
    t.checked = on; t.dispatchEvent(new Event("change", { bubbles: true }));
  }, firewall);

  // Upload the real photo into the hidden file input.
  const input = await page.$("#singleFile");
  await input.uploadFile(c.img);

  // Fill the "application says" fields.
  await page.evaluate(fields => {
    for (const [k, v] of Object.entries(fields)) {
      const el = document.querySelector(`#appForm [name="${k}"]`);
      if (el) el.value = v;
    }
  }, c.fields);

  await page.waitForSelector("#verifyBtn:not([disabled])", { timeout: 10000 });
  await page.click("#verifyBtn");

  await page.waitForSelector("#singleResult .verdict, #singleResult .error-box", { timeout: 120000 });
  await sleep(200);

  return page.evaluate(() => {
    const root = document.querySelector("#singleResult");
    const err = root.querySelector(".error-box");
    if (err) return { error: err.textContent.trim() };
    const verdict = root.querySelector(".verdict");
    const vclass = ["pass", "fail", "review"].find(x => verdict.classList.contains(x));
    const meta = root.querySelector(".verdict .meta")?.textContent.trim() || "";
    const checks = [...root.querySelectorAll(".check")].map(c => {
      const st = ["s-pass", "s-fail", "s-review", "s-notchecked"].find(x => c.classList.contains(x)) || "";
      return {
        name: c.querySelector(".check-name")?.textContent.trim(),
        status: st.replace("s-", ""),
        detail: c.querySelector(".check-detail")?.textContent.trim() || "",
        issues: [...c.querySelectorAll(".issues li")].map(li => li.textContent.trim()),
      };
    });
    return { verdict: vclass, title: verdict.querySelector("h3")?.textContent.trim(), meta, checks };
  });
}

(async () => {
  const browser = await puppeteer.connect({ browserURL: "http://127.0.0.1:9222", defaultViewport: { width: 1280, height: 1600 } });
  const page = await browser.newPage();
  const out = [];
  try {
    for (const c of CASES) {
      for (const firewall of [false, true]) {
        const engine = firewall ? "OCR (firewall)" : "cloud gpt-4o";
        process.stderr.write(`\n>>> ${c.label}  [${engine}]\n`);
        try {
          const r = await runCase(page, c, firewall);
          out.push({ case: c.label, engine, ...r });
          process.stderr.write(`    verdict=${r.verdict || r.error} meta="${r.meta || ""}"\n`);
        } catch (e) {
          out.push({ case: c.label, engine, error: String(e.message || e) });
          process.stderr.write(`    ERROR ${e.message}\n`);
        }
      }
    }
  } finally {
    await page.close();
    await browser.disconnect();
  }
  console.log(JSON.stringify(out, null, 2));
})();
