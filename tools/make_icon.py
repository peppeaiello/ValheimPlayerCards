"""Draws thunderstore/icon.png (256x256): a framed portrait with a horned helmet."""
import sys
from PIL import Image, ImageDraw

out = sys.argv[1] if len(sys.argv) > 1 else "thunderstore/icon.png"
img = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle((8, 8, 248, 248), 28, fill=(38, 30, 24, 255), outline=(201, 162, 39, 255), width=8)
d.rounded_rectangle((44, 40, 212, 222), 18, fill=(70, 56, 44, 255))
d.ellipse((92, 92, 164, 168), fill=(222, 184, 150, 255))            # face
d.pieslice((84, 70, 172, 150), 180, 360, fill=(150, 156, 165, 255))  # helmet
d.rectangle((84, 108, 172, 118), fill=(120, 126, 135, 255))
d.polygon([(86, 104), (56, 62), (70, 58), (98, 96)], fill=(236, 226, 200, 255))   # horns
d.polygon([(170, 104), (200, 62), (186, 58), (158, 96)], fill=(236, 226, 200, 255))
d.polygon([(100, 150), (156, 150), (148, 196), (128, 206), (108, 196)], fill=(176, 104, 40, 255))  # beard
d.rectangle((64, 196, 192, 222), fill=(120, 40, 36, 255))         # cloak
img.save(out)
