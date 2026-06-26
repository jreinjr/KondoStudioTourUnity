# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A full-screen Unity kiosk installation: visitors point at a wall-projected, branching
slideshow with their arm, and an overhead depth sensor (Nuitrack) turns that gesture into
an on-screen cursor. Dwelling the cursor on a hotspot triggers a slide transition. There is
no keyboard/gamepad interaction in the exhibit — the mouse exists only as a development
fallback.

The project splits cleanly into two halves that meet at one seam:

- **Pointing** (`Assets/Kondo/Scripts/{Core,Pointing,UI}`) — sensor → room-space skeleton →
  aim ray → screen UV → a single "active user" cursor.
- **Slideshow** (`Assets/Kondo/Scripts/Slideshow`) — a state machine driving a graph of
  slide prefabs, with seamless-video and fade-through-black transitions.
- **The seam** is `SlideshowPointerProvider`, which flattens the mouse and the active
  skeleton cursor into a list of screen-pixel hover points the slideshow hit-tests.

All project code lives under the `Kondo.*` namespaces. There are **no assembly definitions**,
so everything compiles into the default `Assembly-CSharp` / `Assembly-CSharp-Editor`. The
`.csproj`/`.sln` files in the root are Unity-generated and git-ignored.

## Environment & key tooling

- **Unity 6000.3.12f1** (see `ProjectSettings/ProjectVersion.txt` — match it exactly).
- **URP 17.3** render pipeline; UI is screen-space-overlay Canvas only (the camera culls
  everything in world space).
- **New Input System** (`com.unity.inputsystem`) is the active handler — use
  `UnityEngine.InputSystem`, not legacy `Input`.
- **AVPro Video** (`RenderHeads.Media.AVProVideo`, vendored in `Assets/AVProVideo`) plays
  **all** video. Do not use Unity's built-in `VideoPlayer`. Every player is configured
  `AutoOpen=false, AutoStart=false` and driven explicitly.
- **Nuitrack SDK** (`Assets/NuitrackSDK`) provides skeleton + floor-plane data via
  `NuitrackManager.sensorsData[0]`. Requires the sensor + a machine-specific `nuitrack.lock`
  (git-ignored). Without hardware, the pointing system stays uncalibrated and only the mouse
  drives hover.

## Working in this repo (there is no CLI build/test loop)

This is a GUI Unity project; day-to-day work happens in the Editor. Productivity hinges on
the custom **`Kondo/` menu**, which builds the scene and all base prefabs *from code* (in
`Assets/Kondo/Scripts/Editor`). These builders are **idempotent** — re-running them replaces
the generated scene objects but never overwrites existing assets (styles, slide prefabs), so
they double as repair tools.

- **`Kondo/Build Studio Tour Scene`** — full rebuild of `Assets/Scenes/KondoStudioTour.unity`:
  camera, `NuitrackScripts` (AI on, 6 users), pointing system, cursor/debug canvases, then
  calls the slideshow rig builder. Start here after pulling or if the scene looks broken.
- **`Kondo/Build Slideshow Rig`** — (re)builds just the video/slide/blackout canvases +
  `SlideshowController` in the open scene (does not save).
- **`Kondo/New Slide Prefab`** — clones the base `SlideTemplate` into
  `Assets/Kondo/Slideshow/Prefabs/Slides/`.
- **`Kondo/Upgrade Slide Prefabs`** — migrates pre-`ZoomRoot` slide prefabs to the current
  structure. Run this if a slide warns about a missing `zoomRoot`.
- **`Kondo/Setup UI Prefab Environment`** — points the prefab stage at a design-resolution
  canvas so UI prefabs render correctly when opened.

**Building the player:** standard Unity build (File ▸ Build, the single enabled scene is
`Assets/Scenes/KondoStudioTour.unity`) or batch mode, e.g.
`Unity.exe -batchmode -quit -projectPath . -buildWindows64Player Builds/out/app.exe`.
Output goes to `Builds/` (git-ignored). There is no custom build script.

**Tests:** the project has no test suite of its own. `com.unity.test-framework` is present
and `Assets/NuitrackSDK/HandTest.cs` exists, but that is vendor demo code — do not treat it
as a project test target or invent a test command.

**Runtime diagnostics:** the slideshow and AVPro players log every state/media event with
timestamps. In a build the log is at
`%USERPROFILE%\AppData\LocalLow\xispa\Kondo_StudioTour\Player.log` — it is the primary tool
for diagnosing transition/video issues, which usually differ between Editor and build.

## Architecture — the pointing pipeline

Data flows one direction, recomputed every frame, orchestrated by **`UserPointerManager.Update()`**:

1. **`SensorPoseCalibrator`** establishes `RoomFromSensor` (a `Matrix4x4`) by averaging the
   Nuitrack floor plane over a warmup, then **freezes** it (live floor estimates jitter when
   people occlude the floor). Room space: origin on the floor under the sensor, +Y up, +Z
   into the room, so the wall is at negative Z and aim rays travel toward −Z. A manual
   override/fallback exists for when the floor plane is untrusted.
2. Per user, a **`PointingArmSolver`** runs one **`JointTracker`** per relevant joint,
   chooses the pointing arm (hysteresis), and builds an aim ray via a **fallback chain** of
   `RayModel`s (forearm → whole-arm → upper-arm) that degrades gracefully as joints drop out.
   An enter/exit dwell state machine decides `IsPointing`.
3. **`ProjectionScreen.RaycastToUV`** intersects the ray with the wall plane and maps the hit
   to normalized screen UV, applying artistic `flipX` / `aimGain` / `aimOffset` trims and an
   edge margin (the cursor pins to the edge instead of vanishing).
4. The manager smooths UV (outlier gate → One Euro filter → render-rate spring with a rest
   detector for steady dwell), then **selects one active user** (most central pointing
   skeleton, with sticky switching) and drives the `PointerCursorView`s.

**The pervasive sensor-cadence vs render-cadence pattern:** Nuitrack delivers ~30 Hz while
Unity renders faster. Feeding repeated (stale) frames into the One Euro filters would corrupt
their derivative/speed estimates. So throughout `JointTracker`, `SensorPoseCalibrator`, and
`UserPointerManager`, **bit-identical raw input is treated as "no new sensor frame" and
skipped**, while `dt` is accumulated (`PendingFilterDt`, `accumulatedDt`) so the next real
sample filters with honest elapsed time. Preserve this when touching any filtering code.

`Kondo.Core.OneEuroFilter` (Casiez et al. 2012) is the shared smoothing primitive
(scalar/Vector2/Vector3); the Vector variants derive one adaptive cutoff from full-vector
speed so a filtered direction doesn't bend.

## Architecture — the slideshow

**`SlideshowController`** is a coroutine state machine (`Idle → AwaitingVideo → FadingOut →
PlayingVideo → FadingIn`, plus `Blacking*`/`Black*` and `OverlayShowing`). Only in `Idle` does
it poll hotspots and auto-advance.

The show is a **graph of slide prefabs** reachable from `startSlidePrefab`; there is no scene
list. The edge type is **`SlideTransitionTarget`** (target slide prefab + `TransitionKind`
Video/FadeThroughBlack + a StreamingAssets-relative video path), embedded in both
`SlideHotspot` and each slide's auto-advance block, so the controller handles one shape
regardless of trigger.

- **`Slide`** is the prefab root. Structure: `root → ZoomRoot(BackgroundImage / BackgroundVideo
  / Masks / Hotspots) + Text`. Overlay zoom scales `ZoomRoot` only, so **`Text` lives outside
  it** and never zooms. Background kind is Image / VideoLoop / VideoOnce (per-slide AVPro
  `MediaPlayer`); VideoOnce holds its last frame and can auto-advance on end. **Auto-advance is
  a bool + mode on the `Slide` itself** (there is no separate component); only its delay/window
  durations come from the global style.
- **`SlideHotspot`** hover is **point + radius** (normalized `point`, radius in design units),
  not the rect — the graphic is typically a full-screen alpha PNG. Hit-testing finds the
  nearest point within radius per pointer, with exit-radius hysteresis so edge jitter doesn't
  reset the dwell. `action` is `Transition` (go to target) or `ShowOverlay` (fade hotspots out,
  zoom toward the point, fade on `overlayElements`, hold, reverse — all in-slide). Elements
  listed in any hotspot's `overlayElements` are automatically excluded from the slide's enter
  fades. Drag the hover point with the scene handle (`SlideHotspotEditor`).
- **Seamless video transitions** (`VideoTransition`): the controller pre-opens the likely next
  video *paused* (on hover, before firing) so its first frame is ready, fades the current slide
  out over that frame, plays, builds the incoming slide invisibly above the playing video, then
  fades it in over the held last frame. `TransitionVideoPlayer` is the single shared player and
  has a **stall watchdog** (re-kick `Play()` once after 1.5 s of no progress, then force-finish)
  plus a first-frame nudge, because AVPro backends behave differently in builds vs Editor. On
  any failure it falls back to fade-through-black.
- **`SlideshowStyle`** (`Assets/Kondo/Slideshow/Styles/DefaultSlideshowStyle.asset`) is one
  shared ScriptableObject controlling all look + timing. Components apply it in `OnValidate`
  (editor preview) and `Awake` (runtime). Most per-element fields have a local `override*` flag.

## Conventions & non-obvious gotchas

- **Design resolution is 2880×2160** (`KondoSlideshowBuilder.DesignResolution`). Every
  `CanvasScaler` uses it, and **slide prefab roots are authored at that fixed size** (so the
  prefab stage shows real content instead of a collapsed 100×100 rect); `SlideshowController`
  forces full-stretch on spawn. Hotspot radii in the style are in these design units.
- **Canvas sorting orders** (all screen-space-overlay): VideoCanvas −20, SlideCanvas −10,
  BlackoutCanvas −5, CursorCanvas 0, DebugCanvas 100. The opaque slide background occludes the
  transition video below it until the controller fades the slide out — that occlusion *is* the
  transition mechanism.
- **Incremental GC is ON** (`gcIncremental: 1`) and is the suspected cause of a build-only bug
  where coroutines spawned by the static `Fading.Fade` (closure callback) silently died mid-run.
  `SlideshowController.FadeAndWait` fades **inline** to avoid this; fire-and-forget `Fading.Fade`
  calls (hotspot/element/overlay fades) remain potentially exposed. Disabling incremental GC for
  the kiosk is recommended. Be wary adding nested fade coroutines on the critical path.
- **`runInBackground` must stay ON** (`runInBackground: 1`) — any focus loss otherwise freezes
  the app and AVPro. Don't regress this in Player Settings.
- **Video assets are git-ignored.** `.gitignore` excludes all `*.mp4`/`*.mov`/etc. and
  `Assets/StreamingAssets/Transitions/` (the actual transition clips), keeping only images in
  git. Transition videos live in `StreamingAssets/Transitions/`, slide background videos in
  `StreamingAssets/Slides/`. Paths in prefabs are **relative to StreamingAssets**. A "missing
  video" error is almost always an authored-path / filename mismatch (the on-disk transition
  filenames are inconsistent, e.g. `painting_to-bowls.mp4` vs `office_room-to-bowls.mp4`), not a
  path-separator issue — `File.Exists` accepts mixed slashes on Windows.
