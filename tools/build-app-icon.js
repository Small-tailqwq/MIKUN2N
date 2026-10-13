// Rebuilds Assets/AppIcon.ico from Assets/AppIcon.svg. Keep the vector copy in
// App.xaml (LogoImage) in step when the SVG changes.
// Usage: node tools/build-app-icon.js   (needs the playwright package and a Chromium)
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const root = path.join(__dirname, '..');
const svg = fs.readFileSync(path.join(root, 'Assets', 'AppIcon.svg'), 'utf8');
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();
  const frames = await page.evaluate(async ({ svg, sizes }) => {
    const img = new Image();
    img.src = 'data:image/svg+xml;base64,' + btoa(svg);
    await img.decode();
    const out = [];
    for (const size of sizes) {
      const canvas = document.createElement('canvas');
      canvas.width = canvas.height = size;
      const ctx = canvas.getContext('2d');
      ctx.drawImage(img, 0, 0, size, size);
      out.push({
        size,
        rgba: Array.from(ctx.getImageData(0, 0, size, size).data),
        png: canvas.toDataURL('image/png').split(',')[1]
      });
    }
    return out;
  }, { svg, sizes });
  await browser.close();

  // 256 px is stored as PNG; smaller sizes as 32-bit DIBs, which every Windows
  // icon consumer (including System.Drawing.Icon for the tray) can read.
  const images = frames.map(({ size, rgba, png }) => {
    if (size >= 256) return { size, data: Buffer.from(png, 'base64') };
    const header = Buffer.alloc(40);
    header.writeUInt32LE(40, 0);
    header.writeInt32LE(size, 4);
    header.writeInt32LE(size * 2, 8);
    header.writeUInt16LE(1, 12);
    header.writeUInt16LE(32, 14);
    const pixels = Buffer.alloc(size * size * 4);
    for (let y = 0; y < size; y++) {
      for (let x = 0; x < size; x++) {
        const src = (y * size + x) * 4;
        const dst = ((size - 1 - y) * size + x) * 4;
        pixels[dst] = rgba[src + 2];
        pixels[dst + 1] = rgba[src + 1];
        pixels[dst + 2] = rgba[src];
        pixels[dst + 3] = rgba[src + 3];
      }
    }
    const maskStride = Math.ceil(size / 32) * 4;
    const mask = Buffer.alloc(maskStride * size);
    return { size, data: Buffer.concat([header, pixels, mask]) };
  });

  const dir = Buffer.alloc(6 + 16 * images.length);
  dir.writeUInt16LE(1, 2);
  dir.writeUInt16LE(images.length, 4);
  let offset = dir.length;
  images.forEach(({ size, data }, i) => {
    const e = 6 + 16 * i;
    dir.writeUInt8(size >= 256 ? 0 : size, e);
    dir.writeUInt8(size >= 256 ? 0 : size, e + 1);
    dir.writeUInt16LE(1, e + 4);
    dir.writeUInt16LE(32, e + 6);
    dir.writeUInt32LE(data.length, e + 8);
    dir.writeUInt32LE(offset, e + 12);
    offset += data.length;
  });
  fs.writeFileSync(path.join(root, 'Assets', 'AppIcon.ico'), Buffer.concat([dir, ...images.map(i => i.data)]));
})();
