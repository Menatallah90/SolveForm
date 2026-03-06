# SolveForm
### Data-Driven Architectural Form Optimizer for Grasshopper / Rhino 8

SolveForm is a Grasshopper plugin that generates and ranks building massing 
candidates optimized for environmental performance. It combines real climate 
data (EPW files) with designer-defined constraints and a genetic algorithm to 
propose forms shaped by data — not arbitrary aesthetic choices.

> "The building's shape is an argument made by climate and site."

---

## What It Does

Most environmental plugins analyze a form you already designed.  
SolveForm works the other way: **it generates the form from the data.**

You provide:
- Site dimensions and location
- Hard constraints (max height, coverage ratio, WWR limits)
- Objective weights (how much do you care about solar vs. wind?)
- A real EPW weather file for your site

SolveForm returns:
- Ranked massing candidates (Box, L-Shape, Courtyard typologies)
- Solar score per design (facade orientation × glazing × radiation)
- Wind score per design (exposure + cross-ventilation potential)
- A combined weighted score with full performance report

---

## Sample Output (Riyadh, SAU — real EPW data)
```
Rank 1 | Score: 82.7/100  [Courtyard]
  Size:        26.2m W × 24.8m D × 15.3m H
  Orientation: -4.5° from North
  WWR South:   74%   North: 27%
  ☀ Solar:     87.7/100
  💨 Wind:      70.8/100
  Constraints: ✅ All passed

Rank 2 | Score: 81.0/100  [Box]
  Size:        45.0m W × 12.0m D × 13.9m H
  Orientation: 1.5° from North
  WWR South:   75%   North: 28%
  ☀ Solar:     88.6/100
  💨 Wind:      63.3/100
  Constraints: ✅ All passed
```

The optimizer discovered that a **Courtyard typology** outperforms a pure box 
when both solar and wind objectives are weighted equally — independently 
arriving at a form that creates sheltered outdoor space while maintaining 
south facade exposure. The data made an architectural decision.

---

## How It Works
```
EPW File ──→ EpwReader ──→ SiteData (solar + wind)
Designer Inputs ──→ DesignConstraints
                    ↓
           FormGenerator
     (Box / L-Shape / Courtyard)
                    ↓
        GeneticOptimizer
  (Selection → Crossover → Mutation)
  (20 generations × 30 candidates)
                    ↓
   SolarAnalyzer + WindAnalyzer
                    ↓
    Ranked Output → Geometry + Scorecard
```

---

## Components

| Component | Tab | Description |
|-----------|-----|-------------|
| `EPW Loader` | SolveForm / Data | Parses .epw → solar radiation + wind data |
| `SolveForm Solar` | SolveForm / Optimization | Main optimizer |
| `SolveForm Dashboard` | SolveForm / Visualization | Text scorecard |

---

## Inputs — SolveForm Solar

| Input | Default | Description |
|-------|---------|-------------|
| `Lat` | 24.7 | Site latitude |
| `Lon` | 46.7 | Site longitude |
| `SW` | 50m | Site width E-W |
| `SD` | 40m | Site depth N-S |
| `N°` | 0 | True North offset |
| `Hmax` | 24m | Max building height |
| `Cov` | 0.6 | Max site coverage |
| `N` | 30 | Population size |
| `Top` | 3 | Results to return |
| `Sol` | — | Monthly solar from EPW |
| `Wsol` | 0.7 | Solar weight (0–1) |
| `Wwnd` | 0.3 | Wind weight (0–1) |
| `WDir` | -1 | Wind direction override |
| `WDirD` | — | Monthly wind dirs from EPW |
| `WSpdD` | — | Monthly wind speeds from EPW |

---

## Installation

### Requirements
- Rhino 8
- Grasshopper (included with Rhino 8)
- .NET 7.0

### Option A — Build from source
1. Clone this repo
2. Open `SolveForm.sln` in Visual Studio 2022
3. Press F5 — builds and launches Rhino automatically
4. Or: Build → copy `SolveForm.gha` from `bin/Debug/net7.0-windows/`
5. Paste into `%AppData%\Grasshopper\Libraries\`
6. Restart Rhino

### Option B — Direct install
1. Download `SolveForm.gha` from [Releases](../../releases)
2. Copy to `%AppData%\Grasshopper\Libraries\`
3. Restart Rhino → find components under **SolveForm** tab

---

## Getting EPW Files

Download free EPW climate files for any location:
- **[climate.onebuilding.org](https://climate.onebuilding.org)**
- Search your city → download `.epw`
- Wire file path into the `EPW Loader` component

---

## Project Structure
```
SolveForm/
├── Components/
│   ├── SolveFormComponent.cs      # Main GH component
│   ├── EpwLoaderComponent.cs      # EPW parser GH component  
│   └── DashboardComponent.cs      # Scorecard visualization
├── Core/
│   ├── SolarAnalyzer.cs           # Solar scoring engine
│   ├── WindAnalyzer.cs            # Wind scoring engine
│   ├── GeneticOptimizer.cs        # Evolutionary optimizer
│   ├── FormGenerator.cs           # Box / L-Shape / Courtyard generator
│   ├── ConstraintEvaluator.cs     # Hard constraint enforcement
│   └── EpwReader.cs               # EPW climate file parser
├── Models/
│   ├── SiteData.cs                # Site + climate data
│   ├── DesignCandidate.cs         # Massing candidate
│   ├── DesignConstraints.cs       # Designer constraints
│   └── PerformanceScore.cs        # Score results
└── SolveFormInfo.cs               # Plugin metadata
```

---

## Roadmap

### v0.2 — Program + Floors
- [ ] Multi-floor stacked massing with floor-to-floor height input
- [ ] Min/max room area constraints
- [ ] Program mix input (residential / office / retail ratios)

### v0.3 — Adaptive Facades
- [ ] Per-facade adaptive opening sizing based on solar angle
- [ ] Angled fins/shading devices generated from sun path data
- [ ] Opening variation by floor (solar altitude-driven)

### v0.4 — Arbitrary Site Envelope
- [ ] Input any closed Brep as site/zoning envelope
- [ ] Optimizer constrains all candidates to fit within envelope
- [ ] Works on irregular sites, sloped terrain, complex zoning volumes

### v0.5 — Polish
- [ ] Component icons
- [ ] Visual canvas scorecard (GH-native rendering)
- [ ] PDF performance report export
- [ ] Yak package manager release
- [ ] Food4Rhino submission
---

## Philosophy

SolveForm is built on one argument:  
**Form should follow data, not just function.**

The difference between SolveForm and existing tools (Ladybug, Honeybee, 
Octopus, Galapagos) is that those tools analyze or optimize a form you 
already invented. SolveForm invents the form from environmental constraints.

This is closer to how **Frei Otto** worked — form-finding through physical 
forces — than how most parametric architects work (form-first, analysis-second).

---

## Built With
- Rhino 8 + Grasshopper + RhinoCommon
- C# / .NET 7.0
- EPW weather data format by EnergyPlus

## License
MIT — free to use, modify, distribute.
```

---

### Step 4 — Release the .gha
1. GitHub → **Releases → Create a new release**
2. Tag: `v0.1-beta`
3. Title: `SolveForm v0.1-beta`
4. Description:
```
First public beta.
- Solar + wind multi-objective optimization
- Real EPW climate data support
- Genetic algorithm (20 gen × 30 candidates)
- Box, L-Shape, Courtyard typologies
- Tested on Riyadh EPW — courtyard typology emerged as optimal form
