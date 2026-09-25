<h1 id="readme">
  <img src="MonkeFrames.Editor/Resources/MFtitleWhite.png" height=200><br>
  <img src="https://img.shields.io/github/downloads/MonkeFrames/MonkeFrames/total"/>
</h1>

MonkeFrames is a keyframe-based camera animator loosely based on the Orion Drift spectator view that allows you to plan out camera movements with transitions for each property.

Create a keyframe by pressing V. It's properties will show up on the MonkeFrames panel in the top right. You can tweak its transitions, position, and rotation, or replace the currently selected keyframe by pressing X.

## Installations
1. Download `MonkeFrames.zip` from the [releases](https://github.com/MonkeFrames/MonkeFrames/releases/latest) page
2. Extract the zip file into your game folder and run the game

## Usage
Press `V` to create a new keyframe. You can press `T` to create a new keyframe looking at the monke, `X` to replace the current keyframe with a new one, or press `Del` to delete the selected keyframe.

Once a keyframe is created, you can see it's values with the Keyframe Editor. Press `Window` > `Keyframe Editor` to view and select every keyframe in your project.

Once you are done with editing, you can compile your project (turn those keyframes into movement) with `Project` > `Compile`, then press `Project > Compile & Play`. Press Space to exit the player and return to MonkeFrames.

You can save your project with `File > Save Project` (CTRL+S), then reopen it by selecting `File > Load Project` (CTRL+O) and choosing your project. All projects are saved in a special folder you can access in `Documents\MonkeFrames`.

Once you are done making your animation, you can export it for usage in a video editor with `Project > Export to MP4`. Once it is done, the video will appear highlighted in your File Explorer.

## For Developers
### Issue Trackers
All issue tracking (including bug reporting, feature requests, or any other MonkeFrames inquiries) happens on the [Discord](https://monkeframes.sirkingbinx.dev). Use the `#issues` forum channel and select any tags that apply.

### Contribution
- **MonkeFrames.Editor** is freely avaliable for pull requests.
- **MonkeFrames.Compiler** is avaliable for pull requests but is much less open to change. We accept optimization tweaks, code cleanup, but not much in terms of functionality change.

### Embed MonkeFrames into your project
You can embed the keyframe functionality of MonkeFrames into your own projects. See [MonkeFrames.Compiler](/MonkeFrames.Compiler).

## Credits
- [sirkingbinx](https://sirkingbinx.dev): Developer, documentation, concept art
- [uhJames](https://www.youtube.com/@uhJamesvr): Design, logos, art, commissioner
- [YourBoiAlex](https://www.youtube.com/@YOURBOIALEX.124): Developer, UI work
- [Olibobs](https://github.com/Olibobs81): Developer
