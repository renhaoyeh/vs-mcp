using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using Newtonsoft.Json.Linq;
using VsMcp.Contracts;
using VsMcp.Mcp;
using VsMcp.Services.VisualStudio;

namespace VsMcp.Services.Build
{
    internal sealed class BuildSettingsService
    {
        private const string Prefix = "projectsAndSolutions.buildAndRun.";
        // SVsUnifiedSettingsManager's service identity. Its marker type is in a host-internal
        // assembly; the settings interfaces themselves are supplied by the official SDK.
        private static readonly Guid SettingsService = new Guid("e3684f31-344e-42ea-9047-b620fdc7ac25");
        private readonly VsContext vs;
        public BuildSettingsService(VsContext vs) { this.vs = vs; }

        private sealed class Binding
        {
            public PropertyInfo Field;
            public string Moniker;
            public Type StorageType;
            public Func<object> Read;
            public Action<object> Write;
        }

        public async Task<BuildSettingsOutput> ReadOrUpdateAsync(BuildSettingsValues requested, CancellationToken token)
        {
            await vs.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (requested != null)
            {
                Validate(requested);
                if (BuildRunner.IsRunning || BuildRunner.IsBuildManagerBusy())
                    throw Error(ToolErrorCode.BuildInProgress, "Wait for the build to finish before changing build settings.");
                if (vs.TestsRunning() || vs.DebugLaunchPending || vs.Dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
                    throw Error(ToolErrorCode.ExecutionError, "Changing build settings requires an idle IDE with no tests, debugging or pending launch.");
            }

            var manager = ServiceProvider.GlobalProvider.GetService(SettingsService) as ISettingsManager;
            var bindings = Bindings(manager);
            var before = Snapshot(bindings);
            if (requested == null) return before;

            var changes = bindings.Where(b => b.Field.GetValue(requested) != null).ToList();
            // Check every requested field before queuing or applying anything.
            foreach (var b in changes)
                if (before.UnsupportedSettings.TryGetValue(Name(b), out var reason))
                    throw Error(ToolErrorCode.NotSupported, Name(b) + ": " + reason);

            var after = before;
            var changed = changes.Where(b => !Equals(b.Field.GetValue(requested), b.Field.GetValue(before))).ToList();
            if (changed.Count == 0)
            {
                after.UpdateStatus = BuildSettingsUpdateStatus.Unchanged;
                after.Previous = CopyValues(before);
                after.PreviousMaxParallelProjectBuilds = before.MaxParallelProjectBuilds;
                return after;
            }

            token.ThrowIfCancellationRequested();
            // Once mutation begins, finish readback even if the caller disconnects/cancels.
            // Do not automatically roll back or retry: a provider may have applied a value already.
            var status = BuildSettingsUpdateStatus.Applied;
            try
            {
                var unified = changed.Where(b => b.Moniker != null).ToList();
                if (unified.Count != 0)
                {
                    var writer = manager.GetWriter("VS MCP");
                    foreach (var b in unified)
                    {
                        var value = ToStorage(b, b.Field.GetValue(requested));
                        var queued = b.StorageType == typeof(bool) ? writer.EnqueueChange(b.Moniker, (bool)value)
                            : b.StorageType == typeof(int) ? writer.EnqueueChange(b.Moniker, (int)value)
                            : writer.EnqueueChange(b.Moniker, (string)value);
                        if (queued.Outcome != SettingChangeOutcome.PendingCommit)
                            throw new InvalidOperationException(Name(b) + ": " + queued.Outcome + ". " + queued.Message);
                    }
                    var commit = writer.RequestCommit("Update build and run preferences");
                    if (commit.Outcome == SettingCommitOutcome.PendingApproval)
                        status = BuildSettingsUpdateStatus.PendingApproval;
                    else if (commit.Outcome != SettingCommitOutcome.Success)
                        throw new InvalidOperationException("Settings commit: " + commit.Outcome + ". " + commit.Message);
                }
                // Do not apply the DTE portion while a native settings approval is pending.
                if (status != BuildSettingsUpdateStatus.PendingApproval)
                    foreach (var b in changed.Where(b => b.Moniker == null))
                        b.Write(b.Field.GetValue(requested));

                after = Snapshot(bindings);
                if (status != BuildSettingsUpdateStatus.PendingApproval &&
                    changed.Any(b => after.UnsupportedSettings.ContainsKey(Name(b)) || !Equals(b.Field.GetValue(requested), b.Field.GetValue(after))))
                    throw new InvalidOperationException("Visual Studio did not retain every requested value.");
            }
            catch (Exception ex)
            {
                after = Snapshot(bindings);
                throw Error(ToolErrorCode.ExecutionError,
                    "Settings update was not confirmed and may be partial. Read get_build_settings before retrying. " + ex.Message,
                    new JObject { ["previous"] = ToolJson.ToJObject(CopyValues(before)), ["current"] = ToolJson.ToJObject(after) });
            }
            after.Previous = CopyValues(before);
            after.PreviousMaxParallelProjectBuilds = before.MaxParallelProjectBuilds;
            after.UpdateStatus = status;
            return after;
        }

        private List<Binding> Bindings(ISettingsManager manager)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<Binding>();
            void Unified(string field, string key, Type type, string fallback = null)
            {
                var b = new Binding { Field = typeof(BuildSettingsValues).GetProperty(field), Moniker = key == null ? null : Prefix + key, StorageType = type };
                b.Read = () =>
                {
                    if (manager == null || b.Moniker == null) throw new NotSupportedException("This host does not expose a registered setting for this option.");
                    var read = manager.GetReader().GetValue(b.Moniker, type, SettingReadOptions.RequireValidation);
                    if (read.Outcome != SettingRetrievalOutcome.Success) throw new NotSupportedException(read.Outcome + ". " + read.Message);
                    var target = Nullable.GetUnderlyingType(b.Field.PropertyType);
                    return target.IsEnum ? Enum.Parse(target, (string)read.Value, true) : read.Value;
                };
                // ConcurrentBuilds is the actual DTE automation property, not MaximumParallelProjectBuilds.
                if (fallback != null && manager == null)
                {
                    b.Moniker = null;
                    b.Read = () => Convert.ToInt32(ReadDte("Environment", "ProjectsAndSolution", fallback));
                    b.Write = value => WriteDte("Environment", "ProjectsAndSolution", fallback, Convert.ToInt16(value));
                }
                result.Add(b);
            }
            Unified(nameof(BuildSettingsValues.MaxParallelProjectBuilds), "maxParallelBuilds", typeof(int), "ConcurrentBuilds");
            Unified(nameof(BuildSettingsValues.OutputVerbosity), "outputVerbosity", typeof(string));
            Unified(nameof(BuildSettingsValues.LogFileVerbosity), "outputFileVerbosity", typeof(string));
            Unified(nameof(BuildSettingsValues.OnlyBuildStartupProjectsAndDependencies), "onRunOnlyBuildStartupProjects", typeof(bool));
            Unified(nameof(BuildSettingsValues.OnRunWhenOutOfDate), "onRunWhenOutOfDate", typeof(string));
            Unified(nameof(BuildSettingsValues.OnRunWhenBuildErrors), "onRunWhenBuildErrors", typeof(string));
            Unified(nameof(BuildSettingsValues.LowPriorityBuild), "lowPriorityBuild", typeof(bool));
            Unified(nameof(BuildSettingsValues.StopSolutionBuildAfterFirstFailure), FindStopSetting(), typeof(bool));
            foreach (var pair in new[] { new[] { nameof(BuildSettingsValues.CppBuildTiming), "BuildTiming" }, new[] { nameof(BuildSettingsValues.CppBuildLogging), "BuildLogging" } })
            {
                var property = pair[1];
                result.Add(new Binding
                {
                    Field = typeof(BuildSettingsValues).GetProperty(pair[0]),
                    Read = () => Convert.ToBoolean(ReadDte("Projects", "VCGeneral", property)),
                    Write = value => WriteDte("Projects", "VCGeneral", property, value),
                });
            }
            return result;
        }

        private object ReadDte(string category, string page, string property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return vs.Dte.Properties[category, page].Item(property).Value;
        }

        private void WriteDte(string category, string page, string property, object value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            vs.Dte.Properties[category, page].Item(property).Value = value;
        }

        private static string FindStopSetting()
        {
            // The option was added after the baseline SDK. Discover the unique registered
            // boolean in this host's Build and Run schema instead of inventing a store key.
            try
            {
                var root = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                var schema = JObject.Parse(File.ReadAllText(Path.Combine(root, "CommonExtensions", "Platform", "Shell", "Solution.registration.json")));
                var properties = schema["properties"] as JObject;
                if (properties == null) return null;
                var matches = properties.Properties().Where(p => p.Name.StartsWith(Prefix, StringComparison.Ordinal) &&
                    (string)p.Value["type"] == "boolean" &&
                    p.Name.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    p.Name.IndexOf("first", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (p.Name.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 || p.Name.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                return matches.Count == 1 ? matches[0].Name.Substring(Prefix.Length) : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException || ex is System.ComponentModel.Win32Exception)
            { return null; }
        }

        private BuildSettingsOutput Snapshot(List<Binding> bindings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new BuildSettingsOutput { Instance = vs.ReadInstance(), UpdateStatus = BuildSettingsUpdateStatus.Read };
            foreach (var b in bindings)
            {
                try { b.Field.SetValue(result, b.Read()); }
                catch (Exception ex) { result.UnsupportedSettings[Name(b)] = ex.Message; }
            }
            return result;
        }

        private static string Name(Binding b) => char.ToLowerInvariant(b.Field.Name[0]) + b.Field.Name.Substring(1);
        private static object ToStorage(Binding b, object value) => b.StorageType == typeof(string)
            ? char.ToLowerInvariant(value.ToString()[0]) + value.ToString().Substring(1) : value;
        private static BuildSettingsValues CopyValues(BuildSettingsValues source)
        {
            var result = new BuildSettingsValues();
            foreach (var p in typeof(BuildSettingsValues).GetProperties()) p.SetValue(result, p.GetValue(source));
            return result;
        }
        private void Validate(BuildSettingsValues values)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!typeof(BuildSettingsValues).GetProperties().Any(p => p.GetValue(values) != null))
                throw Error(ToolErrorCode.InvalidArgument, "Specify at least one build setting.");
            if (values.MaxParallelProjectBuilds.HasValue && values.MaxParallelProjectBuilds < 1)
                throw Error(ToolErrorCode.InvalidArgument, "maxParallelProjectBuilds must be positive.");
            foreach (var p in typeof(BuildSettingsValues).GetProperties())
            {
                var value = p.GetValue(values);
                var type = Nullable.GetUnderlyingType(p.PropertyType);
                if (value != null && type.IsEnum && !Enum.IsDefined(type, value))
                    throw Error(ToolErrorCode.InvalidArgument, "Invalid value for " + p.Name + ".");
            }
        }
        private McpToolException Error(ToolErrorCode code, string message, JObject details = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            details = details ?? new JObject();
            details["instance"] = vs.Instance();
            return new McpToolException(code, message, details);
        }
    }
}
