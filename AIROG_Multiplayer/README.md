# ⚔ AIROG Multiplayer — Co-op Plugin

**Version:** 1.0.0  
**Requires:** BepInEx 5.x, AI Roguelite (Steam)

---

## Overview

AIROG Multiplayer turns AI Roguelite into a **co-op text RPG**. One player hosts their existing save game, and others join from the main menu. The AI is aware of every player's character and responds to all of their actions simultaneously — like a shared tabletop RPG session with an AI game master.

- ✅ No dedicated server required
- ✅ Works over LAN or the internet (port forward required for internet play)
- ✅ All players see the same story log in real time
- ✅ Each player submits their own actions each turn
- ✅ The AI weaves everyone's actions into a single narrative response
- ✅ No saves or mods required on the client side beyond this plugin

---

## Installation

### Both Host and All Clients Must Do This

1. Make sure **BepInEx 5.x** is installed in your AI Roguelite game folder.  
   *(If you can see a `BepInEx/plugins/` folder in your game directory, it's already installed.)*

2. Copy `AIROG_Multiplayer.dll` into:
   ```
   C:\Program Files (x86)\Steam\steamapps\common\AI Roguelike\BepInEx\plugins\AIROG_Multiplayer\
   ```
   Create the `AIROG_Multiplayer` subfolder if it doesn't exist.

3. Launch the game. You should see **"⚔ Multiplayer"** button on the main menu.

---

## How to Play — Step by Step

### 🖥️ The Host (the player who owns the game session)

The host runs the actual game. Your save file, world, and AI settings are used for everyone.

**Step 1 — Launch the game normally.**

**Step 2 — On the main menu, click `⚔ Multiplayer`.**

A dialog box will appear with the following fields:

| Field | Description |
|---|---|
| **Host IP Address** | *(Not needed for hosting — only used when joining)* |
| **Port** | The port your friends will connect to. Default: `7777`. Leave as-is unless you have a conflict. |
| **Your Character Name** | *(Not needed for hosting — your in-game character is used)* |
| **Your Class / Role** | *(Not needed for hosting)* |

**Step 3 — Click `Host Game`.**

The status bar will change to:
> ✔ Hosting on port 7777 — 0 player(s) connected.

**Step 4 — Share your IP address with your friends.**

- **For LAN play:** Share your local IP (e.g., `192.168.1.x`). Find it by running `ipconfig` in Command Prompt and looking for "IPv4 Address".
- **For internet play:** Share your **public IP** (search "what is my ip" in a browser). You will also need to **port forward port 7777 (TCP)** on your router to your PC's local IP.

**Step 5 — Load or start your game as normal.**

Once you are in-game, connected clients will be shown a welcome message and will start receiving your story turns live. When clients submit actions, you'll see a toast notification showing what they want to do.

**Step 6 — Play!**

The AI will automatically include your friends' characters in every prompt. When a client submits an action during your turn, it gets injected into the prompt so the AI responds to everyone at once.

---

### 🎮 The Client (players who are joining)

Clients do **not** need to load a save game. You join from the main menu and interact through the plugin's overlay HUD.

**Step 1 — Launch the game.**

You do not need to start or load any game. Stay on the main menu.

**Step 2 — On the main menu, click `⚔ Multiplayer`.**

**Step 3 — Fill in the connection fields:**

| Field | What to Enter |
|---|---|
| **Host IP Address** | The IP address your host gave you (e.g., `192.168.1.50` for LAN, or a public IP for internet) |
| **Port** | Whatever port the host is using. Default: `7777` |
| **Your Character Name** | The name of your character in this adventure |
| **Your Class / Role** | A short description of your character (e.g., `Rogue Archer`, `Battle Mage`, `Wandering Healer`) — this is what the AI will know about you |

> 💡 **Tip:** Be descriptive with your Class/Role. The AI uses exactly this text when writing your character into the story. `"Exiled knight seeking redemption"` gives the AI much more to work with than `"Warrior"`.

**Step 4 — Click `Join Game`.**

If the connection is successful, the lobby dialog will close automatically and the **Co-op HUD** will appear on the right side of your screen.

**Step 5 — The Co-op HUD.**

The HUD is your window into the shared game:

```
┌─────────────────────────────┐
│ ⚔ Co-op Session             │
│ Connected                   │
├─────────────────────────────┤
│ Party                       │
│ HostName (Warrior)          │
│   HP: [████████░░] 80/100   │
│   📍 The Whispering Ruins   │
│                             │
│ YourName (Rogue Archer)     │
│   HP: [██████████] 100/100  │
├─────────────────────────────┤
│ [Story log scrolls here...] │
│                             │
│ The group descends into     │
│ the darkened corridor...    │
├─────────────────────────────┤
│ [Type your action...     ]  │
│                    [▶ Act]  │
│ Press Enter to submit action│
├─────────────────────────────┤
│         Disconnect          │
└─────────────────────────────┘
```

**Step 6 — Submit actions.**

When the host is taking their turn, you can type your action in the input box at the bottom and press **Enter** or click **▶ Act**.

Your action is sent to the host and queued. When the host submits their turn, the AI will respond to **both** your action and the host's action together.

> 💡 **Example actions:**
> - `I draw my bow and look for weaknesses in the enemy's armor`
> - `I search the bookshelf for anything related to the missing merchant`
> - `I cast a healing spell on the wounded guard`

You will see a notification: **"✓ Action queued — waiting for host turn..."**

When the AI responds, the story text will appear in the HUD's log.

---

## Internet Play — Port Forwarding

For friends to connect over the internet (not local network), the **host** needs to forward a port:

1. Log into your router's admin panel (usually at `192.168.1.1` or `192.168.0.1` in a browser).
2. Find **Port Forwarding** (sometimes under "Advanced" or "NAT").
3. Add a rule:
   - **Protocol:** TCP
   - **External Port:** `7777` (or whatever port you set)
   - **Internal IP:** Your PC's local IP (run `ipconfig`, look for IPv4)
   - **Internal Port:** `7777`
4. Save and apply.
5. Tell your friends your **public IP** (visit [whatismyip.com](https://www.whatismyip.com/)).

> ⚠️ **Security note:** Port forwarding exposes that port on your network. Only do this when you actually want to play. You can disable the rule afterwards.

---

## Troubleshooting

### "Failed to connect" on client side
- Make sure the host has clicked **Host Game** before you try to join.
- Double-check the IP address and port — even a single wrong digit will fail.
- For internet play, confirm the host has port-forwarded correctly and is sharing their **public** IP (not local).
- Check if a firewall is blocking the connection. On Windows, you may need to allow the game through **Windows Defender Firewall** on the host PC.
  - Go to: *Windows Security → Firewall → Allow an app* → Add `AI Roguelite` or manually allow port 7777 TCP.

### Client connects but sees no story turns
- The host must be **in-game** (past the main menu, in a loaded save) for story turns to be relayed.
- Story turns are only sent when the host's AI generates new text. Wait for the host to take an action.

### Client actions don't appear in the AI response
- Actions are only injected on the **next** prompt after the client submits them.
- If the host submits before you do, your action will queue for the following turn.
- Make sure you submitted the action (you should see the "✓ Action queued" notification).

### The "⚔ Multiplayer" button doesn't appear on the main menu
- Confirm `AIROG_Multiplayer.dll` is in the correct plugins folder.
- Check `BepInEx/LogOutput.log` for any errors mentioning `AIROG_Multiplayer`.
- Make sure BepInEx itself is correctly installed (you should see `BepInEx/config/` and `BepInEx/plugins/` folders).

### Game crashes or freezes when hosting
- Check `BepInEx/LogOutput.log` for exception details.
- Try a different port (some ISPs/routers block 7777). Common alternatives: `7778`, `25565`, `27015`.

---

## Configuration

After the first launch, a config file is created at:
```
BepInEx/config/com.airog.multiplayer.cfg
```

| Setting | Default | Description |
|---|---|---|
| `Network.Port` | `7777` | Default port shown in the lobby dialog |
| `Network.LastIP` | `127.0.0.1` | IP pre-filled in the lobby dialog when joining |

The last-used IP and port are also remembered automatically between sessions.

---

## For Developers — How It Works

The plugin uses raw **TCP sockets** (no Mirror or Unity Netcode required) with a simple framing protocol:

```
[4-byte int32 LE: message length][UTF-8 JSON body]
```

**Message types:** `Hello`, `Welcome`, `Rejected`, `StoryTurn`, `CharacterUpdate`, `PartyUpdate`, `ActionRequest`, `ActionQueued`, `Chat`, `Ping`, `Pong`, `Disconnect`

**Key Harmony patches:**

| Patch | Effect |
|---|---|
| `MainMenu.Start` (Postfix) | Injects the ⚔ Multiplayer button |
| `GameplayManager.BuildPromptString` (Postfix) | Appends connected players' character descriptions and pending actions to every AI prompt |
| `GameLogView.LogText` (Postfix) | Broadcasts every new story turn to all clients |
| `GameplayManager.DoConvoTextFieldSubmission` (Prefix/Postfix) | Relays host's action text to clients; clears pending client actions after each turn |

**Threading model:** All networking runs on background threads. Callbacks to Unity/game code are queued via a `ConcurrentQueue<Action>` and drained on the Unity main thread in `Update()`.

---

## Changelog

### v1.0.0 (2026-02-25)
- Initial release
- TCP server/client networking with JSON framing
- Main menu multiplayer lobby panel
- Real-time story turn relay to all clients
- Client action injection into AI prompts
- Co-op HUD with story log, party list, action input
- Party update broadcasts after each turn
- OOC (out-of-character) chat support

---

*Built with ❤️ as a BepInEx plugin for AI Roguelite. Not affiliated with the game's developers.*
