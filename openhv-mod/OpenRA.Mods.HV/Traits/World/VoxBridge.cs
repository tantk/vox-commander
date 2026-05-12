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
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.HV.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Localhost TCP bridge for external voice control. Attach to the world actor.")]
	public class VoxBridgeInfo : TraitInfo
	{
		[Desc("TCP port to listen on for command JSON lines.")]
		public readonly int Port = 7777;

		public override object Create(ActorInitializer init) { return new VoxBridge(this); }
	}

	public class VoxBridge : IWorldLoaded, ITick, INotifyActorDisposing
	{
		readonly VoxBridgeInfo info;
		readonly ConcurrentQueue<string> inbound = new ConcurrentQueue<string>();
		TcpListener listener;
		TcpClient activeClient;
		StreamWriter activeWriter;
		CancellationTokenSource cts;
		World world;

		public VoxBridge(VoxBridgeInfo info) { this.info = info; }

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			world = w;
			cts = new CancellationTokenSource();
			listener = new TcpListener(IPAddress.Loopback, info.Port);
			listener.Start();
			Log.Write("debug", $"[VoxBridge] listening on 127.0.0.1:{info.Port}");
			_ = Task.Run(() => AcceptLoopAsync(cts.Token));
		}

		async Task AcceptLoopAsync(CancellationToken ct)
		{
			while (!ct.IsCancellationRequested)
			{
				TcpClient client;
				try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
				catch (OperationCanceledException) { return; }
				catch (ObjectDisposedException) { return; }

				activeClient = client;
				activeWriter = new StreamWriter(client.GetStream(), new UTF8Encoding(false))
				{
					AutoFlush = true,
					NewLine = "\n",
				};
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
			while (inbound.TryDequeue(out var line))
			{
				Log.Write("debug", $"[VoxBridge] in: {line}");
				// Subsequent tasks replace this with real command dispatch.
				SendAckRaw(ExtractId(line), true, null);
			}
		}

		public void Disposing(Actor self)
		{
			try { cts?.Cancel(); } catch { }
			try { listener?.Stop(); } catch { }
			try { activeClient?.Close(); } catch { }
		}

		void SendAckRaw(string id, bool ok, string error)
		{
			if (activeWriter == null) return;
			var payload = error == null
				? $"{{\"type\":\"ack\",\"id\":\"{id}\",\"ok\":{(ok ? "true" : "false")}}}"
				: $"{{\"type\":\"ack\",\"id\":\"{id}\",\"ok\":false,\"error\":\"{error}\"}}";
			try { activeWriter.WriteLine(payload); } catch { }
		}

		static string ExtractId(string line)
		{
			const string key = "\"id\":\"";
			var i = line.IndexOf(key, StringComparison.Ordinal);
			if (i < 0) return "";
			var start = i + key.Length;
			var end = line.IndexOf('"', start);
			return end < 0 ? "" : line.Substring(start, end - start);
		}
	}
}
