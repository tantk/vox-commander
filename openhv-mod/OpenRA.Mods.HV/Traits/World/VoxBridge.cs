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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.HV.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Localhost TCP bridge for external voice control. Attach to the world actor.")]
	public class VoxBridgeInfo : TraitInfo
	{
		[Desc("TCP port to listen on for command JSON lines.")]
		public readonly int Port = 47777;

		[Desc("Number of fallback ports to try if the configured port is unavailable.")]
		public readonly int PortFallbackCount = 5;

		[Desc("Ticks between state snapshots emitted to the client (default 50 = 2s at 25Hz).")]
		public readonly int SnapshotInterval = 50;

		[Desc("Absolute or relative path to the voice-panel launcher script that spawns when a real match starts. " +
			"Empty disables auto-spawn (use the panel manually).")]
		public readonly string PanelLauncher = "C:/dev/elevenhack/cursor/vox-commander.bat";

		public override object Create(ActorInitializer init) { return new VoxBridge(this); }
	}

	public class VoxBridge : IWorldLoaded, ITick, INotifyActorDisposing
	{
		readonly VoxBridgeInfo info;
		readonly ConcurrentQueue<string> inbound = new ConcurrentQueue<string>();
		readonly object writerLock = new object();

		TcpListener listener;
		TcpClient activeClient;
		StreamWriter activeWriter;
		CancellationTokenSource cts;
		World world;

		int tickCount;
		int prevFriendlyUnits = -1;
		bool startEmitted;
		readonly HashSet<uint> autoMineHandled = new HashSet<uint>();

		public VoxBridge(VoxBridgeInfo info) { this.info = info; }

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			world = w;

			// Don't bind during shell map (the main-menu background scene) or map editor.
			if (w.Type != WorldType.Regular)
			{
				Log.Write("debug", $"[VoxBridge] skipping bind: world.Type={w.Type}");
				return;
			}

			cts = new CancellationTokenSource();

			int boundPort = -1;
			for (var i = 0; i <= info.PortFallbackCount; i++)
			{
				var attempt = info.Port + i;
				try
				{
					listener = new TcpListener(IPAddress.Loopback, attempt);
					listener.Start();
					boundPort = attempt;
					break;
				}
				catch (SocketException ex)
				{
					Log.Write("debug", $"[VoxBridge] port {attempt} unavailable ({ex.SocketErrorCode}); trying next");
					listener = null;
				}
			}

			if (listener == null)
			{
				Log.Write("debug", $"[VoxBridge] FAILED to bind any port in [{info.Port}..{info.Port + info.PortFallbackCount}] — bridge disabled this match");
				return;
			}

			Log.Write("debug", $"[VoxBridge] listening on 127.0.0.1:{boundPort}");
			_ = Task.Run(() => AcceptLoopAsync(cts.Token));

			TrySpawnPanel();
		}

		void TrySpawnPanel()
		{
			if (string.IsNullOrWhiteSpace(info.PanelLauncher))
			{
				Log.Write("debug", "[VoxBridge] panel auto-spawn disabled (PanelLauncher empty)");
				return;
			}

			var path = info.PanelLauncher;
			if (!File.Exists(path))
			{
				Log.Write("debug", $"[VoxBridge] panel launcher not found at {path}; skipping auto-spawn");
				return;
			}

			try
			{
				var psi = new ProcessStartInfo
				{
					FileName = path,
					UseShellExecute = true,
					CreateNoWindow = false,
					WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
				};
				Process.Start(psi);
				Log.Write("debug", $"[VoxBridge] launched voice panel: {path}");
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"[VoxBridge] panel launch failed: {ex.Message}");
			}
		}

		async Task AcceptLoopAsync(CancellationToken ct)
		{
			while (!ct.IsCancellationRequested)
			{
				TcpClient client;
				try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
				catch (OperationCanceledException) { return; }
				catch (ObjectDisposedException) { return; }

				lock (writerLock)
				{
					activeClient = client;
					activeWriter = new StreamWriter(client.GetStream(), new UTF8Encoding(false))
					{
						AutoFlush = true,
						NewLine = "\n",
					};
				}
				Log.Write("debug", "[VoxBridge] client connected");
				_ = Task.Run(() => ReadLoopAsync(client, ct));
			}
		}

		async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
		{
			try
			{
				using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
				while (!ct.IsCancellationRequested)
				{
					var line = await reader.ReadLineAsync().ConfigureAwait(false);
					if (line == null) break;
					inbound.Enqueue(line);
				}
			}
			catch
			{
				/* client dropped */
			}
			finally
			{
				Log.Write("debug", "[VoxBridge] client disconnected");
			}
		}

		public void Tick(Actor self)
		{
			if (listener == null) return;

			if (tickCount == 0)
				Log.Write("debug", "[VoxBridge] first tick fired");

			if (!startEmitted && activeWriter != null)
			{
				Emit($"{{\"type\":\"event\",\"kind\":\"match_start\",\"ts\":{Game.LocalTick}}}");
				startEmitted = true;
				Log.Write("debug", "[VoxBridge] emitted match_start");
			}

			while (inbound.TryDequeue(out var line))
			{
				Log.Write("debug", $"[VoxBridge] in: {line}");
				var cmd = TryParse(line);
				if (cmd == null)
				{
					Log.Write("debug", "[VoxBridge] parse failed");
					continue;
				}

				try
				{
					var result = Dispatch(cmd);
					Log.Write("debug", $"[VoxBridge] dispatched {cmd.Intent} -> ok={result.ok} err={result.error ?? "<null>"}");
					SendAck(cmd.Id, result.ok, result.error);
				}
				catch (Exception ex)
				{
					Log.Write("debug", $"[VoxBridge] dispatch error on {cmd.Intent}: {ex}");
					SendAck(cmd.Id, false, "dispatch_error");
				}
			}

			// Every tick: scan our player's production queues for items that finished
			// building and need to be placed on the map. Mirrors what a player would
			// do by clicking the ready building icon and then clicking the map.
			TryAutoPlaceReadyBuildings();

			// Also each tick: any newly-built miner gets sent to the nearest ore deposit
			// and deploys into a Mining Tower. One-shot per miner, tracked by ActorID.
			AutoMineNewMiners();

			if (++tickCount % info.SnapshotInterval == 0)
			{
				EmitStateSnapshot();
				EmitDerivedEvents();
			}
		}

		int lastQueueLogTick;
		void TryAutoPlaceReadyBuildings()
		{
			var p = world.LocalPlayer;
			if (p == null) return;

			// Periodic queue diagnostic so we can see whether items are progressing.
			if (tickCount - lastQueueLogTick > 50)
			{
				lastQueueLogTick = tickCount;
				foreach (var q in p.PlayerActor.TraitsImplementing<ProductionQueue>())
				{
					var ci = q.CurrentItem();
					if (ci == null) continue;
					Log.Write("debug", $"[VoxBridge] queue {q.Info.Type}: item={ci.Item} remaining={ci.RemainingTime}/{ci.TotalTime} done={ci.Done} paused={ci.Paused}");
				}
			}

			var queues = p.PlayerActor.TraitsImplementing<ProductionQueue>();
			foreach (var queue in queues)
			{
				var current = queue.CurrentItem();
				if (current == null || !current.Done)
					continue;

				if (!world.Map.Rules.Actors.TryGetValue(current.Item, out var ai))
					continue;
				var bi = ai.TraitInfoOrDefault<BuildingInfo>();
				if (bi == null)
					continue;  // unit, not a building — auto-deploys, no placement needed

				var origin = ResolvePlacementOrigin();
				var cell = FindPlacementCell(ai, bi, origin);
				if (cell == null)
					continue;  // try again next tick — maybe a cell opens up

				// PlaceBuilding's ExtraData is the actor id of any producer that produces
				// this queue's type. Find one.
				var queueProducer = world.Actors.FirstOrDefault(a =>
					!a.IsDead && a.Owner == p &&
					a.TraitsImplementing<Production>().Any(prod => prod.Info.Produces.Contains(queue.Info.Type)));
				if (queueProducer == null)
					continue;

				var placeOrder = new Order("PlaceBuilding", p.PlayerActor, Target.FromCell(world, cell.Value), false)
				{
					TargetString = current.Item,
					ExtraLocation = new CPos(0, 0),
					ExtraData = queueProducer.ActorID,
					SuppressVisualFeedback = true,
				};
				world.IssueOrder(placeOrder);
				Log.Write("debug", $"[VoxBridge] auto-placed {current.Item} at {cell.Value}");
			}
		}

		public void Disposing(Actor self)
		{
			try { cts?.Cancel(); } catch { }
			try { listener?.Stop(); } catch { }
			try { activeClient?.Close(); } catch { }
		}

		// ----- command dispatch -----

		(bool ok, string error) Dispatch(ParsedCommand cmd)
		{
			switch (cmd.Intent)
			{
				case "select":            return HandleSelect(cmd.Args);
				case "select_all":        return HandleSelectAll();
				case "move":              return HandleMove(cmd.Args);
				case "stop":              return HandleStop();
				case "attack":            return HandleAttack(cmd.Args, attackMove: false);
				case "attack_move":       return HandleAttack(cmd.Args, attackMove: true);
				case "build":             return HandleBuild(cmd.Args);
				case "produce_structure": return HandleProduceStructure(cmd.Args);
				case "harvest":           return HandleHarvest();
				case "deploy":            return HandleDeploy();
				case "auto_mine":         return HandleAutoMine();
				case "toggle_labels":     return HandleToggleLabels(cmd.Args);
				case "meta_pause":        return HandleMetaPause(cmd.Args);
				case "query":             return (true, null);
				default:                  return (false, "unknown_intent");
			}
		}

		(bool ok, string error) HandleSelect(JsonElement args)
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			var filter = args.TryGetProperty("filter", out var f) ? f.GetString() : "all_units";

			var owned = world.Actors
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner == p)
				.Where(a => a.TraitOrDefault<Mobile>() != null)
				.ToList();

			IEnumerable<Actor> filtered = owned;
			if (filter != null && filter != "all_units")
			{
				var needle = filter.StartsWith("all_") ? filter.Substring(4) : filter;
				filtered = owned.Where(a =>
					a.Info.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
			}

			var picked = filtered.ToList();
			world.Selection.Combine(world, picked, isCombine: false, isClick: false);
			return picked.Count > 0 ? (true, null) : (false, "empty_selection");
		}

		(bool ok, string error) HandleMove(JsonElement args)
		{
			var target = args.TryGetProperty("target", out var t) ? t.GetString() : null;
			if (target == null) return (false, "missing_target");

			var cell = ResolveLogicalCell(target);
			if (cell == null) return (false, "unknown_target");

			var selected = world.Selection.Actors
				.Where(a => !a.IsDead && a.Owner == world.LocalPlayer)
				.ToList();
			if (selected.Count == 0) return (false, "no_selection");

			foreach (var a in selected)
				world.IssueOrder(new Order("Move", a, Target.FromCell(world, cell.Value), false));
			return (true, null);
		}

		(bool ok, string error) HandleStop()
		{
			var selected = world.Selection.Actors
				.Where(a => !a.IsDead && a.Owner == world.LocalPlayer)
				.ToList();
			foreach (var a in selected)
				world.IssueOrder(new Order("Stop", a, false));
			return (true, null);
		}

		(bool ok, string error) HandleAttack(JsonElement args, bool attackMove)
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			Actor targetActor = ResolveActorRef(args);
			Target target;
			if (targetActor != null)
			{
				target = Target.FromActor(targetActor);
			}
			else
			{
				var name = args.TryGetProperty("target", out var t) ? t.GetString() : null;
				if (name == null) return (false, "missing_target");
				var cell = ResolveLogicalCell(name);
				if (cell == null) return (false, "unknown_target");
				target = Target.FromCell(world, cell.Value);
			}

			var orderName = attackMove ? "AttackMove" : "Attack";
			var selected = world.Selection.Actors
				.Where(a => !a.IsDead && a.Owner == p)
				.ToList();
			if (selected.Count == 0) return (false, "no_selection");

			foreach (var a in selected)
				world.IssueOrder(new Order(orderName, a, target, false));
			return (true, null);
		}

		(bool ok, string error) HandleBuild(JsonElement args)
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			var unit = args.TryGetProperty("unit", out var u) ? u.GetString() : null;
			var count = args.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number
				? c.GetInt32()
				: 1;
			if (unit == null) return (false, "missing_unit");

			// Confirm at least one producer exists for some queue this unit belongs to.
			if (!world.Map.Rules.Actors.TryGetValue(unit, out var ai))
				return (false, "unknown_unit");
			var buildable = ai.TraitInfoOrDefault<BuildableInfo>();
			if (buildable == null) return (false, "not_buildable");
			var queueType = buildable.Queue.FirstOrDefault();
			if (queueType == null) return (false, "no_queue");

			var producer = world.Actors.FirstOrDefault(a =>
				!a.IsDead && a.Owner == p &&
				a.TraitsImplementing<Production>().Any(prod => prod.Info.Produces.Contains(queueType)));
			if (producer == null) return (false, "no_producer_for_queue");

			// Subject must be PlayerActor (where ProductionQueue traits live), not
			// the producer building — same fix as HandleProduceStructure.
			world.IssueOrder(Order.StartProduction(p.PlayerActor, unit, count));
			Log.Write("debug", $"[VoxBridge] queued {count}x {unit} on PlayerActor for queue={queueType}");
			return (true, null);
		}

		(bool ok, string error) HandleProduceStructure(JsonElement args)
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			var structure = args.TryGetProperty("structure", out var s) ? s.GetString() : null;
			if (structure == null) return (false, "missing_structure");

			var name = structure.ToLowerInvariant();
			if (!world.Map.Rules.Actors.TryGetValue(name, out var actorInfo))
				return (false, "unknown_structure");
			var buildingInfo = actorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (buildingInfo == null) return (false, "not_a_building");

			// Find a producer that can build this — its Production.Produces must include
			// the queue type this structure belongs to.
			var buildable = actorInfo.TraitInfoOrDefault<BuildableInfo>();
			if (buildable == null) return (false, "not_buildable");
			var queueType = buildable.Queue.FirstOrDefault();
			if (queueType == null) return (false, "no_queue");

			var producer = world.Actors.FirstOrDefault(a =>
				!a.IsDead && a.Owner == p &&
				a.TraitsImplementing<Production>().Any(prod => prod.Info.Produces.Contains(queueType)));
			if (producer == null) return (false, "no_producer_for_queue");

			// CRITICAL: StartProduction's subject must be the actor that hosts the queue
			// (i.e. the PlayerActor), NOT the producer building. ResolveOrder is invoked
			// on traits attached to the order's subject; ProductionQueue is on PlayerActor.
			// Sending it to the producer building drops the order silently.
			world.IssueOrder(Order.StartProduction(p.PlayerActor, name, 1));
			Log.Write("debug", $"[VoxBridge] queued {name} on PlayerActor for queue={queueType} (producer={producer.Info.Name})");
			return (true, null);
		}

		CPos ResolvePlacementOrigin()
		{
			var p = world.LocalPlayer;
			if (p != null)
			{
				var ownedBuilding = world.Actors
					.Where(a => !a.IsDead && a.IsInWorld && a.Owner == p)
					.Where(a => a.TraitOrDefault<Building>() != null)
					.OrderByDescending(a => a.TraitsImplementing<Production>().Any())
					.FirstOrDefault();
				if (ownedBuilding != null)
					return ownedBuilding.Location;

				var anyOwned = world.Actors.FirstOrDefault(a => !a.IsDead && a.IsInWorld && a.Owner == p);
				if (anyOwned != null)
					return anyOwned.Location;
			}
			var b = world.Map.Bounds;
			return new CPos(b.Left + b.Width / 2, b.Top + b.Height / 2);
		}

		CPos? FindPlacementCell(ActorInfo ai, BuildingInfo bi, CPos origin)
		{
			// Simple spiral search outward. Range ~ 12 cells which is generous for most buildings.
			for (var radius = 1; radius <= 12; radius++)
			{
				for (var dx = -radius; dx <= radius; dx++)
				{
					for (var dy = -radius; dy <= radius; dy++)
					{
						if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;
						var candidate = origin + new CVec(dx, dy);
						if (world.CanPlaceBuilding(candidate, ai, bi, null))
							return candidate;
					}
				}
			}
			return null;
		}

		(bool ok, string error) HandleAutoMine()
		{
			var selected = world.Selection.Actors
				.Where(a => !a.IsDead && a.Owner == world.LocalPlayer)
				.Where(IsMiner)
				.ToList();
			if (selected.Count == 0) return (false, "no_miner_selected");

			var dispatched = 0;
			foreach (var miner in selected)
				if (TryDispatchMinerToOre(miner))
					dispatched++;

			return dispatched > 0 ? (true, null) : (false, "no_ore_found");
		}

		void AutoMineNewMiners()
		{
			var p = world.LocalPlayer;
			if (p == null) return;
			foreach (var a in world.Actors)
			{
				if (a.IsDead || !a.IsInWorld || a.Owner != p) continue;
				if (!IsMiner(a)) continue;
				if (autoMineHandled.Contains(a.ActorID)) continue;
				autoMineHandled.Add(a.ActorID);
				TryDispatchMinerToOre(a);
			}
		}

		static bool IsMiner(Actor a) =>
			a.Info.Name.Equals("miner", StringComparison.OrdinalIgnoreCase);

		bool TryDispatchMinerToOre(Actor miner)
		{
			var cell = FindNearestOre(miner.Location);
			if (cell == null)
			{
				Log.Write("debug", $"[VoxBridge] auto-mine: no ore on map for miner#{miner.ActorID}");
				return false;
			}
			world.IssueOrder(new Order("Move", miner, Target.FromCell(world, cell.Value), queued: false));
			world.IssueOrder(new Order("DeployTransform", miner, queued: true));
			Log.Write("debug", $"[VoxBridge] auto-mine: miner#{miner.ActorID} -> {cell.Value}");
			return true;
		}

		CPos? FindNearestOre(CPos origin)
		{
			var resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			if (resourceLayer == null) return null;

			CPos? best = null;
			var bestDist = long.MaxValue;
			foreach (var cell in world.Map.AllCells)
			{
				if (resourceLayer.GetResource(cell).Type == null) continue;
				var dx = cell.X - origin.X;
				var dy = cell.Y - origin.Y;
				long dist = (long)dx * dx + (long)dy * dy;
				if (dist < bestDist)
				{
					bestDist = dist;
					best = cell;
				}
			}
			return best;
		}

		(bool ok, string error) HandleDeploy()
		{
			var selected = world.Selection.Actors
				.Where(a => !a.IsDead && a.Owner == world.LocalPlayer)
				.ToList();
			if (selected.Count == 0) return (false, "no_selection");

			var deployed = 0;
			foreach (var a in selected)
			{
				if (a.TraitOrDefault<Transforms>() != null)
				{
					world.IssueOrder(new Order("DeployTransform", a, false));
					deployed++;
				}
			}
			return deployed > 0 ? (true, null) : (false, "selection_cannot_deploy");
		}

		(bool ok, string error) HandleSelectAll()
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");
			var owned = world.Actors
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner == p)
				.Where(a => a.TraitOrDefault<Mobile>() != null
					|| a.TraitOrDefault<Transforms>() != null
					|| a.TraitOrDefault<Building>() != null)
				.ToList();
			world.Selection.Combine(world, owned, isCombine: false, isClick: false);
			Log.Write("debug", $"[VoxBridge] select_all selected {owned.Count} actors " +
				$"(mobile={owned.Count(a => a.TraitOrDefault<Mobile>() != null)}, " +
				$"buildings={owned.Count(a => a.TraitOrDefault<Building>() != null)})");
			return owned.Count > 0 ? (true, null) : (false, "nothing_owned");
		}

		(bool ok, string error) HandleHarvest()
		{
			var selected = world.Selection.Actors
				.Where(a => !a.IsDead && a.Owner == world.LocalPlayer)
				.ToList();
			if (selected.Count == 0) return (false, "no_selection");
			foreach (var a in selected)
				world.IssueOrder(new Order("Harvest", a, false));
			return (true, null);
		}

		(bool ok, string error) HandleToggleLabels(JsonElement args)
		{
			var mgr = world.WorldActor.TraitOrDefault<VoxLabelManager>();
			if (mgr == null) return (false, "label_manager_missing");

			// If args.visible is present, set it explicitly; otherwise flip.
			if (args.TryGetProperty("visible", out var v) && v.ValueKind == JsonValueKind.True)
				mgr.Enabled = true;
			else if (args.TryGetProperty("visible", out var v2) && v2.ValueKind == JsonValueKind.False)
				mgr.Enabled = false;
			else
				mgr.Enabled = !mgr.Enabled;

			Log.Write("debug", $"[VoxBridge] labels now {(mgr.Enabled ? "ON" : "OFF")}");
			return (true, null);
		}

		(bool ok, string error) HandleMetaPause(JsonElement args)
		{
			var paused = args.TryGetProperty("paused", out var p) && p.GetBoolean();
			world.SetPauseState(paused);
			return (true, null);
		}

		// ----- resolution helpers -----

		CPos? ResolveLogicalCell(string @ref)
		{
			var b = world.Map.Bounds;
			switch (@ref)
			{
				case "east_edge":  return new CPos(b.Right - 2, b.Top + b.Height / 2);
				case "west_edge":  return new CPos(b.Left + 2,  b.Top + b.Height / 2);
				case "north_edge": return new CPos(b.Left + b.Width / 2, b.Top + 2);
				case "south_edge": return new CPos(b.Left + b.Width / 2, b.Bottom - 2);
				case "center":     return new CPos(b.Left + b.Width / 2, b.Top + b.Height / 2);
				default:           return null;
			}
		}

		Actor ResolveActorRef(JsonElement args)
		{
			if (args.TryGetProperty("target_ref", out var r) && r.ValueKind == JsonValueKind.String)
			{
				var handle = r.GetString();
				if (handle == "__ambiguous__") return null;
				return world.Actors.FirstOrDefault(a => HandleOf(a) == handle);
			}
			if (args.TryGetProperty("target_kind", out var k) && k.ValueKind == JsonValueKind.String)
			{
				var kind = k.GetString();
				var isEnemy = kind.StartsWith("enemy_", StringComparison.Ordinal);
				var bare = isEnemy ? kind.Substring(6) : kind;
				return world.Actors.FirstOrDefault(a =>
					!a.IsDead && a.IsInWorld &&
					(isEnemy ? a.Owner != world.LocalPlayer : a.Owner == world.LocalPlayer) &&
					a.Info.Name.Equals(bare, StringComparison.OrdinalIgnoreCase));
			}
			return null;
		}

		static string HandleOf(Actor a) =>
			$"{a.Owner.InternalName.ToLowerInvariant()}_{a.Info.Name.ToLowerInvariant()}_{a.ActorID}";

		// ----- outbound events -----

		void EmitStateSnapshot()
		{
			if (activeWriter == null || world.LocalPlayer == null) return;
			var p = world.LocalPlayer;
			var pr = p.PlayerActor.TraitOrDefault<PlayerResources>();
			int cash = (pr?.Cash ?? 0) + (pr?.Resources ?? 0);
			int units = world.Actors.Count(a =>
				!a.IsDead && a.IsInWorld && a.Owner == p && a.TraitOrDefault<Mobile>() != null);

			var enemyList = world.Actors
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner != p && !a.Owner.NonCombatant)
				.Take(20)
				.Select(a => $"{{\"handle\":\"{HandleOf(a)}\",\"kind\":\"{a.Info.Name}\",\"owner\":\"enemy\"}}")
				.ToList();

			var json = $"{{\"type\":\"event\",\"kind\":\"state_snapshot\",\"ts\":{Game.LocalTick}," +
				$"\"cash\":{cash},\"units\":{units}," +
				$"\"enemies\":[{string.Join(",", enemyList)}]}}";
			Emit(json);
		}

		void EmitDerivedEvents()
		{
			if (world.LocalPlayer == null) return;
			var p = world.LocalPlayer;
			int friendly = world.Actors.Count(a =>
				!a.IsDead && a.IsInWorld && a.Owner == p && a.TraitOrDefault<Mobile>() != null);
			if (prevFriendlyUnits >= 0 && friendly < prevFriendlyUnits)
			{
				var lost = prevFriendlyUnits - friendly;
				Emit($"{{\"type\":\"event\",\"kind\":\"units_lost\",\"ts\":{Game.LocalTick},\"count\":{lost}}}");
			}
			prevFriendlyUnits = friendly;
		}

		void SendAck(string id, bool ok, string error)
		{
			var payload = error == null
				? $"{{\"type\":\"ack\",\"id\":\"{id}\",\"ok\":{(ok ? "true" : "false")}}}"
				: $"{{\"type\":\"ack\",\"id\":\"{id}\",\"ok\":false,\"error\":\"{error}\"}}";
			Emit(payload);
		}

		void Emit(string line)
		{
			lock (writerLock)
			{
				if (activeWriter == null) return;
				try { activeWriter.WriteLine(line); }
				catch { /* client gone; will be replaced on next accept */ }
			}
		}

		// ----- JSON parsing -----

		sealed class ParsedCommand
		{
			public string Id { get; }
			public string Intent { get; }
			public JsonElement Args { get; }
			public ParsedCommand(string id, string intent, JsonElement args)
			{
				Id = id; Intent = intent; Args = args;
			}
		}

		static ParsedCommand TryParse(string line)
		{
			try
			{
				using var doc = JsonDocument.Parse(line);
				var root = doc.RootElement;
				if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "command")
					return null;
				var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : "";
				var intent = root.TryGetProperty("intent", out var iEl) ? iEl.GetString() : "";
				var args = root.TryGetProperty("args", out var aEl) ? aEl.Clone() : default;
				return new ParsedCommand(id ?? "", intent ?? "", args);
			}
			catch
			{
				return null;
			}
		}
	}
}
