// TCP command server on 127.0.0.1:18765 for AI-driven integration testing.
// Accepts JSON-line commands: ping, reset, quit, screenshot, import_file,
// get_tracks, set_selection, set_timeline_pos, call, click_button,
// get_property, get_ui_state, list_buttons.

using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace VelosCCS;

public partial class TestServer : Node
{
	private const int Port = 18765;
	private TcpServer _server = null!;
	private StreamPeerTcp? _client;
	private string _buffer = "";

	public override void _Ready()
	{
		Name = "TestServer";
		_server = new TcpServer();
		if (_server.Listen(Port, "127.0.0.1") != Error.Ok)
		{
			GD.PrintErr("[TestServer] Failed to listen on port " + Port);
			QueueFree();
			return;
		}
		GD.Print($"[TestServer] Listening on 127.0.0.1:{Port}");
		SetProcess(true);
	}

	// Check for stale clients, accept new connections, read/process commands
	public override void _Process(double delta)
	{
		// Check if existing client is stale
		if (_client != null)
		{
			var status = _client.GetStatus();
			if (status == StreamPeerTcp.Status.None || status == StreamPeerTcp.Status.Error)
			{
				GD.Print("[TestServer] Client disconnected (status={status})");
				_client = null;
				_buffer = "";
			}
		}

		// Accept new connection
		if (_server.IsConnectionAvailable())
		{
			// Drop existing connection if any
			if (_client != null)
			{
				GD.Print("[TestServer] Dropping old connection for new client");
				_client = null;
				_buffer = "";
			}
			_client = _server.TakeConnection();
			_buffer = "";
			GD.Print("[TestServer] Client connected");
		}

		if (_client == null) return;

		// Check if client disconnected
		if (_client.GetStatus() != StreamPeerTcp.Status.Connected)
		{
			GD.Print($"[TestServer] Client disconnected (status={_client.GetStatus()})");
			_client = null;
			_buffer = "";
			return;
		}

		// Read available data
		while (_client.GetAvailableBytes() > 0)
		{
			string part = _client.GetString(_client.GetAvailableBytes());
			if (string.IsNullOrEmpty(part)) break;
			_buffer += part;
			ProcessBuffer();
		}
	}

	// Split incoming buffer by newlines, parse each as JSON, dispatch command
	private void ProcessBuffer()
	{
		while (true)
		{
			int nl = _buffer.IndexOf('\n');
			if (nl < 0) break;

			string line = _buffer[..nl].Trim();
			_buffer = _buffer[(nl + 1)..];
			if (string.IsNullOrEmpty(line)) continue;

			try
			{
				using var doc = JsonDocument.Parse(line);
				var root = doc.RootElement;
				string cmd = root.GetProperty("cmd").GetString() ?? "";
				var pars = root.TryGetProperty("params", out var p) ? p : default;
				HandleCommand(cmd, pars);
			}
			catch (Exception e)
			{
				SendResponse(new { ok = false, error = e.Message });
			}
		}
	}

	// Route command name to handler, send error for unknown commands
	private void HandleCommand(string cmd, JsonElement pars)
	{
		GD.Print($"[TestServer] cmd={cmd}");
		switch (cmd)
		{
			case "ping":
				SendResponse(new { ok = true, result = "pong" });
				break;

			case "reset":
				HandleReset();
				break;

			case "quit":
				SendResponse(new { ok = true, result = "quitting" });
				GetTree().Quit();
				break;

			case "screenshot":
				TakeScreenshot();
				break;

			case "import_file":
				HandleImportFile(pars);
				break;

			case "get_tracks":
				HandleGetTracks();
				break;

			case "set_selection":
				HandleSetSelection(pars);
				break;

			case "set_timeline_pos":
				HandleSetTimelinePos(pars);
				break;

			case "call":
				HandleCall(pars);
				break;

			case "click_button":
				HandleClickButton(pars);
				break;

			case "get_property":
				HandleGetProperty(pars);
				break;

			case "get_ui_state":
				HandleGetUIState();
				break;

			case "list_buttons":
				HandleListButtons();
				break;

			case "get_clip":
				HandleGetClip(pars);
				break;

			case "set_clip_property":
				HandleSetClipProperty(pars);
				break;

			case "export_and_wait":
				HandleExportAndWait();
				break;

			case "get_dependency_versions":
				HandleGetDependencyVersions();
				break;

			case "get_system_info":
				HandleGetSystemInfo();
				break;

			case "get_logs":
				HandleGetLogs();
				break;

			default:
				SendResponse(new { ok = false, error = $"Unknown command: {cmd}" });
				break;
		}
	}

	private void HandleReset()
	{
		var main = GetMain();
		if (main == null) { SendResponse(new { ok = false, error = "No MainWindow" }); return; }
		main.ResetProject();
		SendResponse(new { ok = true });
	}

	private MainWindow? GetMain() => GetTree().CurrentScene as MainWindow;

	// ── ping ─────────────────────────────────────────────────────────────────

	// ── screenshot ───────────────────────────────────────────────────────────
	private void TakeScreenshot()
	{
		var img = GetViewport().GetTexture().GetImage();
		img.SavePng("user://test_screenshot.png");
		string globalPath = ProjectSettings.GlobalizePath("user://test_screenshot.png");
		SendResponse(new { ok = true, result = new { path = globalPath } });
	}

	// ── import_file ──────────────────────────────────────────────────────────
	private void HandleImportFile(JsonElement pars)
	{
		var main = GetMain();
		if (main == null) { SendResponse(new { ok = false, error = "No MainWindow" }); return; }
		string? filePath = pars.TryGetProperty("path", out var p) ? p.GetString() : null;
		if (string.IsNullOrEmpty(filePath))
		{
			SendResponse(new { ok = false, error = "Missing path" });
			return;
		}
		main.Call("ImportFileInternal", filePath);
		SendResponse(new { ok = true, result = "importing" });
	}

	// ── get_tracks ───────────────────────────────────────────────────────────
	private void HandleGetTracks()
	{
		var main = GetMain();
		if (main == null) { SendResponse(new { ok = false, error = "No MainWindow" }); return; }

		var tracks = main.GetTracksData();
		SendResponse(new { ok = true, result = tracks });
	}

	// ── set_selection ────────────────────────────────────────────────────────
	private void HandleSetSelection(JsonElement pars)
	{
		var main = GetMain();
		if (main == null) { SendResponse(new { ok = false, error = "No MainWindow" }); return; }

		int trackIdx = pars.TryGetProperty("track", out var t) ? t.GetInt32() : -1;
		int clipIdx = pars.TryGetProperty("clip", out var c) ? c.GetInt32() : -1;
		main.SetSelection(trackIdx, clipIdx);
		SendResponse(new { ok = true });
	}

	// ── set_timeline_pos ─────────────────────────────────────────────────────
	private void HandleSetTimelinePos(JsonElement pars)
	{
		var main = GetMain();
		if (main == null) { SendResponse(new { ok = false, error = "No MainWindow" }); return; }

		double pos = pars.TryGetProperty("pos", out var p) ? p.GetDouble() : 0;
		main.SetTimelinePos(pos);
		SendResponse(new { ok = true });
	}

	// ── call ─────────────────────────────────────────────────────────────────
	private void HandleCall(JsonElement pars)
	{
		var main = GetMain();
		if (main == null) { SendResponse(new { ok = false, error = "No MainWindow" }); return; }

		string? method = pars.TryGetProperty("method", out var m) ? m.GetString() : null;
		if (string.IsNullOrEmpty(method))
		{
			SendResponse(new { ok = false, error = "Missing method" });
			return;
		}

		GD.Print($"[TestServer] call: {method}");
		main.CallAction(method);
		SendResponse(new { ok = true });
	}

	// ── click_button ─────────────────────────────────────────────────────────
	private void HandleClickButton(JsonElement pars)
	{
		string? text = pars.TryGetProperty("text", out var t) ? t.GetString() : null;
		if (string.IsNullOrEmpty(text))
		{
			SendResponse(new { ok = false, error = "Missing text" });
			return;
		}

		var button = FindButton(GetTree().Root, text);
		if (button != null)
		{
			button.EmitSignal(BaseButton.SignalName.Pressed);
			// Process any pending events
			awaitTo(0.1f, () => SendResponse(new { ok = true, result = $"clicked '{text}'" }));
			return;
		}
		SendResponse(new { ok = false, error = $"Button '{text}' not found" });
	}

	// ── get_property ─────────────────────────────────────────────────────────
	private void HandleGetProperty(JsonElement pars)
	{
		var main = GetMain();
		if (main == null) { SendResponse(new { ok = false, error = "No MainWindow" }); return; }

		string? name = pars.TryGetProperty("name", out var n) ? n.GetString() : null;
		if (string.IsNullOrEmpty(name))
		{
			SendResponse(new { ok = false, error = "Missing name" });
			return;
		}

		var known = new Dictionary<string, Func<object?>>
		{
			["_videoPath"] = () => main.GetVideoPath(),
			["_tracks_count"] = () => main.GetTrackCount(),
			["_currentState"] = () => main.GetCurrentState().ToString(),
			["_selTrackIdx"] = () => main.GetSelTrackIdx(),
			["_selClipIdx"] = () => main.GetSelClipIdx(),
			["_isPlaying"] = () => main.GetIsPlaying(),
		};

		if (known.TryGetValue(name, out var getter))
		{
			SendResponse(new { ok = true, result = getter()?.ToString() });
		}
		else
		{
			SendResponse(new { ok = false, error = $"Unknown property: {name}" });
		}
	}

	// ── helpers ──────────────────────────────────────────────────────────────

	private void SendResponse(object obj)
	{
		if (_client == null) return;
		string json = JsonSerializer.Serialize(obj) + "\n";
		var data = Encoding.UTF8.GetBytes(json);
		_client.PutData(data);
	}

	// Recursive search for a button by text in the scene tree
	private static Button? FindButton(Node parent, string text)
	{
		if (parent is Button btn && btn.Text == text)
			return btn;
		foreach (var child in parent.GetChildren())
		{
			var found = FindButton(child, text);
			if (found != null) return found;
		}
		return null;
	}

	private async void awaitTo(float seconds, Action cb)
	{
		await ToSignal(GetTree().CreateTimer(seconds), Timer.SignalName.Timeout);
		cb();
	}

	// ── get_ui_state ─────────────────────────────────────────────────────────
	private void HandleGetUIState()
	{
		var main = GetMain();
		if (main == null) { SendResponse(new { ok = false, error = "No MainWindow" }); return; }
		var buttons = new List<string>();
		CollectButtons(GetTree().Root, buttons);
		SendResponse(new { ok = true, result = new {
			state = main.GetCurrentState(),
			trackCount = main.GetTrackCount(),
			selTrack = main.GetSelTrackIdx(),
			selClip = main.GetSelClipIdx(),
			isPlaying = main.GetIsPlaying(),
			visibleButtons = buttons,
		}});
	}

	// ── list_buttons ─────────────────────────────────────────────────────────
	private void HandleListButtons()
	{
		var buttons = new List<string>();
		CollectButtons(GetTree().Root, buttons);
		SendResponse(new { ok = true, result = buttons });
	}

	// ── get_clip ──────────────────────────────────────────────────────────────
	private void HandleGetClip(JsonElement pars)
	{
		var main = GetMain();
		if (main == null) { SendResponse(new { ok = false, error = "No MainWindow" }); return; }

		int trackIdx = pars.TryGetProperty("track", out var t) ? t.GetInt32() : -1;
		int clipIdx = pars.TryGetProperty("clip", out var c) ? c.GetInt32() : -1;

		var data = main.GetClipData(trackIdx, clipIdx);
		if (data == null)
		{
			SendResponse(new { ok = false, error = $"Clip not found: track={trackIdx}, clip={clipIdx}" });
			return;
		}
		SendResponse(new { ok = true, result = data });
	}

	// ── set_clip_property ─────────────────────────────────────────────────────
	private void HandleSetClipProperty(JsonElement pars)
	{
		var main = GetMain();
		if (main == null) { SendResponse(new { ok = false, error = "No MainWindow" }); return; }

		string? prop = pars.TryGetProperty("property", out var p) ? p.GetString() : null;
		double value = pars.TryGetProperty("value", out var v) ? v.GetDouble() : 0;

		if (string.IsNullOrEmpty(prop))
		{
			SendResponse(new { ok = false, error = "Missing property" });
			return;
		}
		main.SetClipProperty(prop, value);
		SendResponse(new { ok = true });
	}

	// ── export_and_wait ───────────────────────────────────────────────────────
	private async void HandleExportAndWait()
	{
		var main = GetMain();
		if (main == null) { SendResponse(new { ok = false, error = "No MainWindow" }); return; }

		main.CallAction("OnExportPressed");

		// Poll for export completion (export hides the UI and calls SetStatus on finish)
		float waited = 0;
		while (waited < 120)
		{
			await ToSignal(GetTree().CreateTimer(1.0), Timer.SignalName.Timeout);
			waited += 1;
			// Check if MainWindow is back to a non-export state
			if (main.GetCurrentState() == "Edit" || main.GetCurrentState() == "Layout" || main.GetCurrentState() == "Import")
			{
				SendResponse(new { ok = true, result = $"export completed in ~{waited}s" });
				return;
			}
		}
		SendResponse(new { ok = false, error = "Export timed out after 120s" });
	}

	// ── get_dependency_versions ────────────────────────────────────────────
	private void HandleGetDependencyVersions()
	{
		string RunAndCapture(string exe, string args)
		{
			try
			{
				var psi = new ProcessStartInfo(exe, args)
				{
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				};
				using var proc = Process.Start(psi);
				if (proc == null) return "";
				string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
				proc.WaitForExit(5000);
				return output.Trim();
			}
			catch { return ""; }
		}

		string ffmpegVer = "";
		string raw = RunAndCapture("ffmpeg", "-version");
		if (!string.IsNullOrEmpty(raw))
		{
			// First line: "ffmpeg version 7.0 ..."
			int n = raw.IndexOf('\n');
			if (n >= 0) raw = raw[..n];
			// Strip everything before "version " and after the next space
			int vi = raw.IndexOf("version ", StringComparison.OrdinalIgnoreCase);
			if (vi >= 0)
			{
				string after = raw[(vi + 8)..].Trim();
				int sp = after.IndexOf(' ');
				ffmpegVer = sp >= 0 ? after[..sp] : after;
			}
		}

		string ytdlpVer = RunAndCapture("yt-dlp", "--version");
		string dotnetVer = RunAndCapture("dotnet", "--version");
		string godotVer = Engine.GetVersionInfo()["string"].AsString();

		SendResponse(new { ok = true, result = new
		{
			ffmpeg = ffmpegVer,
			ytdlp = ytdlpVer,
			dotnet = dotnetVer,
			godot = godotVer,
		}});
	}

	// ── get_system_info ─────────────────────────────────────────────────────
	private void HandleGetSystemInfo()
	{
		string ffmpegPath = "";
		try
		{
			var psi = new ProcessStartInfo("which", "ffmpeg")
			{
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			using var proc = Process.Start(psi);
			if (proc != null)
			{
				ffmpegPath = proc.StandardOutput.ReadToEnd().Trim();
				proc.WaitForExit(3000);
			}
		}
		catch
		{
			try
			{
				var psi = new ProcessStartInfo("where", "ffmpeg")
				{
					RedirectStandardOutput = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				};
				using var proc = Process.Start(psi);
				if (proc != null)
				{
					ffmpegPath = proc.StandardOutput.ReadToEnd().Trim();
					proc.WaitForExit(3000);
				}
			}
			catch { ffmpegPath = "not found"; }
		}

		SendResponse(new { ok = true, result = new
		{
			ram_mb = OS.GetStaticMemoryUsage() / (1024 * 1024),
			gpu = RenderingServer.GetVideoAdapterName(),
			ffmpeg_path = string.IsNullOrEmpty(ffmpegPath) ? "not found" : ffmpegPath,
			os = OS.GetName(),
			processor_count = System.Environment.ProcessorCount,
		}});
	}

	// ── get_logs ──────────────────────────────────────────────────────────────
	private void HandleGetLogs()
	{
		int count = LogBuffer.LineCount;
		var logs = LogBuffer.GetLogs();
		SendResponse(new { ok = true, result = new {
			lineCount = count,
			recent = logs.Count > 10 ? logs.TakeLast(10).ToList() : logs.ToList(),
		}});
	}

	// Collect all visible button texts from the scene tree
	private static void CollectButtons(Node parent, List<string> outList)
	{
		if (parent is Button btn && !string.IsNullOrEmpty(btn.Text))
			outList.Add(btn.Text);
		foreach (var child in parent.GetChildren())
			CollectButtons(child, outList);
	}
}
