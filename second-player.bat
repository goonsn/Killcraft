@echo off
rem Killcraft: a second ULTRAKILL on this PC as player 2, to try multiplayer alone.
rem Player 1 first: HostForSecondPlayer = true in BepInEx\config\dev.killcraft.cfg ([Multiplayer]),
rem then start ULTRAKILL normally. Player 2 has its own config: BepInEx\config\dev.killcraft.p2.cfg.
start "" "%~dp0..\..\..\ULTRAKILL.exe" -killcraftPlayer 2 -screen-fullscreen 0 -screen-width 960 -screen-height 540
