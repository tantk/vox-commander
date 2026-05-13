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
	// World-scoped manager: holds the on/off toggle plus a per-kind counter
	// used to mint short, speakable names like "Tank-3" or "Miner-A".
	// ----------------------------------------------------------------------

	[TraitLocation(SystemActors.World)]
	[Desc("Holds Vox Commander label visibility + per-kind counter. Attach to the world actor.")]
	public class VoxLabelManagerInfo : TraitInfo<VoxLabelManager> { }

	public class VoxLabelManager
	{
		public bool Enabled { get; set; } = true;
		readonly Dictionary<string, int> counters = new Dictionary<string, int>();

		public string AssignName(Actor self)
		{
			var prefix = PrefixFor(self.Info.Name);
			counters.TryGetValue(prefix, out var n);
			n++;
			counters[prefix] = n;
			return $"{prefix}-{n}";
		}

		static string PrefixFor(string actorName)
		{
			switch (actorName.ToLowerInvariant())
			{
				case "mbt": return "Tank";
				case "aatank": return "AA";
				case "apc": return "APC";
				case "artillery": return "Arty";
				case "rifleman": return "Rifle";
				case "rocketeer": return "Rocket";
				case "mortar": return "Mortar";
				case "sniper": return "Sniper";
				case "flamer": return "Flame";
				case "technician": return "Tech";
				case "jetpacker": return "Jet";
				case "miner": return "Miner";
				case "miner2": return "Tower";
				case "tanker1": return "Tanker";
				case "builder": return "Builder";
				case "collector": return "Coll";
				case "generator": return "Power";
				case "storage": return "Store";
				case "factory": return "Factory";
				case "outpost": return "Outpost";
				case "outpost2": return "Outpost";
				case "base": return "Base";
				case "base2": return "Base";
				case "radar": return "Radar";
				case "radar2": return "Radar";
				case "module": return "Module";
				case "module2": return "Module";
				case "bunker": return "Bunker";
				case "turret": return "Turret";
				case "aaturret": return "AA-T";
				default:
					if (string.IsNullOrEmpty(actorName)) return "Unit";
					return char.ToUpperInvariant(actorName[0]) + actorName.Substring(1).ToLowerInvariant();
			}
		}
	}

	// ----------------------------------------------------------------------
	// Per-actor trait: renders the actor's assigned friendly name below it
	// when the manager toggle is on and the actor is owned by the local player.
	// ----------------------------------------------------------------------

	[Desc("Renders a small friendly-name label below the actor for Vox Commander " +
		"micromanagement. Visibility is gated by the world-level VoxLabelManager toggle.")]
	public class WithVoxLabelInfo : TraitInfo
	{
		[Desc("Color for the commander's own units.")]
		public readonly Color OwnedColor = Color.FromArgb(0xFFE8EDF3);

		[Desc("Color for enemy units (so the commander can target by label).")]
		public readonly Color EnemyColor = Color.FromArgb(0xFFFF6B6B);

		[Desc("World-space offset to place the label below the actor's center.")]
		public readonly WVec Offset = new WVec(0, 700, 0);

		[Desc("Font key from the renderer's font registry.")]
		public readonly string Font = "TinyBold";

		public override object Create(ActorInitializer init) => new WithVoxLabel(this);
	}

	public class WithVoxLabel : IRenderAnnotations, INotifyCreated
	{
		readonly WithVoxLabelInfo info;
		public string Name { get; private set; }
		SpriteFont font;

		public WithVoxLabel(WithVoxLabelInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			var mgr = self.World.WorldActor.TraitOrDefault<VoxLabelManager>();
			if (mgr != null)
				Name = mgr.AssignName(self);
		}

		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			if (string.IsNullOrEmpty(Name))
				yield break;

			var mgr = self.World.WorldActor.TraitOrDefault<VoxLabelManager>();
			if (mgr == null || !mgr.Enabled)
				yield break;

			if (self.IsDead || !self.IsInWorld)
				yield break;

			if (self.World.LocalPlayer == null)
				yield break;

			// Render labels for our units AND visible enemies (so commander can
			// say things like "focus Bunker-1"). Skip neutrals/critters.
			var owner = self.Owner;
			if (owner.NonCombatant)
				yield break;

			var isFriendly = owner == self.World.LocalPlayer;
			var color = isFriendly ? info.OwnedColor : info.EnemyColor;

			font ??= Game.Renderer.Fonts[info.Font];

			yield return new TextAnnotationRenderable(font, self.CenterPosition + info.Offset, 0, color, Name);
		}

		bool IRenderAnnotations.SpatiallyPartitionable => true;
	}
}
