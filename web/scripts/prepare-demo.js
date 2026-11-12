// Copies the seed documents next to the UI so the in-browser demo backend can load them (run before `ng build` for GitHub Pages).
const fs = require('fs'), path = require('path');
const src = path.join(__dirname, '..', '..', 'data', 'docs'), dst = path.join(__dirname, '..', 'public', 'docs');
fs.mkdirSync(dst, { recursive: true });
const files = fs.readdirSync(src).filter(f => f.endsWith('.md')).sort();
files.forEach(f => fs.copyFileSync(path.join(src, f), path.join(dst, f)));
fs.writeFileSync(path.join(dst, 'manifest.json'), JSON.stringify(files));
console.log('demo docs prepared:', files.length);
