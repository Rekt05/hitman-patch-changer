# Hitman WoA Patch Changer

An app used for switching between versions of Hitman without installing the game from scratch. It uses the [DepotDownloader](https://github.com/SteamRE/DepotDownloader) to determine which game files have changed, and then installs them.

Currently this only works for Steam on both Windows and Linux.

You must own the game on the Steam account you sign in with.

## Downloading

Download the latest zip from Releases and extract it.

- Windows: double click `HitmanPatchChanger.exe`, this opens a command prompt window (do not close this until you are done)
- Linux: in a terminal in that folder, run `chmod +x HitmanPatchChanger` and then `./HitmanPatchChanger` (do not close this terminal until you are done)

This should open a tab in your browser, if it does not, follow the link at the top of the command prompt or terminal.

## How to use (already downpatched)

1. Set an install directory, this is the location where you have the downpatched game installed.
2. Set a DepotDownloader directory, this is the folder that contains DepotDownloader (`DepotDownloader.exe` on Windows, `DepotDownloader` on Linux) and continue to the next step, or choose an empty folder, then click Install.
3. Sign in with your Steam username + password, or with the Steam QR code, and follow the steps on screen.
4. Click Switch on a version, or paste in your own manifest ID and click Switch to custom, then confirm.
5. After a successful switch, run the HITMAN3.exe in the Retail folder of the game to play the game. You can also add this .exe as a non-steam game, and just use that for any future changes. If you have already set it up as a non-steam game beforehand, you can continue using that.

The first switch will take longer than later ones as DepotDownloader stores the last manifest in a `.DepotDownloader` folder inside the install directory, and later switches only download the files that changed. If that folder is missing and the game files are already there, every file needs to be checksummed first.

## How to use (no downpatch yet)

1. Set an install directory using one of these 2 options:
    - Faster: Make a new folder and copy your game files into it, then set that folder as the install directory. For this, in Steam, right click the game, Manage, Browse local files, and copy everything in that folder into the new one. That folder should have Retail, Runtime, and Launcher.exe. Leave the original steam one as is if you want to keep your original install.
    - Slower: Set a new empty folder, the game will be installed there from scratch.
2. Set a DepotDownloader directory, this is the folder that contains DepotDownloader (`DepotDownloader.exe` on Windows, `DepotDownloader` on Linux) and continue to the next step, or choose an empty folder, then click Install.
3. Sign in with your Steam username + password, or with the Steam QR code, and follow the steps on screen.
4. Click Switch on a version, or paste in your own manifest ID and click Switch to custom, then confirm.
5. After a successful switch, run the HITMAN3.exe in the Retail folder of the game to play the game. You can also add this .exe as a non-steam game, and just use that for any future changes.

The first switch takes longer than later ones as a copied install is checksummed first because `.DepotDownloader` does not exist yet, and an empty folder needs to download the whole game. Later switches will only download the files that changed as long as the `.DepotDownloader` folder is still in the install folder.

## Extra Info

For any extra info (Notable Patches, ZHM compatibility) on downpatching refer to the [downpatching info site](https://rekt05.github.io/hitman-downpatching/)

## Building it yourself

- Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- `dotnet run` for running it
- Windows: `.\build.ps1` writes the release exe to `artifacts\win-x64`
- Linux: `./build.sh` writes the release binary to `artifacts/linux-x64` (or `linux-arm64` / `linux-arm`)
