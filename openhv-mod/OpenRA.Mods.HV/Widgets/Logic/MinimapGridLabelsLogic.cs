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

using OpenRA.Mods.Common.Widgets;
using OpenRA.Mods.HV.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.HV.Widgets.Logic
{
	/// <summary>
	/// Generates Battleship-style axis labels overlaid on the minimap. Reads
	/// column/row counts from the live VoxGridManager so changing the grid
	/// dimensions in world.yaml automatically updates the labels here. Also
	/// reads the minimap widget's actual Bounds so a resized minimap (in
	/// chrome) doesn't require constants here.
	///
	/// Attach via comma-separated Logic on Container@RADAR:
	///   Logic: IngameRadarDisplayLogic, MinimapGridLabelsLogic
	/// </summary>
	public class MinimapGridLabelsLogic : ChromeLogic
	{
		[ObjectCreator.UseCtor]
		public MinimapGridLabelsLogic(Widget widget, World world)
		{
			var mgr = world.WorldActor.TraitOrDefault<VoxGridManager>();
			if (mgr == null) return;

			var radar = widget.GetOrNull("RADAR_MINIMAP");
			if (radar == null) return;

			var mmX = radar.Bounds.X;
			var mmY = radar.Bounds.Y;
			var mmW = radar.Bounds.Width;
			var mmH = radar.Bounds.Height;

			var stepX = mmW / mgr.Columns;
			var stepY = mmH / mgr.Rows;
			const int LabelSize = 12;

			// Column letters across the top edge of the minimap.
			for (var c = 0; c < mgr.Columns; c++)
			{
				var label = new LabelWidget(Game.ModData)
				{
					Text = ((char)('A' + c)).ToString(),
					Font = "TinyBold",
					Align = TextAlign.Center,
					Contrast = true,
				};
				label.Bounds = new WidgetBounds(
					mmX + c * stepX + stepX / 2 - LabelSize / 2,
					mmY + 1,
					LabelSize, LabelSize);
				widget.AddChild(label);
			}

			// Row digits down the left edge of the minimap.
			for (var r = 0; r < mgr.Rows; r++)
			{
				var label = new LabelWidget(Game.ModData)
				{
					Text = (r + 1).ToString(),
					Font = "TinyBold",
					Align = TextAlign.Center,
					Contrast = true,
				};
				label.Bounds = new WidgetBounds(
					mmX + 1,
					mmY + r * stepY + stepY / 2 - LabelSize / 2,
					LabelSize, LabelSize);
				widget.AddChild(label);
			}
		}
	}
}
