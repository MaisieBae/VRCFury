# Fork Changes — MaisieBae/VRCFury

This document describes all changes made in this fork relative to the upstream repository at
[VRCFury/VRCFury](https://github.com/VRCFury/VRCFury/tree/main).

---

## Overview

This fork adds a new **Exit Animation** feature to VRCFury's SPS (Super Plug Shader) Depth Action system.

In the upstream, depth animations only respond to plug entry — they activate as a plug enters a socket
and deactivate once the plug leaves. There was no mechanism to play a *separate, distinct animation*
during or after the plug's withdrawal. This fork adds that capability with a smooth, physically
intuitive approach driven by the gap between two smoothers running at different speeds.

---

## Changed Files

### 1. `com.vrcfury.vrcfury/Runtime/VF/Component/VRCFuryHapticSocket.cs`

**Type:** Runtime data model (serialized component)

**What changed:**
Three new fields were added to the `DepthActionNew` class:

| Field | Type | Default | Purpose |
|---|---|---|---|
| `useExitSmoothing` | `bool` | `false` | Enables the dual-smoother system (fast + slow) |
| `exitSmoothingSeconds` | `float` | `2.0f` | How long the slow smoother takes to decay |
| `useExitAnimation` | `bool` | `false` | Enables a separate exit animation clip |
| `exitActionSet` | `State` | new State() | The animation clip/state to play during exit |
| `exitAnimFadeSeconds` | `float` | `0.5f` | Blend-out time after plug fully leaves |

> **Note:** `useExitSmoothing`, `exitSmoothingSeconds`, and `exitActionSet` existed in an earlier iteration
> of this fork. `useExitAnimation` and `exitAnimFadeSeconds` are the final additions that complete the feature.

**Why:** The `DepthActionNew` class is VRCFury's serialized data bag for a single depth animation entry.
Adding fields here is how you persist new user-configured settings through Unity's serialization system
so they survive domain reloads, prefab saves, and build passes.

---

### 2. `com.vrcfury.vrcfury/Editor/VF/Service/HapticAnimContactsService.cs`

**Type:** Editor-time build service (generates the animator layers at avatar build time)

**What changed:**
This is the most significant logic change. The upstream version uses a **single smoother** for all depth
animations. This fork conditionally builds a **dual-smoother architecture** when exit smoothing is enabled.

#### Upstream Logic (simplified)
```
unsmoothed → mapped → smoothed → drives blend tree / animator transitions
```
One pass, one smoother, one layer. The animation tracks depth proportionally and simply stops when the
plug leaves.

#### Fork Logic (when `useExitSmoothing = true`)
```
unsmoothed → mapped → smoothedFast (entry speed)
                    → smoothedSlow (exit speed)
                    → Max(fast, slow) → smoothed  (drives main depth anim)

                    → smoothedSlow - smoothedFast → rawExitDriver
                    → Map(rawExitDriver, 0..1, 0..1) → exitDriver  (gates exit layer)

                    → Map(smoothedFast, 1..0, 0..1) → exitAnimValue  (positional exit value)
```

**Key behavioral properties of this system:**

- **While plug is entering or stationary:** `smoothedFast >= smoothedSlow`, so the gap is zero or
  negative. `rawExitDriver` is clamped to 0 — exit animation is inactive.

- **While plug is withdrawing:** `smoothedFast` drops faster than `smoothedSlow` (because the fast
  smoother tracks the receding depth more quickly). The gap rises proportionally. `exitDriver > 0`
  — exit animation activates.

- **If plug pauses mid-withdrawal:** Both smoothers converge toward the same value. The gap holds
  roughly constant — the exit animation pauses at its current frame. This is the "hold position"
  behavior described in commit messages.

- **Once plug is fully out:** Both smoothers decay to 0 together. The gap closes back to 0 —
  exit animation fades out. The `exitOn → exitOff` transition fires when `smoothedFast < 0.005f`
  and blends out over `exitAnimFadeSeconds`.

**New animator layer generated (`{layerName} - {actionNum} - Exit`):**

```
[exitOff] --( exitDriver > 0.01 AND smoothedFast > 0.01 )--> [exitOn]
[exitOn]  --( smoothedFast < 0.005, fade exitAnimFadeSeconds )---------> [exitOff]
```

- `exitOn` drives the exit clip via either `MotionTime(exitAnimValue)` (if the clip uses motion time)
  or a 1D BlendTree parameterized by `exitAnimValue`.
- `exitAnimValue` is `Map(smoothedFast, 1→0, 0→1)` — it equals 0 when the plug is fully inside,
  and 1 when the plug is at the socket entrance. This means the exit animation "plays" as the plug
  moves from deep to shallow during withdrawal.
- The `smoothedFast > 0.01` guard on entry into `exitOn` prevents re-triggering the exit animation
  from the lingering decay of `smoothedSlow` after the plug has already fully left.

**Also added:** `using VF.Utils.Controller;` — required for the new `VFAFloat` references used in
the exit layer transition conditions.

---

### 3. `com.vrcfury.vrcfury/Editor/VF/Inspector/VRCFuryHapticSocketEditor.cs`

**Type:** Unity Editor Inspector UI

**What changed:**
The `DepthActionDrawer` inner class was significantly extended to expose the new exit animation fields
in the Unity Inspector.

#### Upstream Inspector fields for a Depth Action:
- Action Set
- Activation Distance slider
- Range Units
- Allow self-trigger
- Smoothing Seconds
- Reverse clip

#### Fork Inspector fields added:
- **"Slow Close (Exit Smoothing)?"** toggle — enables the dual-smoother system
  - When enabled, reveals:
    - **"Exit Smoothing Seconds"** — controls the slow smoother decay speed
    - **"Play Exit Animation?"** toggle — enables the separate exit clip
      - When enabled, reveals:
        - **"Exit Animation"** — the action/clip to play
        - **"Exit Fade Seconds"** — blend-out duration after plug has left

**Also changed in `CreateEditor`:**
- The upstream wraps the hand-touch-zone fields inside `VRCFuryHapticPlugEditor.GetOgbHapticsSection()`
  along with an OGB/OSC ID field and calls `SpsEditorUtils.AutoHapticIdProp()` for the socket name.
- This fork simplifies the haptics section: it uses `VRCFuryHapticPlugEditor.GetHapticsSection()`
  directly and adds the touch zone fields without the OGB ID field. The socket name field is moved
  up to the top of the inspector unconditionally (not nested inside the menu toggle section).
- The `GetName()` helper used in gizmo drawing and `ShouldProbablyHaveTouchZone()` now calls a local
  `GetName(socket)` method rather than the upstream's `GetMenuName(socket)`, since the OSC/menu
  name disambiguation logic was simplified.

---

## New Functionality Summary

### Exit Animation for Depth Actions

Users can now configure a **separate animation to play as a plug withdraws** from a socket. This
animation:

1. **Activates gradually** as the plug begins to exit (not only after fully exiting)
2. **Holds in place** if the plug stops mid-withdrawal
3. **Reverses** if the plug re-enters the socket
4. **Fades out smoothly** over a configurable number of seconds after the plug has fully left

This is implemented entirely within VRCFury's non-destructive animator generation pipeline — no
manual animator editing is required. The feature is fully configurable per depth action via new
Inspector fields in the Unity Editor.

---

## Commit History (Fork-specific)

| SHA | Message |
|---|---|
| `281d7c8` | Add files via upload — VRCFuryHapticSocket.cs (exitAnimFadeSeconds field) |
| `74bb62c` | Add files via upload — HapticAnimContactsService.cs (exit layer refinements) |
| `4470807` | Add files via upload — VRCFuryHapticSocketEditor.cs (exit animation UI fields) |
| `80640f0` | Update HapticAnimContactsService.cs |
| `1b7eed3` | Attempt to make exit animation hold position while plug stops moving |
| `90055d0` | Fix exit animation to trigger gradually as plug withdraws, not just after fully out |
| `d805dc5` | Fix math.Multiply name string argument |
| `ebbc92f` | Fix exit animation to hold while plug is still partially inside socket |

---

## Compatibility Notes

- All new fields on `DepthActionNew` have default values that match the original behavior (`false` / `0`).
  Existing avatars with depth actions will not be affected unless the new options are explicitly enabled.
- This fork does not change any animator parameter names, contact tag names, or other cross-avatar
  interoperability surfaces.
- The fork is based on upstream as of **March 28, 2026** and includes all upstream commits up to that date.
