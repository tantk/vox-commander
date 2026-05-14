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

		[Desc("Emit enemy_intel events for the Intel voice channel.")]
		public readonly bool IntelEnabled = false;

		[Desc("Absolute path to the voice-panel launcher script that spawns when a real match starts. " +
			"Default empty — auto-spawn is OFF because the in-game VOX_PANEL has its own Activate Voice button. " +
			"Set this to vox-commander.bat to reinstate the legacy tkinter panel auto-spawn.")]
		public readonly string PanelLauncher = "";

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
		readonly Dictionary<uint, CPos> dispatchedMinerDestinations = new Dictionary<uint, CPos>();
		readonly HashSet<uint> minerDeployFired = new HashSet<uint>();

		// Enemy intel state: previous-tick snapshot so we can detect deltas.
		int prevEnemyArmyCount = -1;
		int prevEnemyDistanceToBase = -1;
		readonly Dictionary<string, string> prevEnemyQueueItem = new Dictionary<string, string>();

		// Cells within this radius of an existing Mining Tower or an already-dispatched
		// miner are considered "claimed" — the next miner picks the next nearest free ore.
		const int OreClaimRadius = 6;

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
			{
				Log.Write("debug", "[VoxBridge] first tick fired");
				DumpPlayersAndOwners();
			}

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

			// And: any miner whose Transforms trait is unpaused (= it's standing on ore
			// terrain right now) gets DeployTransform fired ONCE. Queuing the order with
			// the move doesn't work because PauseOnCondition=!deposits drops the order
			// silently while the miner is in transit.
			AutoDeployMinersOnOre();

			if (++tickCount % info.SnapshotInterval == 0)
			{
				EmitStateSnapshot();
				EmitDerivedEvents();
				if (info.IntelEnabled)
					EmitEnemyIntel();
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

				// PlaceBuilding's ExtraData is the actor id of the actor that HOSTS the
				// ProductionQueue trait — i.e. the PlayerActor, since queues are player-scoped
				// in HV (ClassicProductionQueue on player.yaml). The engine's PlaceBuilding
				// handler does: w.GetActorById(order.ExtraData).TraitsImplementing<ProductionQueue>()
				// so the ID *must* match the queue host, not the producer building.
				var placeOrder = new Order("PlaceBuilding", p.PlayerActor, Target.FromCell(world, cell.Value), false)
				{
					TargetString = current.Item,
					ExtraLocation = new CPos(0, 0),
					ExtraData = p.PlayerActor.ActorID,
					SuppressVisualFeedback = true,
				};
				world.IssueOrder(placeOrder);
				Log.Write("debug", $"[VoxBridge] auto-placed {current.Item} at {cell.Value}");
			}
		}

		void DumpPlayersAndOwners()
		{
			foreach (var pl in world.Players)
				Log.Write("debug", $"[VoxBridge] player '{pl.InternalName}' Bot={(pl.IsBot ? pl.BotType : "<none>")} NonCombatant={pl.NonCombatant} Spectating={pl.Spectating} local={pl == world.LocalPlayer}");

			var byOwner = world.Actors
				.Where(a => !a.IsDead && a.TraitOrDefault<Building>() != null)
				.GroupBy(a => a.Owner.InternalName);
			foreach (var g in byOwner)
				Log.Write("debug", $"[VoxBridge] buildings owned by '{g.Key}': {g.Count()} -> {string.Join(",", g.Select(x => x.Info.Name).Distinct())}");
		}

		public void Disposing(Actor self)
		{
			try { cts?.Cancel(); } catch { }
			try { listener?.Stop(); } catch { }
			try { activeClient?.Close(); } catch { }
		}

		// In-process entry point for the in-game chrome panel.
		// The UI Logic class formats a JSON command string and pushes it through
		// the same queue the TCP server feeds, so dispatch + diagnostics stay
		// in one code path.
		public void EnqueueRawCommand(string json)
		{
			if (string.IsNullOrWhiteSpace(json)) return;
			inbound.Enqueue(json);
		}

		// ----- command dispatch -----

		(bool ok, string error) Dispatch(ParsedCommand cmd)
		{
			switch (cmd.Intent)
			{
				case "select":            return HandleSelect(cmd.Args);
				case "select_all":        return HandleSelectAll();
				case "select_army":       return HandleSelectArmy();
				case "move":              return HandleMove(cmd.Args);
				case "stop":              return HandleStop();
				case "attack":            return HandleAttack(cmd.Args, attackMove: false);
				case "attack_move":       return HandleAttack(cmd.Args, attackMove: true);
				case "build":             return HandleBuild(cmd.Args);
				case "produce_structure": return HandleProduceStructure(cmd.Args);
				case "harvest":           return HandleHarvest();
				case "deploy":            return HandleDeploy();
				case "auto_mine":         return HandleAutoMine();
				case "scout":             return HandleScout();
				case "harass":            return HandleHarass();
				case "assault":           return HandleAssault();
				case "defend":            return HandleDefend();
				case "station_army":      return HandleStationArmy(cmd.Args);
				case "focus_fire":        return HandleFocusFire(cmd.Args);
				case "set_rally":         return HandleSetRally(cmd.Args);
				case "hold_position":     return HandleHoldPosition();
				case "toggle_grid":       return HandleToggleGrid(cmd.Args);
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

		// ----- attack modes -----

		List<Actor> ArmyUnits(Player p) => world.Actors
			.Where(a => !a.IsDead && a.IsInWorld && a.Owner == p)
			.Where(a => a.TraitOrDefault<Mobile>() != null)
			.Where(a => a.TraitsImplementing<AttackBase>().Any(t => !t.IsTraitDisabled))
			.ToList();

		CPos? EnemyBaseCentroid(Player p)
		{
			var cells = world.Actors
				.Where(a => !a.IsDead && a.IsInWorld &&
					a.Owner != p && !a.Owner.NonCombatant &&
					a.TraitOrDefault<Building>() != null)
				.Select(a => a.Location)
				.ToList();
			if (cells.Count == 0) return null;
			int sx = 0, sy = 0;
			foreach (var c in cells) { sx += c.X; sy += c.Y; }
			return new CPos(sx / cells.Count, sy / cells.Count);
		}

		CPos? FriendlyBaseCentroid(Player p)
		{
			var cells = world.Actors
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner == p &&
					a.TraitOrDefault<Building>() != null)
				.Select(a => a.Location)
				.ToList();
			if (cells.Count == 0) return null;
			int sx = 0, sy = 0;
			foreach (var c in cells) { sx += c.X; sy += c.Y; }
			return new CPos(sx / cells.Count, sy / cells.Count);
		}

		Actor FindEnemyEconomyActor(Player p)
		{
			// Anything that produces value for the enemy: miners, mining towers,
			// storages (refineries), tankers.
			var econNames = new[] { "miner", "miner2", "storage", "tanker1" };
			return world.Actors.FirstOrDefault(a =>
				!a.IsDead && a.IsInWorld &&
				a.Owner != p && !a.Owner.NonCombatant &&
				System.Array.IndexOf(econNames, a.Info.Name.ToLowerInvariant()) >= 0);
		}

		(bool ok, string error) HandleScout()
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			var target = EnemyBaseCentroid(p);
			if (target == null) return (false, "no_enemy_base_visible");

			// Pick our single fastest combat unit; fall back to any non-miner mobile
			// unit if we have no army yet.
			Actor scout = ArmyUnits(p)
				.OrderByDescending(a => a.TraitOrDefault<Mobile>()?.Info.Speed ?? 0)
				.FirstOrDefault();
			if (scout == null)
			{
				scout = world.Actors
					.Where(a => !a.IsDead && a.IsInWorld && a.Owner == p)
					.Where(a => a.TraitOrDefault<Mobile>() != null && !IsMiner(a))
					.OrderByDescending(a => a.TraitOrDefault<Mobile>()?.Info.Speed ?? 0)
					.FirstOrDefault();
			}
			if (scout == null) return (false, "no_scout_available");

			world.Selection.Combine(world, new[] { scout }, isCombine: false, isClick: false);
			world.IssueOrder(new Order("Move", scout, Target.FromCell(world, target.Value), false));
			Log.Write("debug", $"[VoxBridge] scout: {scout.Info.Name}#{scout.ActorID} -> {target.Value}");
			return (true, null);
		}

		(bool ok, string error) HandleHarass()
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			var econ = FindEnemyEconomyActor(p);
			if (econ == null) return (false, "no_enemy_economy_visible");

			var army = ArmyUnits(p);
			if (army.Count == 0) return (false, "no_army");

			// Take ~half the army, clamped to [2, 5]. Smallest viable raiding force.
			var size = System.Math.Min(System.Math.Max(2, army.Count / 2), 5);
			var force = army.Take(size).ToList();

			world.Selection.Combine(world, force, isCombine: false, isClick: false);
			foreach (var a in force)
				world.IssueOrder(new Order("AttackMove", a, Target.FromActor(econ), false));
			Log.Write("debug", $"[VoxBridge] harass: {force.Count} units -> {econ.Info.Name}#{econ.ActorID}");
			return (true, null);
		}

		(bool ok, string error) HandleAssault()
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			var army = ArmyUnits(p);
			if (army.Count == 0) return (false, "no_army");

			var target = EnemyBaseCentroid(p);
			if (target == null) return (false, "no_enemy_base_visible");

			world.Selection.Combine(world, army, isCombine: false, isClick: false);
			foreach (var a in army)
				world.IssueOrder(new Order("AttackMove", a, Target.FromCell(world, target.Value), false));
			Log.Write("debug", $"[VoxBridge] assault: {army.Count} units -> {target.Value}");
			return (true, null);
		}

		(bool ok, string error) HandleDefend()
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			var army = ArmyUnits(p);
			if (army.Count == 0) return (false, "no_army");

			var home = FriendlyBaseCentroid(p);
			if (home == null) return (false, "no_friendly_base");

			world.Selection.Combine(world, army, isCombine: false, isClick: false);
			foreach (var a in army)
				world.IssueOrder(new Order("Move", a, Target.FromCell(world, home.Value), false));
			Log.Write("debug", $"[VoxBridge] defend: {army.Count} units -> {home.Value}");
			return (true, null);
		}

		// ----- auto-mining -----

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
			var cell = FindNearestUnclaimedOre(miner.Location);
			if (cell == null)
			{
				Log.Write("debug", $"[VoxBridge] auto-mine: no unclaimed ore for miner#{miner.ActorID}");
				return false;
			}

			dispatchedMinerDestinations[miner.ActorID] = cell.Value;
			// Just move — deploy gets handled by AutoDeployMinersOnOre once the
			// miner is actually standing on Ore terrain and its Transforms trait
			// unpauses. Queuing DeployTransform here would be silently dropped.
			world.IssueOrder(new Order("Move", miner, Target.FromCell(world, cell.Value), queued: false));
			Log.Write("debug", $"[VoxBridge] auto-mine: miner#{miner.ActorID} -> {cell.Value} (claimed)");
			return true;
		}

		void AutoDeployMinersOnOre()
		{
			var p = world.LocalPlayer;
			if (p == null) return;
			foreach (var a in world.Actors)
			{
				if (a.IsDead || !a.IsInWorld || a.Owner != p) continue;
				if (!IsMiner(a)) continue;
				if (minerDeployFired.Contains(a.ActorID)) continue;
				var t = a.TraitOrDefault<Transforms>();
				if (t == null || t.IsTraitPaused || t.IsTraitDisabled) continue;
				// Standing on ore, ready to deploy. One-shot.
				world.IssueOrder(new Order("DeployTransform", a, false));
				minerDeployFired.Add(a.ActorID);
				Log.Write("debug", $"[VoxBridge] miner#{a.ActorID} deploying into Mining Tower");
			}
		}

		CPos? FindNearestUnclaimedOre(CPos origin)
		{
			var resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			if (resourceLayer == null) return null;

			// Build the claimed set: every existing Mining Tower (any owner — they all
			// physically block ore) plus every cell another miner of ours is heading toward.
			var claimed = new List<CPos>();
			foreach (var a in world.Actors)
			{
				if (a.IsDead || !a.IsInWorld) continue;
				if (a.Info.Name.Equals("miner2", StringComparison.OrdinalIgnoreCase))
					claimed.Add(a.Location);
			}
			foreach (var d in dispatchedMinerDestinations)
			{
				// Drop stale entries: miner died, or already deployed (no longer "miner").
				var a = world.GetActorById(d.Key);
				if (a == null || a.IsDead || !IsMiner(a)) continue;
				claimed.Add(d.Value);
			}

			var claimSq = (long)OreClaimRadius * OreClaimRadius;
			CPos? best = null;
			var bestDist = long.MaxValue;
			foreach (var cell in world.Map.AllCells)
			{
				if (resourceLayer.GetResource(cell).Type == null) continue;

				var tooClose = false;
				foreach (var c in claimed)
				{
					var dx = cell.X - c.X;
					var dy = cell.Y - c.Y;
					if ((long)dx * dx + (long)dy * dy < claimSq)
					{
						tooClose = true;
						break;
					}
				}
				if (tooClose) continue;

				var ox = cell.X - origin.X;
				var oy = cell.Y - origin.Y;
				long dist = (long)ox * ox + (long)oy * oy;
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

		(bool ok, string error) HandleSelectArmy()
		{
			// Combat-only selection: anything owned by the player that can attack
			// (has an AttackBase-derived trait). Filters out miners, builders,
			// technicians, tankers, and buildings — only fielded combat units.
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			var army = world.Actors
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner == p)
				.Where(a => a.TraitOrDefault<Mobile>() != null)
				.Where(a => a.TraitsImplementing<AttackBase>().Any(t => !t.IsTraitDisabled))
				.ToList();

			world.Selection.Combine(world, army, isCombine: false, isClick: false);
			Log.Write("debug", $"[VoxBridge] select_army selected {army.Count} combat units");
			return army.Count > 0 ? (true, null) : (false, "no_army");
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

		(bool ok, string error) HandleStationArmy(JsonElement args)
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			var army = ArmyUnits(p);
			if (army.Count == 0) return (false, "no_army");

			var target = args.TryGetProperty("target", out var t) ? t.GetString() : null;
			if (string.IsNullOrEmpty(target)) return (false, "missing_target");

			var cell = ResolveLogicalCell(target);
			if (cell == null) return (false, "unknown_target");

			// Default to AttackMove (engages threats along the way) — pass aggressive=false
			// for plain Move (slip past skirmishes, used for "hold position without
			// hunting").
			var aggressive = !args.TryGetProperty("aggressive", out var ag)
				|| ag.ValueKind != JsonValueKind.False;
			var order = aggressive ? "AttackMove" : "Move";

			world.Selection.Combine(world, army, isCombine: false, isClick: false);
			foreach (var a in army)
				world.IssueOrder(new Order(order, a, Target.FromCell(world, cell.Value), false));
			Log.Write("debug", $"[VoxBridge] station_army: {army.Count} units {order} -> {target} ({cell.Value})");
			return (true, null);
		}

		(bool ok, string error) HandleFocusFire(JsonElement args)
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			var label = args.TryGetProperty("target_label", out var l) ? l.GetString() : null;
			if (string.IsNullOrEmpty(label)) return (false, "missing_target_label");

			// Find the actor whose WithVoxLabel.Name matches. Match is forgiving:
			// "Outpost 3" / "outpost3" / "OUTPOST-3" all normalise to "outpost-3"
			// for comparison so the LLM doesn't have to nail the exact format.
			static string Normalize(string s) =>
				new string((s ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToLowerInvariant();

			var needle = Normalize(label);
			Actor target = null;
			foreach (var a in world.Actors)
			{
				if (a.IsDead || !a.IsInWorld) continue;
				var lab = a.TraitOrDefault<WithVoxLabel>();
				if (lab == null || string.IsNullOrEmpty(lab.Name)) continue;
				if (Normalize(lab.Name) == needle)
				{
					target = a;
					break;
				}
			}
			if (target == null) return (false, "unknown_label");

			var attackers = world.Selection.Actors
				.Where(a => !a.IsDead && a.Owner == p)
				.Where(a => a.TraitsImplementing<AttackBase>().Any(t => !t.IsTraitDisabled))
				.ToList();
			if (attackers.Count == 0)
			{
				// Fall back to the whole army if nothing combat-capable is selected.
				attackers = ArmyUnits(p);
				world.Selection.Combine(world, attackers, isCombine: false, isClick: false);
			}
			if (attackers.Count == 0) return (false, "no_attackers");

			var tgt = Target.FromActor(target);
			foreach (var a in attackers)
				world.IssueOrder(new Order("Attack", a, tgt, false));
			Log.Write("debug", $"[VoxBridge] focus_fire: {attackers.Count} units -> {label} ({target.Info.Name}#{target.ActorID})");
			return (true, null);
		}

		(bool ok, string error) HandleSetRally(JsonElement args)
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			var target = args.TryGetProperty("target", out var t) ? t.GetString() : null;
			if (string.IsNullOrEmpty(target)) return (false, "missing_target");

			var cell = ResolveLogicalCell(target);
			if (cell == null) return (false, "unknown_target");

			var rallyHosts = world.Actors
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner == p &&
					a.TraitOrDefault<RallyPoint>() != null)
				.ToList();
			if (rallyHosts.Count == 0) return (false, "no_rally_capable_buildings");

			var tgt = Target.FromCell(world, cell.Value);
			foreach (var b in rallyHosts)
				world.IssueOrder(new Order("SetRallyPoint", b, tgt, false));
			Log.Write("debug", $"[VoxBridge] set_rally: {rallyHosts.Count} buildings -> {target} ({cell.Value})");
			return (true, null);
		}

		(bool ok, string error) HandleHoldPosition()
		{
			var p = world.LocalPlayer;
			if (p == null) return (false, "no_local_player");

			var selected = world.Selection.Actors
				.Where(a => !a.IsDead && a.Owner == p)
				.ToList();
			if (selected.Count == 0) return (false, "no_selection");

			// Stop the current order AND lock the unit into Defend stance so it
			// fires on threats in range but won't chase them out of position.
			foreach (var a in selected)
			{
				world.IssueOrder(new Order("Stop", a, false));
				if (a.TraitOrDefault<AutoTarget>() != null)
					world.IssueOrder(new Order("SetUnitStance", a, false) { ExtraData = (uint)UnitStance.Defend });
			}
			Log.Write("debug", $"[VoxBridge] hold_position: {selected.Count} units stopped + Defend stance");
			return (true, null);
		}

		(bool ok, string error) HandleToggleGrid(JsonElement args)
		{
			var mgr = world.WorldActor.TraitOrDefault<VoxGridManager>();
			if (mgr == null) return (false, "grid_manager_missing");

			if (args.TryGetProperty("visible", out var v) && v.ValueKind == JsonValueKind.True)
				mgr.Enabled = true;
			else if (args.TryGetProperty("visible", out var v2) && v2.ValueKind == JsonValueKind.False)
				mgr.Enabled = false;
			else
				mgr.Enabled = !mgr.Enabled;

			Log.Write("debug", $"[VoxBridge] grid now {(mgr.Enabled ? "ON" : "OFF")}");
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
			bool paused;
			if (args.TryGetProperty("paused", out var p)
				&& (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False))
				paused = p.GetBoolean();
			else
				paused = !world.Paused;  // toggle when caller didn't specify

			world.SetPauseState(paused);
			Log.Write("debug", $"[VoxBridge] pause -> {paused}");
			return (true, null);
		}

		// ----- resolution helpers -----

		CPos? ResolveLogicalCell(string @ref)
		{
			if (string.IsNullOrEmpty(@ref)) return null;

			// 1) Battleship grid labels: "A1", "B4", "F6" etc.
			var grid = world.WorldActor.TraitOrDefault<VoxGridManager>();
			if (grid != null)
			{
				var fromGrid = grid.ResolveLabel(@ref, world);
				if (fromGrid != null) return fromGrid;
			}

			// 2) Friendly-building references: "near_storage", "near_radar", etc.
			if (@ref.StartsWith("near_", StringComparison.OrdinalIgnoreCase) && world.LocalPlayer != null)
			{
				var kind = @ref.Substring(5).ToLowerInvariant();
				var bldg = world.Actors.FirstOrDefault(a =>
					!a.IsDead && a.IsInWorld && a.Owner == world.LocalPlayer &&
					a.Info.Name.Equals(kind, StringComparison.OrdinalIgnoreCase));
				if (bldg != null) return bldg.Location;
			}

			// 3) Computed semantic locations.
			if (world.LocalPlayer != null)
			{
				switch (@ref.ToLowerInvariant())
				{
					case "base": return FriendlyBaseCentroid(world.LocalPlayer);
					case "enemy_base": return EnemyBaseCentroid(world.LocalPlayer);
					case "midpoint":
					{
						var our = FriendlyBaseCentroid(world.LocalPlayer);
						var their = EnemyBaseCentroid(world.LocalPlayer);
						if (our != null && their != null)
							return new CPos((our.Value.X + their.Value.X) / 2, (our.Value.Y + their.Value.Y) / 2);
						return null;
					}
				}
			}

			// 4) Named map edges (legacy).
			var b = world.Map.Bounds;
			switch (@ref.ToLowerInvariant())
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

		void EmitEnemyIntel()
		{
			if (activeWriter == null || world.LocalPlayer == null) return;
			var p = world.LocalPlayer;

			// Snapshot enemy production queues. Each enemy player has their own
			// ProductionQueue traits on their PlayerActor. We compare CurrentItem
			// against the previous tick to detect freshly-queued items.
			foreach (var enemyPlayer in world.Players)
			{
				if (enemyPlayer == p || enemyPlayer.NonCombatant) continue;

				foreach (var q in enemyPlayer.PlayerActor.TraitsImplementing<ProductionQueue>())
				{
					var current = q.CurrentItem();
					var item = current?.Item ?? "";
					var key = $"{enemyPlayer.InternalName}:{q.Info.Type}";
					prevEnemyQueueItem.TryGetValue(key, out var prev);
					prevEnemyQueueItem[key] = item;
					if (!string.IsNullOrEmpty(item) && item != prev)
					{
						EmitIntelEvent("enemy_producing", new Dictionary<string, string>
						{
							{ "kind", item },
							{ "queue", q.Info.Type },
						});
					}
				}
			}

			// Army-size surge: enemy mobile-combat count jumping by >=3 in one window
			// is worth narrating.
			var enemyArmy = world.Actors
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner != p && !a.Owner.NonCombatant)
				.Count(a => a.TraitOrDefault<Mobile>() != null
					&& a.TraitsImplementing<AttackBase>().Any(t => !t.IsTraitDisabled));
			if (prevEnemyArmyCount >= 0 && enemyArmy >= prevEnemyArmyCount + 3)
			{
				EmitIntelEvent("enemy_army_surge", new Dictionary<string, string>
				{
					{ "count", enemyArmy.ToString() },
					{ "delta", (enemyArmy - prevEnemyArmyCount).ToString() },
				});
			}
			prevEnemyArmyCount = enemyArmy;

			// Approach detection: if any enemy combat unit gets within N cells of
			// our friendly base centroid, that's an incoming attack.
			var home = FriendlyBaseCentroid(p);
			if (home != null)
			{
				var nearest = int.MaxValue;
				foreach (var a in world.Actors)
				{
					if (a.IsDead || !a.IsInWorld) continue;
					if (a.Owner == p || a.Owner.NonCombatant) continue;
					if (a.TraitOrDefault<Mobile>() == null) continue;
					if (!a.TraitsImplementing<AttackBase>().Any(t => !t.IsTraitDisabled)) continue;
					var dx = a.Location.X - home.Value.X;
					var dy = a.Location.Y - home.Value.Y;
					var d2 = dx * dx + dy * dy;
					if (d2 < nearest) nearest = d2;
				}
				if (nearest < int.MaxValue)
				{
					var distance = (int)System.Math.Sqrt(nearest);
					// Fire once when an enemy first crosses inside the 15-cell ring.
					if (distance <= 15 && (prevEnemyDistanceToBase < 0 || prevEnemyDistanceToBase > 15))
					{
						EmitIntelEvent("enemy_approaching", new Dictionary<string, string>
						{
							{ "distance", distance.ToString() },
						});
					}
					prevEnemyDistanceToBase = distance;
				}
			}
		}

		void EmitIntelEvent(string kind, Dictionary<string, string> payload)
		{
			var parts = new List<string>
			{
				"\"type\":\"event\"",
				$"\"kind\":\"{kind}\"",
				$"\"ts\":{Game.LocalTick}",
			};
			foreach (var kv in payload)
				parts.Add($"\"{kv.Key}\":\"{kv.Value}\"");
			Emit("{" + string.Join(",", parts) + "}");
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
