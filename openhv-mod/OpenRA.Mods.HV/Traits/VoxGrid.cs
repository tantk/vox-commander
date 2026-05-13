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

using System.Collections.Generic;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.HV.Traits
{
	// ----------------------------------------------------------------------
	// World-scoped manager: holds the on/off toggle and resolves Battleship-
	// style labels ("A1".."F6") into world CPos positions for voice/button
	// commands like `station_army target="B4"`.
	// ----------------------------------------------------------------------

	[TraitLocation(SystemActors.World)]
	[Desc("Voice grid overlay manager. Attach to the world actor.")]
	public class VoxGridManagerInfo : TraitInfo
	{
		[Desc("Number of columns in the grid (A, B, C, ...).")]
		public readonly int Columns = 6;

		[Desc("Number of rows in the grid (1, 2, 3, ...).")]
		public readonly int Rows = 6;

		[Desc("Whether the grid is visible at world load.")]
		public readonly bool DefaultEnabled = false;

		public override object Create(ActorInitializer init) => new VoxGridManager(this);
	}

	public class VoxGridManager
	{
		readonly VoxGridManagerInfo info;
		public bool Enabled { get; set; }
		public int Columns => info.Columns;
		public int Rows => info.Rows;

		public VoxGridManager(VoxGridManagerInfo info)
		{
			this.info = info;
			Enabled = info.DefaultEnabled;
		}

		public CPos? ResolveLabel(string label, World world)
		{
			if (string.IsNullOrEmpty(label) || label.Length < 2) return null;
			var c = char.ToUpperInvariant(label[0]);
			if (c < 'A' || c >= 'A' + Columns) return null;
			if (!int.TryParse(label.Substring(1), out var r)) return null;
			if (r < 1 || r > Rows) return null;

			var col = c - 'A';
			var row = r - 1;
			var b = world.Map.Bounds;
			var stepX = b.Width / Columns;
			var stepY = b.Height / Rows;
			return new CPos(b.Left + col * stepX + stepX / 2, b.Top + row * stepY + stepY / 2);
		}

		public IEnumerable<(CPos Center, string Label)> AllCells(World world)
		{
			var b = world.Map.Bounds;
			var stepX = b.Width / Columns;
			var stepY = b.Height / Rows;
			for (var r = 0; r < Rows; r++)
			{
				for (var c = 0; c < Columns; c++)
				{
					var center = new CPos(b.Left + c * stepX + stepX / 2, b.Top + r * stepY + stepY / 2);
					var label = $"{(char)('A' + c)}{r + 1}";
					yield return (center, label);
				}
			}
		}
	}

	// ----------------------------------------------------------------------
	// World-scoped renderer: paints all grid labels on screen when the
	// manager toggle is on. Cheap — 36 TextAnnotationRenderable yields per
	// frame, all clipped by the engine's spatial culling.
	// ----------------------------------------------------------------------

	[TraitLocation(SystemActors.World)]
	[Desc("Renders the Battleship grid overlay. Attach to the world actor.")]
	public class VoxGridOverlayInfo : TraitInfo
	{
		[Desc("Color of the grid labels (ARGB).")]
		public readonly Color Color = Color.FromArgb(0xCCFFFFFF);

		[Desc("Font key. Bold is much more legible at zoom-out than TinyBold.")]
		public readonly string Font = "Bold";

		public override object Create(ActorInitializer init) => new VoxGridOverlay(this);
	}

	public class VoxGridOverlay : IRenderAnnotations
	{
		readonly VoxGridOverlayInfo info;
		SpriteFont font;

		public VoxGridOverlay(VoxGridOverlayInfo info)
		{
			this.info = info;
		}

		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			var mgr = self.World.WorldActor.TraitOrDefault<VoxGridManager>();
			if (mgr == null || !mgr.Enabled)
				yield break;

			font ??= Game.Renderer.Fonts[info.Font];
			var map = self.World.Map;

			foreach (var (cell, label) in mgr.AllCells(self.World))
			{
				var pos = map.CenterOfCell(cell);
				yield return new TextAnnotationRenderable(font, pos, 0, info.Color, label);
			}
		}

		bool IRenderAnnotations.SpatiallyPartitionable => false;
	}
}
