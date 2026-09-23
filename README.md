<h1 id="readme">
  <img src="MonkeFrames.Editor/Resources/MFtitleWhite.png" height=200><br>
  <img src="https://img.shields.io/github/downloads/sirkingbinx/MonkeFrames/total"/>
</h1>

MonkeFrames is a keyframe-based camera animator loosely based on the Orion Drift spectator view that allows you to plan out camera movements with transitions for each property.

Create a keyframe by pressing V. It's properties will show up on the MonkeFrames panel in the top right. You can tweak its transitions, position, and rotation, or replace the currently selected keyframe by pressing X.

## Installations
1. Download `MonkeFrames.zip` from the [releases](https://github.com/sirkingbinx/MonkeFrames/releases/latest) page
2. Extract the zip file into `BepInEx/plugins/` folder and launch your game

## Usage
Press `V` to create a new keyframe. You can press `T` to create a new keyframe looking at the monke, `X` to replace the current keyframe with a new one, or click `Keyframe` > `Delete Keyframe` on the topbar to delete the selected keyframe.

Once a keyframe is created, you can see it's values with the Keyframe Editor. Press `View` > `Keyframe Editor` to view and select every keyframe in your project.

Once you are done with editing, you can compile your project (turn those keyframes into movement) with `Project` > `Compile`, then press `Project > Compile & Play`. Press Space to exit the player and return to MonkeFrames.

You can save your project with `Project > Save Project`, then reopen it by selecting `Project > Load Project` and choosing your project. All projects are saved in a special folder you can access by pressing `Win` + `R`, and then entering `%USERFOLDER%/AppData/LocalLow/Another Axiom/Gorilla Tag/MonkeFrames/projects`.

## Custom UI & Smooth Keyframes
This build adds a fully custom, animated interface and a smooth keyframes option.

**UI**
- Dark rounded theme with soft shadows, tinted by your accent colour (Settings). All textures are generated at runtime.
- Every window fades, slides and scales in when opened and out when closed; the title bar accent line grows in.
- Menu bar with animated hover, an accent underline and dropdowns that slide open with staggered items and shortcut hints. Hover another menu while one is open to switch; click outside to close.
- Animated controls: toggle switches, segmented pickers with a gliding highlight, sliders with a trailing fill, colour swatches.
- Keyframe Editor: animated keyframe list (sliding selection, rows slide in), properties that cross-fade when you change selection, a live curve preview for each transition, and double-click a row to jump the camera there.
- Status messages appear as a toast at the bottom-left with a countdown bar.
- Settings: turn UI animations off or change their speed.

**Other Cameras** (new menu + window)
- Live camera modes that follow any gorilla in your lobby, or yourself: **Orbit**, **First Person**, **Follow**, **Shoulder** and **Tracking**. Switching modes blends smoothly; going back to Free returns the camera to where it was. Press V in any mode to keyframe that view. If the watched player leaves, it switches to you.
- **Orbit** always keeps the gorilla centred. Auto spin on/off + speed, distance, angle, height, "turn with gorilla". Manual control: left-drag, A/D around, W/S angle, Q/E height, scroll zoom, Shift = faster.
- **First Person** is locked exactly to the head (no position lag); only turning is smoothed. Level horizon and eye offset.
- **Follow**: distance, height, side offset, turn lag (scroll = distance). **Shoulder**: distance, height, side, swap shoulder (scroll = distance). **Tracking**: optional auto-zoom that keeps the gorilla the same size.
- **Front / Selfie**: in front of the gorilla looking back at their face (distance, height, turn lag). **Top Down**: bird's-eye view (height, turn with gorilla). **Side View**: side-on "2D" view from a fixed direction (direction, distance, height). **Hand Cam**: strapped to the left or right hand, looking at the face or along the hand. **Director**: automatic shots that cut or glide between Orbit, Follow, Front, Shoulder, Top Down and Side View every few seconds.
- **No more lag when spectating**: the camera is now placed at the last moment before each frame renders, after every player (including remote players) has moved, and it is locked rigidly to the player. Smoothing only eases angle changes, never makes the camera trail behind. Want a floaty drone feel? Turn up the new *Position lag* slider (default 0 = locked).
- Motion blur in Other Cameras keeps the gorilla you're watching sharp while the world streaks past (like a camera riding along with them), and the blur is centred on the current frame so it never looks like the camera is lagging behind, even in First Person at full speed.
- Every mode: FOV, smoothing, position lag, aim height, tilt (dutch angle), handheld shake, real-time motion blur, and Reset. The window scrolls if it doesn't fit your screen.

**Spectator camera model**
- While your MonkeFrames camera is in use (free cam, any Other Camera, playback), other players who also have MonkeFrames see a camera model (with your name and a blinking light) where your camera is, including in VR. You never see your own.
- Settings > Spectator camera model: *Show my camera to other MonkeFrames users*, *Show other people's cameras*, *Show cameras in replays*.
- Replays record every MonkeFrames camera (yours and other mod users') and show them as camera models during playback (toggle in the Replays window).
- Sent as a tiny unreliable Photon event (~12 per second) to the other players in the room; players without MonkeFrames just ignore it.

**Replays** (new menu + window, F9 to record)
- Press **F9** (or *Start recording* in the Replays window) and everyone in the lobby is recorded: their movement (body, arms, head, fingers, cosmetics) and their **voice chat**, plus **your own mic**. Press F9 again to stop; the replay opens straight away and saves itself to *Documents/MonkeFrames/replays*.
- Quality 30 / 60 / 90 fps, max length 5-30 min, and switches for recording voices and your mic.
- Playback uses copies of each gorilla, so you can fly the free camera around them or point **any Other Camera** at them (Orbit, First Person, Follow, Director...). Hit *Film* next to a gorilla, or pick them from the Other Cameras list (marked "replay").
- Editing: scrub the timeline, drag the In/Out handles (or *Set In* / *Set Out*) to trim, 0.25x-2x slow motion, loop, rename, hide a gorilla, mute someone's voice. Lanes under the timeline show when each gorilla is in the replay; ticks show when people talk. Save, load and delete replays in *Saved replays*.
- **Sync with keyframes**: keyframe time 0 = the replay's In point, so the Player, *Project > Play* and **MP4 export** play the replay in time with your camera animation. Build a camera move over a replay and export it.
- Playback options: hide the live players while watching, mute the live game, 3D voices (they pan and fade with the camera) and voice volume up to 200%.
- Notes: replays you just recorded keep everyone's exact look. Replays loaded from a file rebuild gorillas from your own gorilla's model with their colour and cosmetics where possible, and without name tags. MP4 export is video only (voices aren't in the video file yet). Replays happen where they were recorded, so load them in the same map.

**Replay Studio** (editing layout, only while a replay is open)
- When a replay is showing, the screen switches to an editing layout: the game view becomes a viewport (top left), with **Camera Preview** and **Keyframe** inspector on the right, and **Replay** transport + a **Timeline** along the bottom. Close the replay (or press *Exit*) and everything goes back to normal.
- The timeline is in replay time: rows for keyframes, transitions (coloured by type), FOV, motion blur, when each gorilla is present, and voice. Click / drag to scrub, drag a keyframe diamond to retime it (other keys stay put), double-click to jump the camera there, mouse wheel to zoom, Shift+wheel to pan.
- **V** adds a keyframe from the current camera exactly at the playhead (between keys if needed), **X** / *Update* moves the selected keyframe to the camera, **Delete** removes it without shifting the others, **F** jumps to it. **Space** plays / pauses, **,** and **.** step one frame.
- Camera Preview shows what your keyframe camera sees at the playhead (live render). *Viewport follows camera path* looks through it in the main view; *Show path* draws the camera path in the world.
- Replay panel: play/pause, frame stepping, 0.1x-2x speed, frame slider, loop, hide live players, mute game, voice volume. Timeline toolbar: Add, Update, Delete, Clear All, Save / Load project and Export (MP4 of your keyframes over the replay).
- Turn the layout on or off in the Replays window or *Replays > Replay Studio Layout*.

**Motion blur**
- Keyframe Editor: a *Motion blur* switch and strength slider per keyframe (under FOV). The camera is blurred while it moves from that keyframe to the next, in the Player preview (while playing), Project > Play and MP4 export. "All" copies the setting to every keyframe.
- Real camera motion blur: each frame the camera is rendered several extra times at in-between positions along its path (6 samples in preview, 12 in MP4 export) and the renders are averaged, like a film camera's open shutter. Strength sets the shutter length. Cuts and teleports are never blurred.

**Notification sound**
- A soft two-note chime plays when a notification pops up. Toggle it and set the volume (with a Test button) in *Settings*.

**Intro screen**
- A minimal 10 second intro: the MonkeFrames logo fades in with a soft left-to-right wipe, a quiet ring ripples from the camera hand every few seconds while the logo gently breathes, and a thin loading line draws out before the editor fades in.
- Click or press any key to skip. Turn it off in *Settings > Intro on startup*, or replay it from *MonkeFrames > Play Intro*.

**Mouse controls**
- **Left-drag** looks around, **right-drag** tilts (rolls) the camera. Drags only start when you click outside the MonkeFrames windows, so using the UI never moves the camera.

**Smooth mouse look**
- Press **Caps Lock** to toggle cinematic, floaty mouse look and tilt (a "SMOOTH LOOK" badge shows in the menu bar).
- *Settings > Camera* has the toggle and a Light/Medium/Heavy smoothing slider.
- With Smooth Look on, scroll-wheel FOV zoom glides too (same smoothing slider). Scrolling over a MonkeFrames window no longer zooms the camera.
- Mouse look now starts from wherever the camera is pointing (e.g. after Go to keyframe) instead of snapping back.

**Speed graph editor**
- New **Custom** transition: drag the two handles on the graph to shape the camera's speed (the white line shows speed, the coloured line shows progress). Handles can go above/below the box for overshoot and wind-up.
- Presets: Linear, Ease, In, Out, In-Out, Snappy, Overshoot, Wind-up. Picking one switches the keyframe to a custom curve.
- **Smooth** keyframes can use the graph too: turn on *Custom speed curve* to control speed along the curved path.
- "Apply to all keyframes" copies the curve as well.

**Smooth keyframes**
- New transitions: **Smooth**, **Ease In** and **Ease Out** (alongside Linear, Sine and Cut).
- **Smooth** moves the camera along a curved spline through your keyframes and keeps its momentum instead of stopping at each one. Speed stays continuous even when neighbouring transitions have different durations.
- *Project Settings > Frame rate* now goes from 24 up to 360 fps (24, 30, 48, 50, 60, 90, 120, 144, 165, 240, 300, 360).
- *Project Settings > Smoothness* controls how much it flows (0 = stop at each keyframe, 1 = fully flowing, up to 1.5 for extra swoop).
- Keyframe Editor: **Make all Smooth** and **Apply to all keyframes** buttons. *Settings > Smooth keyframes by default* makes new keyframes Smooth.
- The **Sine** curve now eases smoothly across the whole transition (it used to reach the target about 64% of the way through and then hold).

> **Building:** close Gorilla Tag and double-click `BUILD-AND-INSTALL.bat`. It builds with the .NET 10 SDK and installs the DLLs into `BepInEx\plugins\MonkeFrames`. (Don't use `installer.bat`: it downloads the original MonkeFrames from GitHub.)

## For Developers
### Issue Trackers
All issue tracking (including bug reporting, feature requests, or any other MonkeFrames inquiries) happens on the [Discord](https://discord.gg/tDjSs2txsR). Use the `#issues` forum channel and select any tags that apply.

### Contribution
- **MonkeFrames.Editor** is freely avaliable for pull requests.
- **MonkeFrames.Compiler** is avaliable for pull requests but is much less open to change. We accept optimization tweaks, code cleanup, but not much in terms of functionality change.

### Embed MonkeFrames into your project
You can embed the keyframe functionality of MonkeFrames into your own projects. See [MonkeFrames.Compiler](/MonkeFrames.Compiler).

## Credits
- [sirkingbinx (bingus)](https://sirkingbinx.dev): Developer, documentation, concept art
- [uhJames](https://www.youtube.com/@uhJamesvr): Design, logos, art, commissioner

## Extras
![Concept Art](/MonkeFrames.Editor/Resources/MonkeFrames_Concept.png)
