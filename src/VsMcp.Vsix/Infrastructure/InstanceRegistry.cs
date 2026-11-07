#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using VsMcp.Contracts;

namespace VsMcp.Infrastructure
{
    internal sealed class InstanceRegistry
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        private readonly string directory;
        private readonly Func<InstanceRegistration, bool> processMatches;

        public InstanceRegistry(string? directory = null, Func<InstanceRegistration, bool>? processMatches = null)
        {
            this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VsMcp", "instances");
            this.processMatches = processMatches ?? ProcessMatches;
        }

        public IDisposable Publish(InstanceRegistration entry)
        {
            if (!Valid(entry)) throw new ArgumentException("Invalid instance registration.", nameof(entry));
            Directory.CreateDirectory(directory);
            var path = EntryPath(entry);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(entry, Json), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new RegistrationLease(this, entry);
        }

        public IReadOnlyList<InstanceRegistration> ReadAll()
        {
            if (!Directory.Exists(directory)) return Array.Empty<InstanceRegistration>();
            var entries = new List<InstanceRegistration>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var entry = ReadFile(path);
                if (entry == null || path != EntryPath(entry)) continue;
                try
                {
                    if (processMatches(entry)) entries.Add(entry);
                    else Remove(entry);
                }
                catch (System.ComponentModel.Win32Exception) { } // Inspection denied: preserve the record.
            }
            return entries.OrderBy(entry => entry.Pid).ToArray();
        }

        public InstanceRegistration? Find(int pid, string instanceId) => ReadAll().FirstOrDefault(entry => entry.Pid == pid && entry.InstanceId == instanceId);

        public bool IsCurrent(InstanceRegistration entry)
        {
            var current = ReadFile(EntryPath(entry));
            try
            {
                return current != null && current.Pid == entry.Pid && current.InstanceId == entry.InstanceId && current.Port == entry.Port
                    && current.ProcessStartTimeUtcTicks == entry.ProcessStartTimeUtcTicks && processMatches(current);
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        private string EntryPath(InstanceRegistration entry) => Path.Combine(directory, entry.Pid + "." + entry.InstanceId + ".json");

        private static InstanceRegistration? ReadFile(string path)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<InstanceRegistration>(File.ReadAllText(path), Json);
                return Valid(entry) ? entry : null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException) { return null; }
        }

        private static bool Valid(InstanceRegistration? entry) => entry != null && entry.Pid > 0
            && entry.ProcessStartTimeUtcTicks > 0 && entry.Port > 0 && entry.Port <= 65535 && Guid.TryParse(entry.InstanceId, out _);

        private static bool ProcessMatches(InstanceRegistration entry)
        {
            try
            {
                using (var process = Process.GetProcessById(entry.Pid))
                    return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == entry.ProcessStartTimeUtcTicks;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private void Remove(InstanceRegistration entry)
        {
            try
            {
                var path = EntryPath(entry);
                if (ReadFile(path)?.InstanceId == entry.InstanceId) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class RegistrationLease : IDisposable
        {
            private readonly InstanceRegistry registry;
            private readonly InstanceRegistration entry;
            public RegistrationLease(InstanceRegistry registry, InstanceRegistration entry) { this.registry = registry; this.entry = entry; }
            public void Dispose() => registry.Remove(entry);
        }
    }
}
