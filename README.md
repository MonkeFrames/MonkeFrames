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

**Intro screen**
- A minimal 20 second intro: the MonkeFrames logo fades in with a soft left-to-right wipe, a quiet ring ripples from the camera hand every few seconds while the logo gently breathes, and a thin loading line draws out before the editor fades in.
- Click or press any key to skip. Turn it off in *Settings > Intro on startup*, or replay it from *MonkeFrames > Play Intro*.

**Smooth mouse look**
- Press **Caps Lock** to toggle cinematic, floaty right-click mouse look (a "SMOOTH LOOK" badge shows in the menu bar).
- *Settings > Camera* has the toggle and a Light/Medium/Heavy smoothing slider.
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

> Building needs the .NET 10 SDK (C# 14), or .NET 9 with `-p:LangVersion=preview`.

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
