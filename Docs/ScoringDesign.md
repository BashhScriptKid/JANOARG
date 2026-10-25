# Scoring Displays & Character Skills — Design

## 0. Ground truth (never changes)

- `TotalExScore`: fixed at chart load (`PlayerScreen.cs:931`). Per head:
  `3|1 (Normal|Catch) + ceil(Hold/0.5) + flick?1 + dirFlick?1`. Each hold tick = 1.
- `CurrentExScore`: scaled gains. Head: `base * (1 - |acc|)`; hold tick: 1;
  flick/catch: binary vs `PassWindow`.
- Base display (Pessimistic): `round(Current / Total * 1e6)`, capped 1M.
- `PASSING_SCORE = 800000` (`Helper.cs:10`): clear bar, A-rank floor, rating knee.
- Rank / rating / store / clear read **base** — never a display value —
  except Cheerleader-line skills, which smuggle explicitly and are tagged.

## 1. New state (all in `PlayerScreen`, updated in `AddScore`)

| Field | Update | Used by |
|---|---|---|
| `consumedMax` | `+= baseMax` per head, `+= 1` per tick | Optimistic, Volatile, Ghost |
| `dropped` | `+= baseMax - gained` | Optimistic |
| `constantGained` | §3 | Misaligned-constant |
| `favouredGained` | §4 | Misaligned-favoured |
| `strictGained` | `+= acc==0 ? base : 0` (binary hits as now) | Strict |
| `momentum` | `mom = mom*0.98 + gainedRatio*0.02` per hit | Momentum |
| `osuDisplay`, `apeCombo`, `apeMiss`, `apeM` | §7 | Rocket Ape |
| `wmcRun / wJ` | §6 | Praiser weights |

`AddScore()` must receive `baseMax` (currently only gets scaled `finalScore`).
Hold-tick call sites (`PlayerInputManager.cs:1424,1543`) pass `baseMax = 1`.
`consumedMax == 0` guard for Volatile.

`ScrollingCounter` needs more digits (Ape hits 8+); current `SetNumber`
shows leading digits on overflow (`UI/ScrollingCounter.cs:11`).

## 2. Displays (HUD only, no gameplay effect)

- **Pessimistic** (default): `Current / Total * 1e6`. Floor, miss-all-rest.
- **Optimistic**: `(Total - dropped) / Total * 1e6`. Ceiling, FC-the-rest.
- **Volatile**: `Current / consumedMax * 1e6`. Realtime acc%, swings early.
- **Ghost**: `Current - pbScore/1e6 * consumedMax`, pb from
  `ScoreStore.Get()` at start. +ahead / -behind; settles at record margin.
- **Strict**: `strictGained / Total * 1e6`. Goods pay 0, run continues.
- **Misaligned-constant**: any Good pays `base * 0.5`, offset ignored
  (§3 accumulator).
- **Misaligned-favoured**: Good pays `base * Lerp(0.7, 0.4, smoothstep(t))`,
  `t = InverseLerp(PerfectWindow, GoodWindow, |offset|)` (§4).
- **Momentum**: decayed accuracy, recent notes weigh more.
- **Perfectionist**: boolean latch, dead on first `|acc| > 0` or miss.
  (Lamp, not a number. Renamed off Arcaea's "PM".)
- Dropped: Rolling-50 (overlap with Volatile), Early/Late bias meter
  (per-hit EARLY/LATE + results median already cover it).

## 3. Misaligned-constant

```csharp
constantGained += acc == null ? score      // flick/catch/ticks, already binary
  : acc == 0 ? baseMax
  : Math.Abs(acc.Value) < 1 ? baseMax * 0.5f
  : 0;
display = constantGained / Total * 1e6;
```

Compresses the Good band; borderline-Goods gain vs linear, barely-Goods lose.

## 4. Misaligned-favoured

```csharp
float t = Mathf.InverseLerp(PerfectWindow, GoodWindow, offsetAbs); // 0 bare, 1 borderline
float eased = t * t * (3 - 2 * t);          // or 0.5 - 0.5*cos(pi*t) for bell shoulders
favouredGained += baseMax * Mathf.Lerp(0.7f, 0.4f, eased);  // Perfect = base, Bad = 0
```

Cliff intended: Perfect→bare-Good drops 1.0→0.7, whole Good band spans 0.3.

## 5. Cheerleader `(P=1, k=1)` — unofficial, smuggles

```csharp
pool  = J + extraMax;                 // extra = Normal+Flick+DirFlick+Hold heads in run (ticks, plain Catch: 0)
bonus = pool * (wmc / wJ);            // linear; w = 1 + premiums per judgement (§6 weights, k=1 ⇒ plain mc/J + extra)
final = base + bonus;                 // clear judged on FINAL — the smuggle, tagged
```

Catch-spam earns nothing beyond +1 combo; hard taps earn premium.
Pool realistic: ~0.5k Simple / ~2.2k Overdrive — smuggle decorative
(flips runs within ~1 miss of the line). Rating stays base-derived.

## 6. Combo Praiser `(P=10, k=2)` — same ceiling, harder ramp

```csharp
w     = 1 + isNormal + isFlick + isDir + isHoldHead;   // per judgement, ticks/Catch = 1
bonus = P * pool * pow(wmc / wJ, k);   // P=10, k=2; pool shared with Cheerleader
final = base + bonus;
```

FC ceilings identical to Cheerleader (Simple 1,000,552 / Overdrive 1,002,210).
Broken runs pay ~30% less than linear (Overdrive 5G+1M: 1097 vs 1547).
With P=10: FC 1,005,520 / 1,022,100; smuggle band ~15–22k (bounded).
Weighting the ratio (not just the pool) taxes catch-spam twice:
slow climb toward a small pool.

## 7. Rocket Ape — osu!-stable port, display-only, NO smuggle

Per judgement (heads + ticks), own combo `c` (miss resets Ape combo only):

```csharp
hv  = hitValue * (baseMax / 3);        // Perfect 300, Good 100, Miss 0 — EX mutilation
D   = 2 + 4 * (constant - 1) / 39;     // constant range 1–40 ⇒ D 2→6
ape += (hv + hv * min(c - 1, INF) * D / 25) * M;   // uncapped, div 25
```

`M` computed once at load: simulate perfect run → `rawFC`,
`M = max(1, (1M + 1K*TotalEX) / rawFC)`. Floor lifts small charts,
roof frees big ones. Clear/rank/rating/store read base; HUD-only.

Locked test results (Raintain, `good_keep = 0.5` in EX scenarios):

| Chart | FC | 5G+1M | tail |
|---|---|---|---|
| Simple (C2, J477, TotalEX584) | 1,584,000 (M 1.33×) | 945,334 (60%) | 41× |
| Overdrive (C32, J1470, TotalEX2666) | 38,790,196 (M 1×) | 21,703,627 (56%) | 305× |

Combo gravity: 95–99% of Ape score is multiplier (last note 41×/305×
first). One miss ≈ −40%. Proposed-but-open: miss-frequency momentum
envelope `m = max(0, 1 - misses / (0.01*J))` multiplying the combo term
(soft landing; Ape combo not reset, real Combo still resets).

## 8. Cheerleader-line test results (base + bonus, `good_keep = 0.5`)

| Chart | FC max | 5G+1M | smuggle sweep |
|---|---|---|---|
| Simple | 1,000,552 | 990,973 | never flips (pools ≪ per-miss cost) |
| Overdrive | 1,002,210 | 999,869 | never flips at (1,1); ~15-miss band at percentile rate; ~15–22k band at Praiser (10,2) |

Per-miss base cost ≈ 0.7–2k ppm; bonus pools §5–6. Smuggle is real
only at Praiser rates and up.

## 9. What Arcaea does (reference, not ported)

Uniform `10M/N` per note, Far half, +1 per shiny Pure, no combo bonus.
Holds/arcs: Pure-or-Lost only. Skills touch gauge/mirror globally, never
per-note-type weights. Our hard-tap premium and combo curves are
original; names avoid Arcaea terms (Perfectionist, not PM-lamp).

## 10. Implementation checklist

1. Thread `baseMax` into `AddScore()` (3 call sites + 2 tick sites).
2. Add §1 accumulators; single `enum ScoreDisplay` switch at
   `ScoreCounter.SetNumber` (`PlayerScreen.cs:1575`).
3. Ape `M` simulation where `TotalExScore` is built (`:931`); store per run.
4. Cheerleader/Praiser share one function, params `(P, k)`; assisted-clear
   marking for boosted clears (cf. Arcaea EASY-clear distinction).
5. Rank/rating/store keep base (`Helper.GetRank/GetRating`,
   `PlayerScreenResult.cs:284`, `ScoreStore.Register` — note its
   else-branch keeps old Score while updating Rating/MaxCombo; decide
   boosted-Sscore policy there).
6. Widen `ScrollingCounter` digits; clamp `ScoreRings fillAmount ≤ 1`.
