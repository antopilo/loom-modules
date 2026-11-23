# Godot Game

Play a local Godot 4.7+ project in a Loom panel on Windows. The module starts the Godot executable you select; it does not download Godot or project files.

1. In **Settings → Godot Game**, set **Godot executable** to the full path of the Windows Godot executable.
2. Set **Godot project folder** to the full path of the folder that contains `project.godot`.
3. Run **Play Godot Game** from the command palette. Click inside the game to give it mouse and keyboard focus. Use the game's controls to release the mouse; in many games this is `Escape`.
4. Use **Stop** in the panel to close the game.

If the settings are blank, the module uses `GODOT_EXECUTABLE` and `GODOT_PROJECT`. The game runs on the PC that runs Loom, not on a paired host.

If you have a local copy of this module, move it out of Loom's source module folders before installing the community copy. Both copies use the same module ID.
