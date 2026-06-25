# SolveForm v0.2-beta

A Grasshopper plugin for Rhino 8 that generates solar-optimized building massing with automatic facade articulation and window placement.

---

## Pipeline

```
Solar → Section → Unify → Normalize → Facades → Openings
                        ↘
                         Floors
```

| Component | Description |
|---|---|
| **Solar** | Reads EPW climate file, computes solar exposure by orientation |
| **Section** | Generates stepped massing from a profile curve, solar-derived heights |
| **Floors** | Derives floor slabs and Z levels from Section output |
| **Unify** | Boolean-unions all zone masses into one closed polysurface |
| **Normalize** | Corrects scrambled face normals from Boolean union |
| **Facades** | Tags exterior faces by orientation (N/S/E/W), outputs face geometry |
| **Openings** | Arrays windows across all exterior faces, one row per floor band |

---

## Installation

1. Build the solution in Visual Studio 2022 (`Release` config, targeting Rhino 8)
2. Copy the compiled `.gha` file to your Grasshopper Libraries folder:
   - Windows: `%AppData%\Grasshopper\Libraries`
3. Unblock the `.gha` file (right-click → Properties → Unblock)
4. Restart Rhino and Grasshopper
5. SolveForm components appear under the **SolveForm** tab

---

## Wiring

```
EPW File         → Solar.EPW
Solar.NorthAngle → Section.NorthAngle
Solar.Heights    → Section.RequiredHeights

Section.Massing     → Unify.Masses
Section.ZoneHeights → Floors.ZoneHeights
Section.Profiles    → Floors.Profiles

Unify.Unified    → Normalize.Brep
Normalize.Fixed  → Facades.Unified
Normalize.Fixed  → Openings.UnifiedBrep

Floors.ZLevels   → Openings.FloorHeights
```

---

## Openings Component Inputs

| Input | Description | Default |
|---|---|---|
| UnifiedBrep | Mass brep from Normalize | — |
| FloorHeights | Z levels from Floors | — |
| WindowWidth | Width per window | 1.0 |
| WindowHeight | Height per window | 2.4 |
| WindowSpacing | Center-to-center spacing | 1.8 |
| SillHeight | Floor to window bottom | 0.9 |

---

## Known Limitations (v0.2)

- **Stepping artifact:** Section generates zones with up to 20% N-offset per level. Floor band Z levels occasionally misalign with step geometry, causing top-floor windows to appear slightly tall or offset. Fix scheduled for v0.3.
- **No solar-driven WWR:** Openings places equal windows on all orientations. Solar optimization of window-to-wall ratio by facade orientation is Step 2, scheduled as a separate `SolveFormSolarOpenings` component in v0.3.
- **No wall thickness / boolean cut:** CutOpenings component is in development. For renders, use the flat window rectangles from Openings as surface cutters manually in Rhino.
- **Section massing:** Profile extrusion logic is a placeholder. Genuine setback/cruciform footprint tapering is v0.3+.

---

## Roadmap

| Version | Features |
|---|---|
| v0.1 | EPW reader, solar/wind analysis, genetic optimizer, 3 typologies |
| v0.2 | Normalize, Facades, Openings — full facade articulation pipeline |
| v0.3 | Solar-driven WWR, Section massing redesign, CutOpenings boolean |
| v0.4 | Arbitrary Brep site envelope input |
| v0.5 | Food4Rhino submission, UI polish |

---

## Requirements

- Rhino 8
- Grasshopper (built-in)
- Visual Studio 2022 (.NET Framework 4.8)

---

## Author

Menatallah Abdulrhman — github.com/Menatallah90
