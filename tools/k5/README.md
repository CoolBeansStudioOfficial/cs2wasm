Stream K round 5 diagnosis scripts (scratch tools, kept as a record; paths in round.sh are this machine's).

- round.sh: the locked rounds (run through tests/out/heavy.sh). Steps: build:<name>[:ENV=..+ENV=..] (self-host build
  from k/m-src with k/cli-<name>), time:<n>:<a,b> (interleaved timings), ftime:<n>:<name>:<flags;flags> (V8 flag sweep,
  self-host and NativeAOT-LLVM), tinl:<name> (--trace-wasm-inlining, both), suite:<ENV>, bench:<ENV>, quiet.
- tinl.mjs trace.txt named.wasm: TurboFan inlining decisions by callee (refused for budget, inlined).
- generics.mjs named.wasm: code of generic instantiations by family; what one body per family would save.
- dups.mjs a.wasm: byte-identical bodies, and identical up to calls of identical functions.
- fnlist.mjs a.wasm: sections and per-function body sizes (diff two modules).
- lend.cs: exceptions thrown through 35 library paths that call back into user code; its output must be the same
  with IL2WASM_SHARE=0 and the default (it exercises a lending shared body).
