using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Newtonsoft.Json.Linq;

namespace VsMcp.Services.Diagnostics
{
    // Sources synchronously publish their initial entries/snapshots on Subscribe. Reading the
    // immutable snapshots off the UI thread avoids creating/changing the Error List window.
    internal static class ErrorTableReader
    {
        internal static JObject Read(ITableDataSource[] sources, string severity, string project, string text,
            string code, string source, string producer, int maximum, CancellationToken token)
        {
            var results = new JArray();
            var warnings = new JArray();
            var stable = true;
            var truncated = false;
            var scanned = 0;
            foreach (var dataSource in sources)
            {
                token.ThrowIfCancellationRequested();
                using (var sink = new Sink())
                {
                    IDisposable subscription = null;
                    try
                    {
                        subscription = dataSource.Subscribe(sink);
                        sink.Visit(get =>
                        {
                            token.ThrowIfCancellationRequested();
                            if (++scanned > 100000) { truncated = true; return false; }
                            var level = get(StandardTableKeyNames.ErrorSeverity);
                            var kind = level is __VSERRORCATEGORY category
                                ? category == __VSERRORCATEGORY.EC_ERROR ? "error" : category == __VSERRORCATEGORY.EC_WARNING ? "warning" : "message"
                                : null;
                            string Value(string key) { var value = get(key); return value == null ? null : Convert.ToString(value); }
                            var description = Value(StandardTableKeyNames.FullText) ?? Value(StandardTableKeyNames.Text);
                            var itemProject = Convert.ToString(get(StandardTableKeyNames.ProjectName));
                            var itemCode = Convert.ToString(get(StandardTableKeyNames.ErrorCode));
                            var itemSource = Convert.ToString(get(StandardTableKeyNames.ErrorSource));
                            var buildTool = Convert.ToString(get(StandardTableKeyNames.BuildTool));
                            if (severity != null && !string.Equals(severity, kind, StringComparison.OrdinalIgnoreCase) ||
                                project != null && !string.Equals(project, itemProject, StringComparison.OrdinalIgnoreCase) ||
                                text != null && (description ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0 ||
                                code != null && !string.Equals(code, itemCode, StringComparison.OrdinalIgnoreCase) ||
                                source != null && !string.Equals(source, itemSource, StringComparison.OrdinalIgnoreCase) ||
                                producer != null && (buildTool ?? "").IndexOf(producer, StringComparison.OrdinalIgnoreCase) < 0 &&
                                    (dataSource.Identifier ?? "").IndexOf(producer, StringComparison.OrdinalIgnoreCase) < 0) return true;
                            if (results.Count == maximum) { truncated = true; return false; }
                            int? Position(string key)
                            {
                                var value = get(key);
                                if (value == null) return null;
                                var number = Convert.ToInt32(value);
                                return number < 0 || number == int.MaxValue ? (int?)null : number + 1;
                            }
                            results.Add(new JObject
                            {
                                ["severity"] = kind, ["description"] = description, ["project"] = itemProject,
                                ["path"] = Value(StandardTableKeyNames.DocumentName) ?? Value(StandardTableKeyNames.Path),
                                ["line"] = Position(StandardTableKeyNames.Line), ["column"] = Position(StandardTableKeyNames.Column),
                                ["code"] = itemCode, ["source"] = itemSource, ["buildTool"] = buildTool,
                                ["dataSource"] = dataSource.Identifier, ["sourceType"] = dataSource.SourceTypeIdentifier,
                            });
                            return true;
                        });
                        stable &= sink.IsStable;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        stable = false;
                        warnings.Add(new JObject { ["dataSource"] = dataSource.Identifier, ["error"] = ex.Message });
                    }
                    finally { subscription?.Dispose(); }
                }
                if (truncated) break;
            }
            return new JObject
            {
                ["items"] = results, ["truncated"] = truncated, ["isStable"] = stable,
                ["backend"] = "vsTable", ["extendedFieldsAvailable"] = true, ["warnings"] = warnings,
            };
        }

        private sealed class Sink : ITableDataSink, IDisposable
        {
            private readonly object gate = new object();
            private readonly List<ITableEntry> entries = new List<ITableEntry>();
            private readonly List<ITableEntriesSnapshot> snapshots = new List<ITableEntriesSnapshot>();
            private readonly List<ITableEntriesSnapshotFactory> factories = new List<ITableEntriesSnapshotFactory>();
            private bool stable;
            public bool IsStable { get { lock (gate) return stable; } set { lock (gate) stable = value; } }
            public void AddEntries(IReadOnlyList<ITableEntry> value, bool clear) { lock (gate) { if (clear) entries.Clear(); entries.AddRange(value); } }
            public void RemoveEntries(IReadOnlyList<ITableEntry> value) { lock (gate) foreach (var item in value) entries.Remove(item); }
            public void ReplaceEntries(IReadOnlyList<ITableEntry> oldValue, IReadOnlyList<ITableEntry> newValue) { lock (gate) { RemoveEntries(oldValue); AddEntries(newValue, false); } }
            public void RemoveAllEntries() { lock (gate) entries.Clear(); }
            public void AddSnapshot(ITableEntriesSnapshot value, bool clear) { lock (gate) { if (clear) snapshots.Clear(); snapshots.Add(value); } }
            public void RemoveSnapshot(ITableEntriesSnapshot value) { lock (gate) snapshots.Remove(value); }
            public void RemoveAllSnapshots() { lock (gate) snapshots.Clear(); }
            public void ReplaceSnapshot(ITableEntriesSnapshot oldValue, ITableEntriesSnapshot newValue) { lock (gate) { RemoveSnapshot(oldValue); AddSnapshot(newValue, false); } }
            public void AddFactory(ITableEntriesSnapshotFactory value, bool clear) { lock (gate) { if (clear) factories.Clear(); factories.Add(value); } }
            public void RemoveFactory(ITableEntriesSnapshotFactory value) { lock (gate) factories.Remove(value); }
            public void ReplaceFactory(ITableEntriesSnapshotFactory oldValue, ITableEntriesSnapshotFactory newValue) { lock (gate) { RemoveFactory(oldValue); AddFactory(newValue, false); } }
            public void FactorySnapshotChanged(ITableEntriesSnapshotFactory factory) { /* Capture current snapshot on read. */ }
            public void RemoveAllFactories() { lock (gate) factories.Clear(); }
            public void Visit(Func<Func<string, object>, bool> visit)
            {
                lock (gate)
                {
                    foreach (var entry in entries)
                        if (!visit(key => entry.TryGetValue(key, out var value) ? value : null)) return;
                    foreach (var snapshot in snapshots.Concat(factories.Select(f => f.GetCurrentSnapshot())).Where(s => s != null).Distinct())
                    {
                        snapshot.StartCaching();
                        try
                        {
                            for (var i = 0; i < snapshot.Count; i++)
                            {
                                var index = i;
                                if (!visit(key => snapshot.TryGetValue(index, key, out var value) ? value : null)) return;
                            }
                        }
                        finally { snapshot.StopCaching(); }
                    }
                }
            }
            public void Dispose() { lock (gate) { entries.Clear(); snapshots.Clear(); factories.Clear(); } }
        }
    }
}
