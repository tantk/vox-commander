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
		readonly VoxBridge bridge;
		uint cmdSeq;

		[ObjectCreator.UseCtor]
		public VoxPanelLogic(Widget widget, World world)
		{
			bridge = world.WorldActor.TraitOrDefault<VoxBridge>();
			if (bridge == null)
				return;

			// Combos: list of (intent, jsonArgs) pairs we fire sequentially.
			var standardOpening = new (string Intent, string Args)[]
			{
				("produce_structure", "{\"structure\":\"generator\"}"),
				("produce_structure", "{\"structure\":\"storage\"}"),
				("produce_structure", "{\"structure\":\"factory\"}"),
				("produce_structure", "{\"structure\":\"radar\"}"),
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
