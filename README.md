# Hitman WoA Patch Changer

An app used for switching between versions of Hitman without installing the game from scratch. It uses the [DepotDownloader](https://github.com/SteamRE/DepotDownloader) to determine which game files have changed, and then installs them.

Currently this only works for Steam and Windows.

You must own the game on the Steam account you sign in with.

## Downloading

Download the latest zip from Releases, extract it, and double click the HitmanPatchChanger.exe.

This will open a command prompt window (do not close this until you are done) and should automatically open a tab in your browser, if it didn't, scroll to the top of that command prompt window and follow the link listed there.

## How to use (already downpatched)

1. Set an install directory. This location is where you have the downpatched game installed.
2. Set a DepotDownloader directory. Choose the folder that contains DepotDownloader.exe and continue to the next step, or an empty folder, then click Install.
3. Sign in with your Steam username + password, or with the Steam QR code, and follow the steps on screen.
4. Click Switch on a version, or paste in your own manifest ID and click Switch to custom, then confirm.
5. After a successful switch, run the HITMAN3.exe in the Retail folder of the game to play the game. You can also add this .exe as a non-steam game, and just use that for any future changes. If you have already set it up as a non-steam game beforehand, you can continue using that.

The first switch takes longer than later ones. DepotDownloader stores the last manifest in a `.DepotDownloader` folder inside the install directory, and later switches only download files that changed. If that folder is missing and the game files are already there, every file is checksummed first.

## How to use (no downpatch yet)

1. Set an install directory using one of these 2 options:
    - Faster: Make a new folder and copy your game files into it, then set that folder as the install directory. For this, in Steam, right click the game, Manage, Browse local files, and copy everything in that folder into the new one. That folder should have Retail, Runtime, and Launcher.exe. Leave the original steam one as is if you want to keep your original install.
    - Slower: Set a new empty folder. The game will be installed there from scratch.
2. Set a DepotDownloader directory. Choose the folder that contains DepotDownloader.exe and continue to the next step, or an empty folder, then click Install.
3. Sign in with your Steam username + password, or with the Steam QR code, and follow the steps on screen.
4. Click Switch on a version, or paste in your own manifest ID and click Switch to custom, then confirm.
5. After a successful switch, run the HITMAN3.exe in the Retail folder of the game to play the game. You can also add this .exe as a non-steam game, and just use that for any future changes.

The first switch takes longer than later ones. A copied install is checksummed first because `.DepotDownloader` does not exist yet. An empty folder downloads the whole game. Later switches only download files that changed as long as `.DepotDownloader` is still in the install folder.

## Extra Info

For any extra info (Notable Patches, ZHM compatibility) on downpatching refer to the [downpatching info site](https://rekt05.github.io/hitman-downpatching/)

## Building it yourself

- Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- `dotnet run` for running it
- `.\build.ps1` for building the release exe to artifacts\win-x64
