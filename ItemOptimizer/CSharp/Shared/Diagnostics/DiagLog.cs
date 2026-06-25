using System;
using System.IO;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Writes diagnostic lines to {ModDir}/diag.log.
    /// Thread-safe via lock. Auto-creates file, appends with timestamps.
    /// </summary>
    static class DiagLog
    {
        private static readonly object _lock = new();
        private static string _path;

        private static string GetPath()
        {
            if (_path != null) return _path;
            _path = ModPaths.ResolveData("diag.log");
            return _path;
        }

        private static void SafeLog(Action writeAction, string context)
        {
            try
            {
                writeAction();
            }
            catch (Exception ex)
            {
                DebugConsole.Log($"[DiagLog] Write failed ({context}): {ex.Message}");
            }
        }

        public static void Write(string message)
        {
            SafeLog(() =>
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
                lock (_lock)
                {
                    File.AppendAllText(GetPath(), line);
                }
            }, "Write");
        }

        /// <summary>Clear the log file.</summary>
        public static void Clear()
        {
            SafeLog(() =>
            {
                lock (_lock)
                {
                    File.WriteAllText(GetPath(), $"[{DateTime.Now:HH:mm:ss.fff}] === DiagLog cleared ===\n");
                }
            }, "Clear");
        }

        /// <summary>Write multiple lines at once (more efficient for bulk dumps).</summary>
        public static void WriteBlock(string header, string[] lines)
        {
            SafeLog(() =>
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] ── {header} ──\n");
                foreach (var line in lines)
                    sb.Append($"  {line}\n");
                lock (_lock)
                {
                    File.AppendAllText(GetPath(), sb.ToString());
                }
            }, "WriteBlock");
        }
    }
}
