#region Copyright & License Information
/*
 * Copyright 2026 The Vox Commander Contributors
 * This file is part of OpenHV, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Mods.HV.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.HV.Widgets.Logic
{
	/// <summary>
	/// Wires the in-game Vox Commander side panel. Every button forwards a
	/// pre-built JSON command line through VoxBridge.EnqueueRawCommand so it
	/// dispatches through the same path as TCP / voice tool calls.
	/// </summary>
	public class VoxPanelLogic : ChromeLogic
	{
		// Project root that holds vox-commander.bat and .env. Hardcoded for the
		// hackathon demo — fine because the trait already hardcodes the same
		// path for its auto-spawn.
		const string ProjectRoot = @"C:\dev\elevenhack\cursor";

		readonly VoxBridge bridge;
		Process voiceProc;
		ButtonWidget toggleBtn;
		LabelWidget statusLbl;
		TextFieldWidget keyField;
		uint cmdSeq;

		[ObjectCreator.UseCtor]
		public VoxPanelLogic(Widget widget, World world)
		{
			bridge = world.WorldActor.TraitOrDefault<VoxBridge>();
			if (bridge == null)
				return;

			// Standard opening: pure-economy build chain. Combat composition is
			// left to the player ("build 10 tanks", "build 5 rifles" etc.) so the
			// opening doesn't blow the starting cash on speculative units.
			//
			// 1. generator  — power; without it production runs at LowPowerModifier speed
			// 2. storage    — refinery + spawns 1 free miner (per HV encyclopedia text)
			// 3. module     — produces pods (rifleman / sniper / mortar / etc.)
			// 4. factory    — produces vehicles (mbt / aatank / artillery / etc.)
			// 5. radar      — vision + unlocks aircraft tech
			// 6. +3 miners  — saturates one storage (1 free + 3 trained = 4 active)
			var standardOpening = new (string Intent, string Args)[]
			{
				("produce_structure", "{\"structure\":\"generator\"}"),
				("produce_structure", "{\"structure\":\"storage\"}"),
				("produce_structure", "{\"structure\":\"module\"}"),
				("produce_structure", "{\"structure\":\"factory\"}"),
				("produce_structure", "{\"structure\":\"radar\"}"),
				("build",             "{\"unit\":\"miner\",\"count\":3}"),
			};

			Wire(widget, "VOX_BTN_OPENING",       () => RunCombo(standardOpening));
			Wire(widget, "VOX_BTN_SELECT_ARMY",   () => Issue("select_army", "{}"));
			Wire(widget, "VOX_BTN_SCOUT",         () => Issue("scout", "{}"));
			Wire(widget, "VOX_BTN_HARASS",        () => Issue("harass", "{}"));
			Wire(widget, "VOX_BTN_ASSAULT",       () => Issue("assault", "{}"));
			Wire(widget, "VOX_BTN_DEFEND",        () => Issue("defend", "{}"));
			Wire(widget, "VOX_BTN_TOGGLE_GRID",   () => Issue("toggle_grid", "{}"));
			Wire(widget, "VOX_BTN_TOGGLE_LABELS", () => Issue("toggle_labels", "{}"));
			Wire(widget, "VOX_BTN_PAUSE",         () => Issue("meta_pause", "{}"));
			Wire(widget, "VOX_BTN_BUILD_TANKS",   () => Issue("build", "{\"unit\":\"mbt\",\"count\":5}"));
			Wire(widget, "VOX_BTN_BUILD_RIFLES",  () => Issue("build", "{\"unit\":\"rifleman\",\"count\":3}"));
			Wire(widget, "VOX_BTN_BUILD_MINER",   () => Issue("build", "{\"unit\":\"miner\",\"count\":1}"));
			Wire(widget, "VOX_BTN_ARMY_B4",       () => Issue("station_army", "{\"target\":\"B4\"}"));
			Wire(widget, "VOX_BTN_ARMY_MIDPOINT", () => Issue("station_army", "{\"target\":\"midpoint\"}"));
			Wire(widget, "VOX_BTN_HOLD_POSITION", () => Issue("hold_position", "{}"));
			Wire(widget, "VOX_BTN_RALLY_B4",      () => Issue("set_rally", "{\"target\":\"B4\"}"));
			Wire(widget, "VOX_BTN_RALLY_MIDPOINT",() => Issue("set_rally", "{\"target\":\"midpoint\"}"));

			// ----- voice service controls (API key + activate toggle) -----
			keyField = widget.GetOrNull<TextFieldWidget>("VOX_FIELD_APIKEY");
			toggleBtn = widget.GetOrNull<ButtonWidget>("VOX_BTN_VOICE_TOGGLE");
			statusLbl = widget.GetOrNull<LabelWidget>("VOX_LBL_VOICE_STATUS");

			var saveBtn = widget.GetOrNull<ButtonWidget>("VOX_BTN_SAVE_KEY");
			if (saveBtn != null)
				saveBtn.OnClick = OnSaveKey;

			if (toggleBtn != null)
				toggleBtn.OnClick = OnToggleVoice;

			SetStatus("Voice service idle");
		}

		void OnSaveKey()
		{
			if (keyField == null) return;
			var key = (keyField.Text ?? "").Trim();
			if (string.IsNullOrEmpty(key))
			{
				SetStatus("Enter a key first");
				return;
			}

			var envPath = Path.Combine(ProjectRoot, ".env");
			var text = File.Exists(envPath) ? File.ReadAllText(envPath) : "";
			const string keyName = "ELEVENLABS_API_KEY";
			if (Regex.IsMatch(text, $"(?m)^{keyName}=.*$"))
				text = Regex.Replace(text, $"(?m)^{keyName}=.*$", $"{keyName}={key}");
			else
				text += (text.EndsWith("\n") || text.Length == 0 ? "" : "\n") + $"{keyName}={key}\n";

			try
			{
				File.WriteAllText(envPath, text);
				keyField.Text = "";
				SetStatus("API key saved");
			}
			catch (Exception ex)
			{
				SetStatus($"Save failed: {ex.Message}");
			}
		}

		void OnToggleVoice()
		{
			if (voiceProc != null && !voiceProc.HasExited)
			{
				try { voiceProc.Kill(); } catch { }
				voiceProc = null;
				if (toggleBtn != null) toggleBtn.Text = "ACTIVATE VOICE";
				SetStatus("Voice service stopped");
				return;
			}

			var python = Path.Combine(ProjectRoot, "voice-service", ".venv", "Scripts", "python.exe");
			var workdir = Path.Combine(ProjectRoot, "voice-service");
			if (!File.Exists(python))
			{
				SetStatus($"Python venv not found: {python}");
				return;
			}

			try
			{
				var psi = new ProcessStartInfo
				{
					FileName = python,
					Arguments = "-u -m vox.main",
					UseShellExecute = true,
					CreateNoWindow = false,
					WorkingDirectory = workdir,
				};
				// Keep the console visible so the operator can see transcripts / errors
				// during the demo. Hide it later by setting CreateNoWindow + UseShellExecute=false.
				voiceProc = Process.Start(psi);
				if (toggleBtn != null) toggleBtn.Text = "DEACTIVATE VOICE";
				SetStatus("Voice live — speak to the XO");
			}
			catch (Exception ex)
			{
				SetStatus($"Spawn failed: {ex.Message}");
			}
		}

		void SetStatus(string text)
		{
			if (statusLbl != null)
				statusLbl.Text = text;
		}

		static void Wire(Widget parent, string id, Action onClick)
		{
			var btn = parent.GetOrNull<ButtonWidget>(id);
			if (btn != null)
				btn.OnClick = onClick;
		}

		void RunCombo((string Intent, string Args)[] steps)
		{
			foreach (var (intent, args) in steps)
				Issue(intent, args);
		}

		void Issue(string intent, string args)
		{
			cmdSeq++;
			var json = $"{{\"type\":\"command\",\"id\":\"ui-{cmdSeq}\",\"intent\":\"{intent}\",\"args\":{args}}}";
			bridge.EnqueueRawCommand(json);
		}
	}
}
