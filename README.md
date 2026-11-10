# Bribery+

More uses for the game's **Bribery** skill in Obenseuer: ask the people you meet for money.

## Features
- **Ask anyone for money.** Look at a person and press **B** (configurable). A hint under the crosshair shows your chance of success.
- **Uses the game's own Bribery skill.** Your Bribery level sets the odds and the reward. Each try gives Bribery XP the way the game does (more XP when the odds were worse), so it also makes police bribes and talking down robbers easier.
- **Rewards grow with your level.** 3 to 12 OC at level 1, about 4.6 times that at level 10. Higher levels also have a growing chance (up to 40%) of paying in RM, worth a bit more.
- **Everyone is different.** Some people are generous and some are stingy, and each person stays the same.
- **Police pay more** (twice as much) but are much harder to convince, and more likely to report you.
- **Failing has risks.** People may be offended and refuse to talk money with you for a few days, or report you for harassment.
- One try per person per in-game day. Alcohol and mushrooms affect your odds the way the game's skills work.
- The "asked today" and "offended" lists are saved with your save game.

Every number can be changed in the config file or with ConfigurationManager.

## Install
1. Install BepInEx 6 (Unity.Mono, x64) for Obenseuer.
2. Put `Obenseuer-BriberyPlus.dll` in `BepInEx\plugins\Obenseuer-BriberyPlus\`.

## Build
`dotnet build -c Release` (set `/p:ObenseuerDir=...` if the game is not in the default Steam folder).

Made by JuanBerta, with AI assistance (Claude).
