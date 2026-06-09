// Lightweight system resource monitor that logs RAM, CPU, and GC stats.
// Reads /proc/meminfo on Linux for system-level memory.

using Godot;
using System;
using System.Diagnostics;
using System.IO;

namespace VelosCCS;

public static class SystemResources
{
	private static readonly long PageSize = System.Environment.SystemPageSize;
	private static Process? _process;
	private static double _lastCpuTime;
	private static DateTime _lastCpuSample = DateTime.MinValue;

	public static void Log(string label)
	{
		try
		{
			if (_process == null || _process.HasExited)
			{
				_process = Process.GetCurrentProcess();
				_process.Refresh();
			}

			_process.Refresh();

			double wsMB = _process.WorkingSet64 / 1048576.0;
			double privMB = _process.PrivateMemorySize64 / 1048576.0;
			double cpuSec = _process.TotalProcessorTime.TotalSeconds;
			double cpuPct = 0;
			if (_lastCpuSample != DateTime.MinValue)
			{
				double elapsed = (DateTime.UtcNow - _lastCpuSample).TotalSeconds;
				if (elapsed > 0)
					cpuPct = (cpuSec - _lastCpuTime) / elapsed * 100 / System.Environment.ProcessorCount;
			}
			_lastCpuTime = cpuSec;
			_lastCpuSample = DateTime.UtcNow;

			long managedMB = GC.GetTotalMemory(false) / 1048576;
			int gen0 = GC.CollectionCount(0);
			int gen1 = GC.CollectionCount(1);
			int gen2 = GC.CollectionCount(2);
			int threadCount = _process.Threads.Count;

			// System memory from /proc/meminfo
			long sysTotalMB = 0, sysAvailMB = 0, sysFreeMB = 0;
			try
			{
				foreach (var line in File.ReadLines("/proc/meminfo"))
				{
					if (line.StartsWith("MemTotal:"))  sysTotalMB = ParseProcMem(line);
					if (line.StartsWith("MemAvailable:")) sysAvailMB = ParseProcMem(line);
					if (line.StartsWith("MemFree:")) sysFreeMB = ParseProcMem(line);
				}
			}
			catch { }

			long usedMB = sysTotalMB - sysAvailMB;
			double pctUsed = sysTotalMB > 0 ? (double)usedMB / sysTotalMB * 100 : 0;

			VelosCCS.Log.Print($"[SysRes] {label} — sys: {usedMB}/{sysTotalMB} MB ({pctUsed:F0}%) avail: {sysAvailMB} MB | "
			        + $"proc: {wsMB:F0} MB ws, {privMB:F0} MB priv | "
			        + $"cpu: {cpuSec:F1}s tot ({cpuPct:F0}% avg) | "
			        + $"heap: {managedMB} MB | "
			        + $"GC: {gen0}/{gen1}/{gen2} | "
			        + $"threads: {threadCount}");
		}
		catch (Exception e)
		{
			VelosCCS.Log.Print($"[SysRes] {label} — failed: {e.Message}");
		}
	}

	private static long ParseProcMem(string line)
	{
		var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
			return kb / 1024;
		return 0;
	}
}
