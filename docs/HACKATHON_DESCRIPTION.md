Vox Commander is a real-time strategy game you play with your voice.
No keyboard, no mouse — just spoken commands.

We picked the hardest possible target on purpose. RTS games are the
most input-dense genre on PC: pro players hit 300+ actions per minute,
every second a flood of clicks, hotkeys, control groups, and menus.
That input wall is exactly why the entire genre is closed off to
anyone with a motor impairment, RSI, low vision, a broken arm, or
just their hands full. If voice can replace mouse + keyboard here,
voice can replace them anywhere.

The system is three layers. ElevenLabs Conversational AI owns the
voice — streaming STT, intent decision, and the XO persona that
confirms every order in sub-second turn-taking. A Python tool host
exposes ~25 typed intents (focus_fire, set_rally, hold_position,
station_army, airstrike, toggle_grid, …) the agent can call. A custom
C# trait inside OpenHV translates those intents into the same native
game orders a mouse click would issue — no cheating, no engine forks.

The submission demo runs a full skirmish — base construction, economy,
scouting, focused fire on named targets, airstrikes, tactical pull-back,
final assault — entirely by voice. Built in Cursor over four days,
~70 commits, every line shipped through Claude Code.
