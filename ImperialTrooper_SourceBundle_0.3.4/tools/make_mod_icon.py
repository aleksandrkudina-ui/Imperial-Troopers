#!/usr/bin/env python3
"""Generate a 256x256 Thunderstore icon for Imperial Troopers without Unity."""
import math
import sys
from pathlib import Path
from PIL import Image, ImageDraw

OUTPUT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("icon.png")
SCALE = 4
SIZE = 256 * SCALE
image = Image.new("RGBA", (SIZE, SIZE), (9, 14, 25, 255))
pixels = image.load()
for y in range(SIZE):
    for x in range(SIZE):
        dx = (x - SIZE / 2) / (SIZE / 2)
        dy = (y - SIZE / 2) / (SIZE / 2)
        radius = min(1.0, math.sqrt(dx * dx + dy * dy))
        pixels[x, y] = (int(13 + 4 * (1 - radius)), int(23 + 7 * (1 - radius)), int(39 + 17 * (1 - radius)), 255)

draw = ImageDraw.Draw(image, "RGBA")
margin = 15 * SCALE
draw.ellipse((margin, margin, SIZE - margin, SIZE - margin), outline=(160, 29, 35, 255), width=5 * SCALE)
draw.ellipse((margin + 9*SCALE, margin + 9*SCALE, SIZE - margin - 9*SCALE, SIZE - margin - 9*SCALE), outline=(226,64,61,180), width=SCALE)

helmet = [(83,56),(102,43),(128,39),(154,43),(173,57),(185,83),(181,115),(169,138),(155,151),(149,177),(139,190),(117,190),(107,177),(101,152),(87,137),(75,114),(71,83)]
draw.polygon([(x*SCALE,y*SCALE) for x,y in helmet], fill=(225,231,237,255), outline=(255,255,255,255))
draw.polygon([(77*SCALE,81*SCALE),(100*SCALE,73*SCALE),(128*SCALE,77*SCALE),(157*SCALE,73*SCALE),(179*SCALE,82*SCALE),(172*SCALE,101*SCALE),(148*SCALE,107*SCALE),(128*SCALE,103*SCALE),(105*SCALE,107*SCALE),(84*SCALE,99*SCALE)], fill=(7,12,20,255))
for x,y in [(94,86),(161,86)]:
    draw.line((x*SCALE,y*SCALE,(x+12)*SCALE,(y+5)*SCALE), fill=(109,188,224,180), width=2*SCALE)
draw.polygon([(122*SCALE,101*SCALE),(135*SCALE,101*SCALE),(141*SCALE,131*SCALE),(128*SCALE,140*SCALE),(115*SCALE,131*SCALE)], fill=(155,165,177,255), outline=(78,88,103,255))
draw.polygon([(84*SCALE,107*SCALE),(105*SCALE,112*SCALE),(112*SCALE,139*SCALE),(98*SCALE,132*SCALE)], fill=(192,201,211,255), outline=(112,124,138,255))
draw.polygon([(172*SCALE,107*SCALE),(151*SCALE,112*SCALE),(144*SCALE,139*SCALE),(158*SCALE,132*SCALE)], fill=(192,201,211,255), outline=(112,124,138,255))
draw.rounded_rectangle((103*SCALE,139*SCALE,153*SCALE,160*SCALE), radius=4*SCALE, fill=(12,18,27,255), outline=(89,100,114,255), width=SCALE)
for x in range(108,150,6):
    draw.line((x*SCALE,143*SCALE,x*SCALE,156*SCALE), fill=(183,192,202,230), width=2*SCALE)
draw.polygon([(113*SCALE,163*SCALE),(128*SCALE,170*SCALE),(143*SCALE,163*SCALE),(137*SCALE,181*SCALE),(119*SCALE,181*SCALE)], fill=(199,207,216,255), outline=(105,116,129,255))
draw.rounded_rectangle((67*SCALE,89*SCALE,83*SCALE,120*SCALE), radius=4*SCALE, fill=(188,198,209,255), outline=(91,103,119,255), width=2*SCALE)
draw.rounded_rectangle((173*SCALE,89*SCALE,189*SCALE,120*SCALE), radius=4*SCALE, fill=(188,198,209,255), outline=(91,103,119,255), width=2*SCALE)
draw.ellipse((178*SCALE,174*SCALE,213*SCALE,209*SCALE), fill=(176,24,33,255), outline=(250,91,74,255), width=2*SCALE)
cx,cy = 195*SCALE,191*SCALE
for step in range(8):
    angle = math.pi * 2 * step / 8
    draw.line((cx+math.cos(angle)*4*SCALE,cy+math.sin(angle)*4*SCALE,cx+math.cos(angle)*11*SCALE,cy+math.sin(angle)*11*SCALE), fill=(255,240,220,255), width=2*SCALE)
draw.ellipse((191*SCALE,187*SCALE,199*SCALE,195*SCALE), fill=(255,240,220,255))

image = image.resize((256, 256), Image.Resampling.LANCZOS)
OUTPUT.parent.mkdir(parents=True, exist_ok=True)
image.save(OUTPUT, format="PNG", optimize=True)
print("Generated Thunderstore icon: " + str(OUTPUT) + " (256x256)")
