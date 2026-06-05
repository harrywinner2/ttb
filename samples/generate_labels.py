#!/usr/bin/env python3
"""
Generate a set of sample alcohol-beverage labels for testing the verifier.

Each label is paired with a row in manifest.csv describing what the matching
"application" claims. The set deliberately mixes fully-compliant labels with
realistic violations (title-case warning, missing warning, wrong ABV, brand
case mismatch) so every code path in the verifier can be exercised.

Usage:  python3 generate_labels.py
Output: *.png  +  manifest.csv  in this directory.
"""
import csv
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
FONT_DIR = "/usr/share/fonts/truetype/dejavu"

# The exact statutory warning (27 CFR 16.21).
WARNING_BODY = (
    "(1) According to the Surgeon General, women should not drink alcoholic "
    "beverages during pregnancy because of the risk of birth defects. "
    "(2) Consumption of alcoholic beverages impairs your ability to drive a "
    "car or operate machinery, and may cause health problems."
)


def font(name, size):
    return ImageFont.truetype(os.path.join(FONT_DIR, name), size)


def wrap(draw, text, fnt, max_w):
    words, lines, cur = text.split(), [], ""
    for w in words:
        trial = (cur + " " + w).strip()
        if draw.textlength(trial, font=fnt) <= max_w:
            cur = trial
        else:
            if cur:
                lines.append(cur)
            cur = w
    if cur:
        lines.append(cur)
    return lines


def draw_warning(draw, x, y, max_w, heading="GOVERNMENT WARNING:", include=True):
    """Render the warning. heading lets us simulate the title-case violation."""
    if not include:
        return
    head_font = font("DejaVuSans-Bold.ttf", 15)   # bold heading (compliant labels)
    body_font = font("DejaVuSans.ttf", 14)         # non-bold body
    draw.text((x, y), heading, font=head_font, fill="black")
    # Heading on its own line, statutory body wrapped beneath it.
    body_lines = wrap(draw, WARNING_BODY, body_font, max_w)
    yy = y + 22
    for ln in body_lines:
        draw.text((x, yy), ln, font=body_font, fill="black")
        yy += 18
    return yy


def base_label(bg, accent):
    img = Image.new("RGB", (640, 900), bg)
    d = ImageDraw.Draw(img)
    d.rectangle([18, 18, 622, 882], outline=accent, width=4)
    d.rectangle([30, 30, 610, 870], outline=accent, width=1)
    return img, d


def spirits_label(path, brand, klass, abv_text, net, bottler, *,
                  warning_heading="GOVERNMENT WARNING:", include_warning=True,
                  bg="#f3ead6", accent="#6b4a1f", ink="#2a1d0e"):
    img, d = base_label(bg, accent)
    cx = 320
    d.text((cx, 90), "EST. 1921", font=font("DejaVuSerif.ttf", 16), fill=accent, anchor="mm")
    # Brand (may be multi-line)
    bf = font("DejaVuSerif-Bold.ttf", 46)
    for i, ln in enumerate(brand.split("\n")):
        d.text((cx, 150 + i * 52), ln, font=bf, fill=ink, anchor="mm")
    d.line([120, 250, 520, 250], fill=accent, width=2)
    d.text((cx, 285), klass, font=font("DejaVuSerif.ttf", 22), fill=ink, anchor="mm")
    d.text((cx, 470), abv_text, font=font("DejaVuSans-Bold.ttf", 24), fill=ink, anchor="mm")
    d.text((cx, 510), net, font=font("DejaVuSans.ttf", 20), fill=ink, anchor="mm")
    d.text((cx, 690), bottler, font=font("DejaVuSans.ttf", 14), fill=ink, anchor="mm")
    draw_warning(d, 60, 740, 520, heading=warning_heading, include=include_warning)
    img.save(path, "PNG")
    print("wrote", os.path.basename(path))


def wine_label(path, brand, klass, abv_text, net, bottler, *,
               include_warning=True, warning_heading="GOVERNMENT WARNING:"):
    img, d = base_label("#f7f4ef", "#5a1230")
    cx = 320
    d.text((cx, 120), brand, font=font("DejaVuSerif-Bold.ttf", 40), fill="#3a0c1f", anchor="mm")
    d.text((cx, 175), "NAPA VALLEY", font=font("DejaVuSerif.ttf", 18), fill="#5a1230", anchor="mm")
    d.line([150, 220, 490, 220], fill="#5a1230", width=1)
    d.text((cx, 300), klass, font=font("DejaVuSerif-Italic.ttf", 24), fill="#3a0c1f", anchor="mm")
    d.text((cx, 470), abv_text, font=font("DejaVuSans-Bold.ttf", 22), fill="#3a0c1f", anchor="mm")
    d.text((cx, 508), net, font=font("DejaVuSans.ttf", 20), fill="#3a0c1f", anchor="mm")
    d.text((cx, 690), bottler, font=font("DejaVuSans.ttf", 14), fill="#3a0c1f", anchor="mm")
    draw_warning(d, 60, 740, 520, heading=warning_heading, include=include_warning)
    img.save(path, "PNG")
    print("wrote", os.path.basename(path))


def main():
    rows = []

    # 1. Fully compliant bourbon — the spec's example label.
    p = os.path.join(HERE, "01_old_tom_bourbon_ok.png")
    spirits_label(p, "OLD TOM\nDISTILLERY", "Kentucky Straight Bourbon Whiskey",
                  "45% Alc./Vol. (90 Proof)", "750 mL",
                  "Bottled by Old Tom Distillery, Bardstown, KY")
    rows.append(("01_old_tom_bourbon_ok.png", "Old Tom Distillery",
                 "Kentucky Straight Bourbon Whiskey", "45% Alc./Vol.", "750 mL",
                 "Old Tom Distillery, Bardstown, KY", ""))

    # 2. Brand case differs (STONE'S THROW vs Stone's Throw) — should still PASS (fuzzy).
    p = os.path.join(HERE, "02_stones_throw_gin_casediff.png")
    spirits_label(p, "STONE'S THROW", "London Dry Gin",
                  "47% Alc./Vol. (94 Proof)", "750 mL",
                  "Distilled & bottled by Stone's Throw Spirits, Portland, OR",
                  bg="#eef2ef", accent="#234", ink="#10202a")
    rows.append(("02_stones_throw_gin_casediff.png", "Stone's Throw",
                 "London Dry Gin", "47%", "750 mL", "Stone's Throw Spirits, Portland, OR", ""))

    # 3. Warning heading in Title Case — should FAIL (must be ALL CAPS).
    p = os.path.join(HERE, "03_titlecase_warning_fail.png")
    spirits_label(p, "IRON RIDGE", "Straight Rye Whiskey",
                  "50% Alc./Vol. (100 Proof)", "750 mL",
                  "Bottled by Iron Ridge Distilling Co., Louisville, KY",
                  warning_heading="Government Warning:",
                  bg="#efe7df", accent="#444", ink="#222")
    rows.append(("03_titlecase_warning_fail.png", "Iron Ridge",
                 "Straight Rye Whiskey", "50%", "750 mL", "Iron Ridge Distilling Co., Louisville, KY", ""))

    # 4. Missing warning entirely — should FAIL.
    p = os.path.join(HERE, "04_missing_warning_fail.png")
    spirits_label(p, "BLUE HERON", "Silver Tequila",
                  "40% Alc./Vol. (80 Proof)", "750 mL",
                  "Imported by Blue Heron Imports, San Diego, CA",
                  include_warning=False, bg="#eaf1f4", accent="#1d5a78", ink="#0c2c3a")
    rows.append(("04_missing_warning_fail.png", "Blue Heron",
                 "Silver Tequila", "40%", "750 mL", "Blue Heron Imports, San Diego, CA", "Product of Mexico"))

    # 5. ABV mismatch — application says 43%, label shows 40% — should FAIL.
    p = os.path.join(HERE, "05_abv_mismatch_fail.png")
    spirits_label(p, "CEDAR CREEK", "Single Malt Whisky",
                  "40% Alc./Vol. (80 Proof)", "700 mL",
                  "Distilled in Scotland for Cedar Creek Ltd.",
                  bg="#f1ebe0", accent="#3a2a14", ink="#241a0c")
    rows.append(("05_abv_mismatch_fail.png", "Cedar Creek",
                 "Single Malt Whisky", "43% Alc./Vol.", "700 mL", "Cedar Creek Ltd.", "Product of Scotland"))

    # 6. Compliant wine.
    p = os.path.join(HERE, "06_red_oak_wine_ok.png")
    wine_label(p, "RED OAK CELLARS", "Cabernet Sauvignon",
               "13.5% Alc./Vol.", "750 mL", "Produced & bottled by Red Oak Cellars, Napa, CA")
    rows.append(("06_red_oak_wine_ok.png", "Red Oak Cellars",
                 "Cabernet Sauvignon", "13.5%", "750 mL", "Red Oak Cellars, Napa, CA", ""))

    with open(os.path.join(HERE, "manifest.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["filename", "brand_name", "class_type", "alcohol_content",
                    "net_contents", "bottler", "country"])
        w.writerows(rows)
    print("wrote manifest.csv")


if __name__ == "__main__":
    main()
