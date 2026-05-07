# PC Input System — Implementation Notes

> This document describes the high-level architecture of the PC keyboard + mouse input system
> (`PCInputManager`, `PCMouseOwnershipCueManager`, `PCMouseOwnershipCue`) so design decisions
> and known gaps can be reviewed without reading the full source.

---

## Entry point

`PlayerInputManager.UpdateInput()` checks `DesktopInput`. When true, it calls
`PCInputManager.sInstance.UpdateInput()` and returns immediately. Everything below happens
inside that single call per frame — no `MonoBehaviour.Update()` on `PCInputManager` itself.

---

## Keyboard — KeyClass mirrors TouchClass

Each held key is wrapped in a `KeyClass` that persists from keydown to keyup in the
`KeyClasses` dictionary (analogous to `TouchClasses` in `PlayerInputManager`).

| KeyClass field | TouchClass equivalent | Purpose |
|---|---|---|
| `KeyCode` | finger index | identity |
| `Initial` | `Tapped` (first frame only) | new-press detection |
| `PressTime` | `StartTime` | timing delta for queued-hit resolver |
| `PressPosition` | `touch.Touch.startScreenPosition` | tap-flick position check |
| `QueuedHit` | `QueuedHit` | note claimed this press, resolved end-of-frame |
| `QueuedHitDistance` | `QueuedHitDistance` | nearest-note tie-break (unused for keyboard Normal notes — no proximity check) |
| `DiscreteHitobjectIsInRange` | `DiscreteHitobjectIsInRange` | tap-protection gate |
| `NearestDiscreteHitobject` | `NearestDiscreteHitobject` | tap-protection comparison |
| `CatchCooldownExpiry` | *(no equivalent)* | per-key catch throttle |

Raw input uses `InputSystem.onEvent` + `EnumerateChangedControls` — only changed controls
are visited per event, zero allocation on mouse move / analog events.

---

## Tick structure (UpdateInput)

```
1. Mouse flick tracking     — VelocityTracker push, IsGesturing, FlickCenter, Flicked commit
2. HitQueue processor       — HitobjectProcessor per note, discrete marking, missed-note forced hit
3. HoldQueue processor      — verbatim mirror of PlayerInputManager.HoldQueue_Processor
4. DiscreteHitQueue processor — catch auto-clear at note time
5. Queued-hit resolver      — fires QueuedHit for all Initial keys, then clears Initial
6. Cue tick                 — PCMouseOwnershipCueManager.sMain?.UpdateCue(deltaTime)
```

---

## How "chords" work (no chord abstraction)

There is no chord budget or grouping window. N simultaneous keypresses each produce N
`KeyClass` wrappers all with `Initial = true`. In `HitobjectProcessor`, each `Initial` key
independently claims the front eligible note in the HitQueue (`QueuedHit`). One key = one
note max. The queued-hit resolver fires them all at end of frame. N keys → N notes hit.
That's the chord, emergent, same mechanism as two fingers on a touchscreen.

---

## Note type handling

| Note type | Input source | Cursor used? |
|---|---|---|
| Normal tap | Any `Initial` key — first eligible note in window | No |
| Catch (held) | Any held key passing cooldown gate | Only for per-key cooldown bypass — if cursor is already over the note (e.g. ownership queue placed it there), the throttle is waived. Not player-directed. |
| Flickable Normal (tap-flick) | `Initial` key + mouse gesture | Yes — `PressPosition` for corridor/radius check |
| Flickable Catch (catch-flick) | Mouse gesture only | Yes — `FlickCenter` and current position |
| Hold sustain | Any held key | Yes — cursor must be within hold tail hitbox |

---

## Catch throttle

After a key clears a catch note via hold (not a new press), a cooldown of `60 / BPM / 16`
seconds (one 1/16th note) is stamped on that specific `KeyClass`.

While on cooldown, the key cannot clear another catch note **unless** the cursor intersects
the incoming note's screen-space hitbox (`HitCoord.Position ± Radius`). The intersection
bypass means spatially adjacent catch notes (that would naturally share one input position)
are still clearable; only genuinely distant notes require a new keypress.

Chord presses (new `Initial` keys) bypass the cooldown entirely — they are distinct inputs
by definition.

---

## Mouse flick tracking

`MouseClass` is a single persistent object (one cursor) carrying the flick state that
`TouchClass` would carry per finger. Key fields:

- `VelocityTracker` — quadratic regression over 10 samples (identical to touch engine)
- `IsGesturing` — velocity pre-gate: `speed >= 1.8 × (dpi/275) × dpi`
- `FlickCenter` — origin of current gesture, reset on a 0.08 s naive clock or snapped to
  hitbox entry when a flickable note is approaching
- `Flicked` — committed once `IsGesturing && distance >= flickThreshold (dpi × 0.2)`
- `flickDirection` — CW-from-up degrees, tracked from `threshold/2` onward for stability

Verifiers:

- **TapFlickVerifier** — requires an `Initial` key press; checks position (corridor for
  directional, radius for omni) then delegates to FlickVerifier for direction confirmation.
- **FlickVerifier** — for catch-flick, checks cursor/FlickCenter was inside hitbox;
  validates direction against `FlickDirection` via `ValidateFlickDirection` (±25°/27.5°).

After a flick note is hit, `Flicked` and `flickDirection` are reset.

### Known gap — windowed mode DPI
`Screen.dpi` is used raw, matching `PlayerInputManager`. In windowed mode on PC the rendered
pixel space may not match physical pixel density if the window is scaled. This affects the
flick threshold and velocity threshold. To be addressed in both engines together when
windowed mode becomes a supported configuration.

---

## Hold sustain

`IsPlayerHolding` in `HoldQueue_Processor` requires:
- At least one key held (`KeyClasses.Count > 0`), **and**
- Cursor within the hold note's screen-space hitbox (`HitCoord.Position ± Radius`)

The cursor check is intentional — the mouse tracks the hold tail (ownership queue teleports
cursor on transfer), so cursor proximity is the correct "are you still holding this note"
signal, unlike keyboard keys which have no spatial meaning for holds.

---

## Mouse ownership queue (cue system)

Hold notes and flickable notes register with `_OwnershipQueue` on `EnqueueHoldNote`. The
front entry owns the cursor; on transfer the cursor is warped to `HitCoord.Position` and
`PCMouseOwnershipCueManager.sMain?.OnOwnerChanged(...)` fires.

Priority stack when cursor is occupied or warm (within `BPM / 4 / 1000` seconds of last release):

1. Easing type rank (Snap detected by near-zero float-delta > Elastic > Expo > Circle > Back > Quintic > Quartic > Cubic > Quadratic > Sine > Linear)
2. Float delta tiebreaker (`NotePosition × laneLength` summed over 2-beat sample window; threshold `< 0.2` falls through)
3. Flick direction specificity (directional beats omni)
4. Shortest cursor distance
5. Last-come-first-serve

---

## Cue system (PCMouseOwnershipCueManager / PCMouseOwnershipCue)

`PCMouseOwnershipCueManager` is a standalone singleton (`sMain`) on its own Canvas
(Screen Space Overlay), analogous to `PlayerHitboxVisualizer`. `PCInputManager` has no
canvas reference — it only calls `sMain?.UpdateCue()` and `sMain?.OnOwnerChanged()`.

On each ownership transfer, `OnOwnerChanged` destroys the previous prefab instance and
instantiates a fresh `PCMouseOwnershipCue`. The cue follows `owner.HitCoord.Position`
each frame (updated live by `HoldQueue_Processor`). Animation phases:

| Phase | Range | Shape | Easing |
|---|---|---|---|
| 1 — ring fill | 0–55% | `GraphicCircleGPU` ring (4 sides, insideRadius 0.65) | InOutExpo |
| 2 — inner fill | 55–75% | `GraphicCircleGPU` fill (4 sides, insideRadius 0) | OutCircle |
| 3 — alpha fade | 75–100% | `CanvasGroup` alpha | OutCubic (halved duration for discrete notes) |

Duration is `1 / (laneStep.Speed × Player.Speed)` seconds. Rotation is the lane's
screen-space orientation + 25°.

---

## Open items

- Windowed-mode DPI scaling for flick/velocity threshold (shared gap with touch engine)
- BPM mid-song edge case for ownership warm-window (uses `GetStop` at current time; needs
  to handle tempo changes that land exactly on the hold release frame)
- Flick re-trigger guard (`LastFlickHitTime` is stored but the raised-threshold logic from
  the touch engine is not yet applied in the invalidator — currently just uses `PerfectWindow` timeout)
- `QueuedHitDistance` on `KeyClass` is populated for tap-flick's nearest-key selection
  but unused for Normal notes (no proximity check); can be removed if tap-flick nearest-key
  selection is dropped in favour of first-key-wins
