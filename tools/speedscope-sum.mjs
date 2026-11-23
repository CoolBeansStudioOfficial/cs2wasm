// A speedscope profile (dotnet-trace collect --format speedscope) summed: the frames by self time and, with a
// second argument, by inclusive time under frames whose name contains it (each frame once per sample).
// usage: node tools/speedscope-sum.mjs <file.speedscope.json> [under-name] [lines=40] [only names containing]
//   dotnet-trace collect --format speedscope -o t.nettrace -- cs2wasmc.exe program.cs -o out.wasm
import { readFileSync } from 'node:fs';
const [file, under, linesArg, only] = process.argv.slice(2);
const lines = +(linesArg ?? 40);
const p = JSON.parse(readFileSync(file, 'utf8'));
const frames = p.shared.frames.map((f) => f.name);
const self = new Map(), incl = new Map();
let total = 0, counted = 0;
for (const prof of p.profiles) {
  if (prof.type === 'evented') {
    // open/close events with timestamps: the time between consecutive events goes to the frame on top
    const stack = [];
    let last = prof.startValue;
    for (const e of prof.events) {
      const dt = e.at - last;
      if (stack.length && dt > 0) {
        total += dt;
        const inside = !under || stack.some((f) => frames[f].includes(under));
        if (inside) {
          counted += dt;
          const top = stack[stack.length - 1];
          self.set(top, (self.get(top) ?? 0) + dt);
          const seen = new Set();
          let on = !under;
          for (const f of stack) {
            if (!on && frames[f].includes(under)) on = true;
            if (on && !seen.has(f)) { seen.add(f); incl.set(f, (incl.get(f) ?? 0) + dt); }
          }
        }
      }
      last = e.at;
      // (the trace's own leaves, which say only that the thread ran: the time is the frame's below)
      if (/^(CPU_TIME|UNMANAGED_CODE_TIME)$/.test(frames[e.frame])) continue;
      if (e.type === 'O') stack.push(e.frame); else stack.pop();
    }
  } else if (prof.type === 'sampled') {
    prof.samples.forEach((stack, i) => {
      const dt = prof.weights[i];
      total += dt;
      if (under && !stack.some((f) => frames[f].includes(under))) return;
      counted += dt;
      const top = stack[stack.length - 1];
      self.set(top, (self.get(top) ?? 0) + dt);
      const seen = new Set();
      let on = !under;
      for (const f of stack) {
        if (!on && frames[f].includes(under)) on = true;
        if (on && !seen.has(f)) { seen.add(f); incl.set(f, (incl.get(f) ?? 0) + dt); }
      }
    });
  }
}
const show = (m, title) => {
  console.log(`--- ${title}`);
  [...m].filter(([f]) => !only || frames[f].includes(only)).sort((a, b) => b[1] - a[1]).slice(0, lines)
    .forEach(([f, t]) => console.log(`${t.toFixed(0).padStart(8)} ms ${(100 * t / counted).toFixed(1).padStart(5)}%  ${frames[f].slice(0, 170)}`));
};
console.log(`${file}: ${total.toFixed(0)} ms in all threads${under ? `, ${counted.toFixed(0)} ms under "${under}"` : ''}`);
show(self, 'self time');
show(incl, 'inclusive');
