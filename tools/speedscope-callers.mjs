// Who calls a frame, in a speedscope profile (dotnet-trace collect --format speedscope): the time with a frame whose
// name contains <name> on top of the stack (the trace's CPU_TIME leaves aside), by the `depth` frames below it.
// usage: node tools/speedscope-callers.mjs <file.speedscope.json> <name> [depth=3] [lines=25] [skip names containing, comma separated]
import { readFileSync } from 'node:fs';
const [file, want, depthArg, linesArg, skipArg] = process.argv.slice(2);
const depth = +(depthArg ?? 3), lines = +(linesArg ?? 25);
const skip = (skipArg ?? '').split(',').filter(Boolean);
const p = JSON.parse(readFileSync(file, 'utf8'));
const frames = p.shared.frames.map((f) => f.name);
const short = (n) => n.replace(/^[^!]*!/, '').replace(/\(.*$/, '').slice(0, 110);
const by = new Map();
let total = 0;
for (const prof of p.profiles) {
  if (prof.type !== 'evented') continue;
  const stack = [];
  let last = prof.startValue;
  for (const e of prof.events) {
    const dt = e.at - last;
    last = e.at;
    if (stack.length && dt > 0 && frames[stack[stack.length - 1]].includes(want)) {
      total += dt;
      // the frames below the topmost run of matching (and skipped) frames
      let i = stack.length - 1;
      while (i >= 0 && (frames[stack[i]].includes(want) || skip.some((s) => frames[stack[i]].includes(s)))) i--;
      const key = [];
      for (let k = i; k >= 0 && key.length < depth; k--) key.push(short(frames[stack[k]]));
      const ks = key.join(' < ');
      by.set(ks, (by.get(ks) ?? 0) + dt);
    }
    if (/^(CPU_TIME|UNMANAGED_CODE_TIME)$/.test(frames[e.frame])) continue;
    if (e.type === 'O') stack.push(e.frame); else stack.pop();
  }
}
console.log(`"${want}" on top: ${total.toFixed(0)} ms`);
[...by].sort((a, b) => b[1] - a[1]).slice(0, lines).forEach(([k, t]) => console.log(`${t.toFixed(0).padStart(8)} ms ${(100 * t / total).toFixed(1).padStart(5)}%  ${k}`));
