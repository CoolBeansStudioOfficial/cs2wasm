# Third-party notices

cs2wasm compiles other projects' sources into the modules it produces, and keeps changed copies of some of their files
in this repository (`src/Cs2Wasm/Upstream`). Their licences apply to those files and to what is built from them.

| What | Where it is used | Licence |
|---|---|---|
| .NET runtime (`dotnet/runtime`, tag `v11.0.0`), the .NET Foundation and contributors | The library every module is built from: fetched at build time (the files the `manifest.txt` of each library lists); changed copies of some files under `Upstream/*/Runtime`, generated ones under `Upstream/*/Generated` | MIT |
| Roslyn (`dotnet/roslyn`, the commit `Compiler/Library.cs` names), the .NET Foundation and contributors | The compiler's front end (the NuGet package), and its sources for the self-hosted compiler | MIT |
| Godot Engine (`godotengine/godot`, commit `ed1daf0bf001b61586d9930840f2f1394092c079`), Juan Linietsky, Ariel Manzur and the Godot Engine contributors | GodotSharp, fetched at build time; generated glue under `Upstream/GodotSharp` | MIT |
| FreeBSD msun / fdlibm, Sun Microsystems, Inc. | `Upstream/System.Private.CoreLib/Boundary/CMath.cs`, a port to C# | See below |

The MIT licence's text is in `LICENSE`; each project's own copyright notice applies to its files.

fdlibm:

    Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.

    Developed at SunPro, a Sun Microsystems, Inc. business.
    Permission to use, copy, modify, and distribute this
    software is freely granted, provided that this notice
    is preserved.
