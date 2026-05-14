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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Mods.HV.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.HV.Widgets.Logic
{
	/// <summary>
	/// 14-beat scripted demo panel. Each button:
	///   - Plays the matching commander MP3 from voice-service/commander_audio/
	///     (Process.Start with UseShellExecute hands off to the OS's default
	///      audio handler; recording's system-audio track picks it up).
	///   - Dispatches the equivalent game command(s) directly through the
	///     VoxBridge inbound queue so the action fires instantly without
	///     waiting for the agent's STT round-trip.
	///
	/// Drives a hands-off-keyboard recording: click N for beat N, hear the
	/// commander voice, watch the game obey.
	/// </summary>
	public class VoxDemoPanelLogic : ChromeLogic
	{
		const string AudioRoot = @"C:\dev\elevenhack\cursor\voice-service\commander_audio";
		const string XOAudioRoot = @"C:\dev\elevenhack\cursor\voice-service\xo_audio";

		// Approximate commander VO duration before the XO replies. Picked to
		// land after most commander lines (~1.5–2.5s) without leaving an
		// awkward gap. Tunable; per-beat overrides could be added later if a
		// specific commander line runs especially long.
		const int XOResponseDelayMs = 2000;

		readonly VoxBridge bridge;
		readonly List<Timer> pendingXOTimers = new List<Timer>();
		uint cmdSeq;

		struct Beat
		{
			public string Slug;
			public Action<VoxDemoPanelLogic> Send;
		}

		[ObjectCreator.UseCtor]
		public VoxDemoPanelLogic(Widget widget, World world)
		{
			bridge = world.WorldActor.TraitOrDefault<VoxBridge>();
			if (bridge == null)
				return;

			var beats = new Dictionary<int, Beat>
			{
				[1]  = new Beat { Slug = "01_status_report",  Send = l => { /* commentary-only beat */ } },
				[2]  = new Beat { Slug = "02_build_units",    Send = l => {
					l.Issue("build", "{\"unit\":\"mbt\",\"count\":10}");
					l.Issue("build", "{\"unit\":\"rocketeer\",\"count\":5}");
				} },
				[3]  = new Beat { Slug = "03_pan_to_enemy",   Send = l => l.Issue("pan_camera", "{\"target\":\"enemy_base\"}") },
				[4]  = new Beat { Slug = "04_airstrike",      Send = l => l.Issue("airstrike", "{\"target\":\"E5\"}") },
				[5]  = new Beat { Slug = "05_hold_position",  Send = l => l.Issue("station_army", "{\"target\":\"C3\",\"aggressive\":false}") },
				[6]  = new Beat { Slug = "06_focus_fire",     Send = l => l.Issue("focus_fire", "{\"target_label\":\"Bunker-1\"}") },
				[7]  = new Beat { Slug = "07_full_assault",   Send = l => l.Issue("assault", "{}") },
				[8]  = new Beat { Slug = "08_pull_back",      Send = l => l.Issue("defend", "{}") },
				[9]  = new Beat { Slug = "09_rally_d4",       Send = l => {
					l.Issue("build", "{\"unit\":\"mbt\",\"count\":10}");
					l.Issue("set_rally", "{\"target\":\"D4\"}");
				} },
				[10] = new Beat { Slug = "10_final_assault",  Send = l => l.Issue("assault", "{}") },
				[11] = new Beat { Slug = "11_set_up_base",    Send = l => {
					l.Issue("produce_structure", "{\"structure\":\"generator\"}");
					l.Issue("produce_structure", "{\"structure\":\"storage\"}");
					l.Issue("produce_structure", "{\"structure\":\"factory\"}");
					l.Issue("produce_structure", "{\"structure\":\"radar\"}");
				} },
				[12] = new Beat { Slug = "12_select_army",    Send = l => l.Issue("select_army", "{}") },
				[13] = new Beat { Slug = "13_show_grid",      Send = l => l.Issue("toggle_grid", "{\"visible\":true}") },
				[14] = new Beat { Slug = "14_pause_resume",   Send = l => l.Issue("meta_pause", "{}") },
			};

			foreach (var kv in beats)
			{
				var n = kv.Key;
				var beat = kv.Value;
				var btn = widget.GetOrNull<ButtonWidget>($"VOX_DEMO_BTN_{n:D2}");
				if (btn != null)
					btn.OnClick = () => Fire(beat);
			}
		}

		void Fire(Beat beat)
		{
			PlayAudio(AudioRoot, beat.Slug);
			beat.Send(this);
			ScheduleXOResponse(beat.Slug);
		}

		void PlayAudio(string root, string slug)
		{
			var path = Path.Combine(root, slug + ".mp3");
			if (!File.Exists(path))
			{
				Log.Write("debug", $"[VoxDemoPanel] missing audio: {path}");
				return;
			}
			try
			{
				var psi = new ProcessStartInfo
				{
					FileName = path,
					UseShellExecute = true,
					CreateNoWindow = true,
				};
				Process.Start(psi);
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"[VoxDemoPanel] audio play failed for {slug}: {ex.Message}");
			}
		}

		// Plays the matching XO response MP3 after a fixed delay. Without
		// this the live agent never hears the demo button audio (it bypasses
		// the mic), so the recording would have commander lines with no XO
		// reply. Pre-canned XO audio uses the same Adam voice ID as the live
		// agent, so a viewer can't tell the difference.
		void ScheduleXOResponse(string slug)
		{
			var xoPath = Path.Combine(XOAudioRoot, slug + ".mp3");
			if (!File.Exists(xoPath))
			{
				Log.Write("debug", $"[VoxDemoPanel] missing XO audio: {xoPath}");
				return;
			}

			Timer t = null;
			t = new Timer(_ =>
			{
				PlayAudio(XOAudioRoot, slug);
				try { t?.Dispose(); } catch { }
				lock (pendingXOTimers) { pendingXOTimers.Remove(t); }
			}, null, XOResponseDelayMs, System.Threading.Timeout.Infinite);
			lock (pendingXOTimers) { pendingXOTimers.Add(t); }
		}

		void Issue(string intent, string args)
		{
			cmdSeq++;
			var json = $"{{\"type\":\"command\",\"id\":\"demo-{cmdSeq}\",\"intent\":\"{intent}\",\"args\":{args}}}";
			bridge.EnqueueRawCommand(json);
		}
	}
}
