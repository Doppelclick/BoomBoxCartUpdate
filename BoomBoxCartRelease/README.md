# BoomboxCartUpgrade 

Adds a "Boombox" component into the cart with its own UI that plays video links as audio from the cart. 
Has volume and quality sliders as well to configure the music to your liking. One player can control the 
Boombox UI at any time, and everyone (with the mod installed) can hear the songs you play too!

Current websites you can play audio from:
 - Youtube (Music)
 - SoundCloud
 - Rutube
 - music.yandex
 - Bilibili

Reach out to me (link at the end) for issues with any service listed here, or suggestions on adding new ones from <a href="https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md">these sites</a>

## Usage

<p>Everyone in the lobby needs the mod to be able to hear the boombox. However, if someone does not have the mod, you will still be able to play together.</p>
<p>Incompatible with the original BoomBox mod. May cause crashes.</p>

<p>How to use mod:</p>
<ol>
    <li>Grab on to a cart</li>
    <li>Press 'Y' on your keyboard (note: 'Y' is the default key - can be changed. Only one person can have the UI open at a time)</li>
    <li>Paste in a video link for the music/video you want to play</li>
    <li>Press 'Play', wait a moment for the video to download (longer videos may take longer), and audio should start playing!</li>
    <li>To adjust config options ingame use something like <a href="https://thunderstore.io/c/repo/p/nickklmao/REPOConfig/">REPOconfig</a></li>
</ol>
<p>Known Issues:</p>
<ul>
    <li>When the menu is opened you can still move and click through it</li>
    <li>A lot of browsers will not work for the downloader
    <li>Singleplayer does NOT work at all. Clicking "Host Game" and playing solo works though!</li>
</ul>

<p>Possible Future Features:</p>
<ul>
    <li>Suggest some maybe!</li>
</ul>

## Features
- Supports timestamps from YouTube (music) and Soundcloud embedded in the link. (Also has a config setting)
- Cookie passthrough
- Some cool visual effects: RGB underglow and a frequency visualiser

## Issues

### No audio
- Causes
    - Pressing the mute button (default key "M")
    - Outdated mod, or different version to the lobby host
    - The audio service you are downloading from does not support your region
    - An internal error of ffmpeg or yt-dl (dependencies)
    - Error when downloading dependencies

- Fixes
    - If you can view the game logs, they might provide some helpful information, e.g. a dependency download error, region block, etc.
    - Delete the BoomboxedCart located here:
    ../Steamlibrary/steamapps/common/REPO/**BoomboxedCart**
    or enable the flag in the config file (BepInEx/config/Doppelclick.BoomboxCartUpgrade.cfg - Debug - ReinstallResources: true) and launch the game
    - If your region is blocked, use a VPN server in e.g. America or Central Europe, or view the fixes for "Sign in to confirm you're not a bot"

### "Sign in to confirm you're not a bot"
- Causes
    - YouTube does not like your ip :(
    - You have been flagged for "suspicious" activity

- Fixes
    - Use a different streaming service, e.g. Soundcloud - This is the easiest and most reliable fix
    - Head to the mod's settings and select a browser for Downloader - Browser: (NONE, chrome, firefox, edge, brave, safari)
    Please note: This will most likely not work, as a lot of browsers do not store your cookies in plain text anymore.
    - Alternatively: Extract your cookies to a file using an extension such as <a href="https://chromewebstore.google.com/detail/get-cookiestxt-locally/cclelndahbckbenkjhflpdbgdldlbecc">this</a>.
    Then paste the path to the cookies file into Downloader - CustomCookiePath, such as C:\Users\Me\Downloads\cookies.txt
    - Please note. It is highly recommended to use a youtube account that is not your main for this, although it is highly unlikely, you may get it banned for botting. (Be signed into an alt account when extracting cookies, or while using the Browser)

- To enable the console window, head to your mod config directory, into the BepInEx config file and enable the console (BepInEx/config/BepInEx.cfg / Logging.Console / Enabled)
- Ideally also turn on log level Debug.

### No fix found
Launch the game with the console enabled and send the log into the <a href="https://discord.com/channels/1344557689979670578/1439361913933795489">mod thread</a> on the <a href="https://discord.com/channels/1344557689979670578/1439361913933795489">REPO Modding discord server</a>

## Credits
<p>A HUGE thanks to @survivalq and their <a href="https://github.com/survivalq/SemiBoombox">SemiBoombox Mod</a> for critical funcionality for parts of this mod. If you're looking to make your own boombox mod or just play audio from a third-party, check their mod out, it is much better code!!!

Big thanks to all the members in the <a href="https://discord.gg/EYAnUPV7kX">R.E.P.O Modding Server</a> that report bugs and help me test new versions! This includes but not limited to: <a href="https://thunderstore.io/c/repo/p/SteamBlizzard/">Dan</a>, Dreepye, Vehzx, and Wiz!

Also big thanks to <a href="https://thunderstore.io/c/repo/p/SteamBlizzard/">Dan</a> for the incredible cover art. Go download his mods too!</p>



### This mod is based on PhilTec-Philip's BoomboxCartUpdate fork of the original BoomBoxCartMod by ColtG5

## Contact

Feel free to reach out to me on in the repo modding community discord (https://discord.com/invite/vPJtKhYAFe) in this thread: https://discord.com/channels/1344557689979670578/1439361913933795489