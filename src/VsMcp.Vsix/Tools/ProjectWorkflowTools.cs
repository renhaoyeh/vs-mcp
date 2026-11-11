using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.VCProjectEngine;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VsMcp.Contracts;
using VsMcp.Mcp;
using VsMcp.Services.VisualStudio;

namespace VsMcp.Tools
{
    /// <summary>Native C++ project configuration, launch, output and item workflows.</summary>
    internal sealed class ProjectWorkflowTools
    {
        private readonly VsContext _vs;
        public ProjectWorkflowTools(VsContext vs) { _vs = vs; }

        [McpServerTool(Name = "cpp_configuration_get", Title = "Get C++ configuration", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Read native Visual C++ configuration properties without activating a configuration. Supply an exact configuration/platform pair, or omit both to use the project's actual mapping for the active solution configuration. Optional file selects one exact absolute C++ source path and returns the native per-file compiler settings. Reports raw and evaluated supported include paths, defines, libraries, output paths, toolset, C++ language standard and precompiled header settings. Does not modify the project.")]
        public async Task<ProjectWorkflowOutput<CppConfigurationOutput>> GetCppConfigurationTool(
            [Description("Exact project name or unique name.")] string project,
            [Description("Exact project configuration name, for example Debug. If both configuration and platform are omitted, Visual Studio's actual mapping for the active solution configuration is used without switching it.")] string configuration = null,
            [Description("Exact project platform name, for example x64. If both configuration and platform are omitted, Visual Studio's actual mapping for the active solution configuration is used without switching it.")] string platform = null,
            [Description("Optional exact absolute path to one C++ source file included in the project. Reads the file configuration associated with the selected project configuration/platform.")] string file = null,
            CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var selected = Select(project, configuration, platform);
            var properties = new Dictionary<string, CppPropertyValue>(StringComparer.OrdinalIgnoreCase);
            object scope = selected.ConfigurationObject;
            var filePath = null as string;
            if (!string.IsNullOrWhiteSpace(file))
            {
                if (!Path.IsPathRooted(file)) throw Failure(ToolErrorCode.InvalidArgument, "File must be an exact absolute path.");
                var vcFile = FindCppFile(selected, Path.GetFullPath(file));
                filePath = vcFile.FullPath;
                var fileConfiguration = FindFileConfiguration(vcFile, selected);
                if (fileConfiguration == null) throw Failure(ToolErrorCode.NotSupported, "Visual C++ did not expose a file configuration for the selected configuration/platform.");
                var fileTool = fileConfiguration.Tool;
                if (fileTool == null) throw Failure(ToolErrorCode.NotSupported, "The selected file has no native compiler tool configuration.");
                ReadFile(properties, fileConfiguration, fileTool, "additionalIncludeDirectories", "AdditionalIncludeDirectories");
                ReadFile(properties, fileConfiguration, fileTool, "preprocessorDefinitions", "PreprocessorDefinitions");
                ReadFile(properties, fileConfiguration, fileTool, "languageStandard", "LanguageStandard");
                ReadFile(properties, fileConfiguration, fileTool, "usePrecompiledHeader", "PrecompiledHeader");
                ReadFile(properties, fileConfiguration, fileTool, "precompiledHeaderThrough", "PrecompiledHeaderFile");
            }
            else
            {
                var cl = GetTool(scope, "VCCLCompilerTool", "CL");
                var linker = GetTool(scope, "VCLinkerTool", "Link");
                var librarian = GetTool(scope, "VCLibrarianTool", "Lib");
                Read(properties, selected.ConfigurationObject, cl, "additionalIncludeDirectories", "AdditionalIncludeDirectories", "AdditionalIncludeDirectories");
                Read(properties, selected.ConfigurationObject, cl, "preprocessorDefinitions", "PreprocessorDefinitions", "PreprocessorDefinitions");
                Read(properties, selected.ConfigurationObject, linker ?? librarian, "additionalDependencies", "AdditionalDependencies", "AdditionalDependencies");
                Read(properties, selected.ConfigurationObject, linker ?? librarian, "additionalLibraryDirectories", "AdditionalLibraryDirectories", "AdditionalLibraryDirectories");
                Read(properties, selected.ConfigurationObject, linker, "outputFile", "OutputFile", "OutputFile");
                Read(properties, selected.ConfigurationObject, linker, "programDatabaseFile", "ProgramDatabaseFile", "ProgramDatabaseFile");
                Read(properties, selected.ConfigurationObject, null, "outputDirectory", "OutputDirectory", "OutDir");
                Read(properties, selected.ConfigurationObject, null, "intermediateDirectory", "IntermediateDirectory", "IntDir");
                Read(properties, selected.ConfigurationObject, null, "platformToolset", "PlatformToolset", "PlatformToolset");
                Read(properties, selected.ConfigurationObject, cl, "languageStandard", "LanguageStandard", "LanguageStandard");
                Read(properties, selected.ConfigurationObject, cl, "usePrecompiledHeader", "UsePrecompiledHeader", "PrecompiledHeader");
                Read(properties, selected.ConfigurationObject, cl, "precompiledHeaderThrough", "PrecompiledHeaderThrough", "PrecompiledHeaderFile");
            }
            return Wrap(new CppConfigurationOutput { Project = selected.UniqueName, File = filePath, Configuration = selected.ConfigurationName, Platform = selected.PlatformName, Properties = properties });
        }

        [McpServerTool(Name = "cpp_launch_settings_get", Title = "Get C++ launch settings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Read the native Visual C++ debugger settings for an exact project configuration/platform. Does not change active configuration or startup project.")]
        public async Task<ProjectWorkflowOutput<CppLaunchSettingsOutput>> GetCppLaunchSettingsTool(string project, string configuration = null, string platform = null, CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var selected = Select(project, configuration, platform);
            return Wrap(new CppLaunchSettingsOutput { Project = selected.UniqueName, Configuration = selected.ConfigurationName, Platform = selected.PlatformName, Values = ReadLaunch(selected.ConfigurationObject) });
        }

        [McpServerTool(Name = "cpp_launch_settings_set", Title = "Set C++ launch settings", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
        [Description("Update specified native Visual C++ debugger settings stored for one exact project configuration/platform. Requires an idle IDE and saved documents, solution and project. Omitted fields are preserved. Validates all requested properties before writing and returns previous and read-back values. It does not change the active configuration or startup project. After an error or interruption, read the settings before retrying.")]
        public async Task<ProjectWorkflowOutput<CppLaunchSettingsOutput>> SetCppLaunchSettingsTool(
            string project, string configuration = null, string platform = null,
            [Description("Executable command to launch; omit to preserve.")] string command = null,
            [Description("Command line arguments; omit to preserve.")] string arguments = null,
            [Description("Working directory; omit to preserve.")] string workingDirectory = null,
            [Description("Debuggee environment as newline-delimited NAME=VALUE entries; omit to preserve.")] string environment = null,
            [Description("Whether to merge the specified environment with the inherited environment; omit to preserve.")] bool? mergeEnvironment = null,
            [Description("Local debug engine type: auto, nativeOnly, managedOnly, mixed, managedCore or nativeWithManagedCore; omit to preserve.")] string debuggerType = null,
            CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var selected = Select(project, configuration, platform);
            IdleAndSaved(selected.Project);
            var settings = GetRuleStorage(selected.ConfigurationObject, "WindowsLocalDebugger");
            if (settings == null) throw Failure(ToolErrorCode.NotSupported, "Visual Studio does not expose the WindowsLocalDebugger property rule for this configuration.");
            var requested = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (command != null) requested["ApplicationCommand"] = command;
            if (arguments != null) requested["CommandArguments"] = arguments;
            if (workingDirectory != null) requested["WorkingDirectory"] = workingDirectory;
            if (environment != null) { ValidateEnvironment(environment); requested["Environment"] = environment; }
            if (mergeEnvironment.HasValue) requested["EnvironmentMerge"] = mergeEnvironment.Value;
            if (debuggerType != null) requested["DebuggerType"] = ParseDebuggerType(debuggerType);
            if (requested.Count == 0) throw Failure(ToolErrorCode.InvalidArgument, "Supply at least one launch setting to update.");
            var previous = ReadLaunch(settings);
            foreach (var key in requested.Keys)
                if (!LaunchPropertyNames.Contains(key)) throw Failure(ToolErrorCode.NotSupported, "Native debugger setting is not supported: " + key);
            foreach (var pair in requested) settings.SetPropertyValue(LaunchRuleProperty(pair.Key), Convert.ToString(pair.Value));
            selected.Project.Save(selected.Project.FullName);
            return Wrap(new CppLaunchSettingsOutput { Project = selected.UniqueName, Configuration = selected.ConfigurationName, Platform = selected.PlatformName, Previous = previous, Values = ReadLaunch(settings) });
        }

        [McpServerTool(Name = "project_outputs", Title = "Get project outputs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
        [Description("Read project output paths reported by the selected Visual Studio project configuration without activating it. Supply an exact project configuration/platform pair, or omit both to use the project's actual mapping for the active solution configuration. Includes bounded output-group paths and, for C++, primary output, PDB, import library and configured output directories. Paths come from Visual Studio project APIs and are not inferred from files on disk. Unsupported values are null and identified in unavailable.")]
        public async Task<ProjectWorkflowOutput<ProjectOutputsOutput>> ProjectOutputsTool(string project, string configuration = null, string platform = null, CancellationToken cancellationToken = default)
        {
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var selectedProject = SelectProject(project);
            var dteConfiguration = SelectDteConfiguration(selectedProject.Project, configuration, platform, out var actualConfiguration, out var actualPlatform);
            var unavailable = new Dictionary<string, string>();
            var data = new ProjectOutputsOutput { Project = selectedProject.UniqueName, Configuration = actualConfiguration, Platform = actualPlatform, Unavailable = unavailable };
            var nativeProject = selectedProject.Project.Object as VCProject;
            if (nativeProject != null)
            {
                var selected = Select(project, actualConfiguration, actualPlatform);
                var config = selected.ConfigurationObject;
                data.PrimaryOutput = Output(config, "primaryOutput", unavailable, "primaryOutput");
                data.ProgramDatabase = Output(config, "programDatabase", unavailable, "programDatabase");
                data.ImportLibrary = Output(config, "importLibrary", unavailable, "importLibrary");
                data.OutputDirectory = Output(config, "outputDirectory", unavailable, "outputDirectory");
                data.IntermediateDirectory = Output(config, "intermediateDirectory", unavailable, "intermediateDirectory");
            }
            else
            {
                unavailable["importLibrary"] = "This project configuration does not expose a native import-library output.";
                unavailable["outputDirectory"] = "Visual Studio did not expose an absolute output directory for this project system.";
                unavailable["intermediateDirectory"] = "Visual Studio did not expose an absolute intermediate directory for this project system.";
            }
            ReadOutputGroups(dteConfiguration, data, unavailable);
            return Wrap(data);
        }

        private EnvDTE.Configuration SelectDteConfiguration(Project project, string configuration, string platform, out string actualConfiguration, out string actualPlatform)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnvDTE.Configuration selected;
            if (string.IsNullOrWhiteSpace(configuration) && string.IsNullOrWhiteSpace(platform))
                selected = project.ConfigurationManager.ActiveConfiguration;
            else if (string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(platform))
                throw Failure(ToolErrorCode.InvalidArgument, "Supply both configuration and platform, or omit both to use the active solution mapping.");
            else
                selected = project.ConfigurationManager.Item(configuration, platform);
            if (selected == null) throw Failure(ToolErrorCode.NotSupported, "Visual Studio did not expose the selected project's mapped configuration.");
            actualConfiguration = selected.ConfigurationName;
            actualPlatform = selected.PlatformName;
            if (!string.IsNullOrWhiteSpace(configuration) && (!Same(configuration, actualConfiguration) || !Same(platform, actualPlatform)))
                throw Failure(ToolErrorCode.ConfigurationNotFound, "Visual Studio did not return the exact requested project configuration/platform pair.");
            return selected;
        }

        private static void ReadOutputGroups(EnvDTE.Configuration configuration, ProjectOutputsOutput output, Dictionary<string, string> unavailable)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            const int maxGroups = 32;
            const int maxPathsPerGroup = 256;
            try
            {
                var groups = configuration.OutputGroups;
                var count = 0;
                foreach (OutputGroup group in groups)
                {
                    if (++count > maxGroups) { unavailable["outputGroups"] = "Visual Studio returned more than 32 output groups; the result was truncated."; break; }
                    var result = new ProjectOutputGroupInfo { CanonicalName = group.CanonicalName ?? "", DisplayName = group.DisplayName ?? "" };
                    result.Role = OutputRole(result.CanonicalName);
                    var urls = group.FileURLs;
                    if (urls is Array urlArray)
                    {
                        foreach (var url in urlArray)
                        {
                            if (result.Paths.Count == maxPathsPerGroup) { unavailable["outputGroups"] = "An output group exceeded 256 paths; the group was truncated."; break; }
                            if (url != null && Uri.TryCreate(Convert.ToString(url), UriKind.Absolute, out var uri) && uri.IsFile) result.Paths.Add(uri.LocalPath);
                        }
                    }
                    if (result.Paths.Count == 0)
                    {
                        var paths = group.FileNames;
                        if (paths is Array pathArray)
                        {
                            foreach (var path in pathArray)
                            {
                                if (result.Paths.Count == maxPathsPerGroup) { unavailable["outputGroups"] = "An output group exceeded 256 paths; the group was truncated."; break; }
                                if (path != null && !string.IsNullOrWhiteSpace(Convert.ToString(path))) result.Paths.Add(Convert.ToString(path));
                            }
                        }
                        else if (paths != null && !string.IsNullOrWhiteSpace(Convert.ToString(paths))) result.Paths.Add(Convert.ToString(paths));
                    }
                    output.OutputGroups.Add(result);
                }
            }
            catch (Exception ex) { unavailable["outputGroups"] = ex.GetBaseException().Message; }

            var binaryGroup = output.OutputGroups.FirstOrDefault(g => g.Role == "binary" && g.Paths.Count > 0);
            if (output.PrimaryOutput == null && binaryGroup != null) output.PrimaryOutput = binaryGroup.Paths[0];
            if (output.ProgramDatabase == null)
                output.ProgramDatabase = output.OutputGroups.SelectMany(g => g.Paths).FirstOrDefault(p => string.Equals(Path.GetExtension(p), ".pdb", StringComparison.OrdinalIgnoreCase));
            if (output.PrimaryOutput != null) unavailable.Remove("primaryOutput");
            if (output.ProgramDatabase != null) unavailable.Remove("programDatabase");
            if (output.PrimaryOutput == null && !unavailable.ContainsKey("primaryOutput")) unavailable["primaryOutput"] = "Visual Studio returned no primary output path.";
            if (output.ProgramDatabase == null && !unavailable.ContainsKey("programDatabase")) unavailable["programDatabase"] = "Visual Studio returned no program database path.";
        }

        private static string OutputRole(string canonicalName)
        {
            if (string.Equals(canonicalName, "Built", StringComparison.OrdinalIgnoreCase)) return "binary";
            if (string.Equals(canonicalName, "DebugSymbols", StringComparison.OrdinalIgnoreCase)) return "debugSymbols";
            if (string.Equals(canonicalName, "Documentation", StringComparison.OrdinalIgnoreCase)) return "documentation";
            if (string.Equals(canonicalName, "ContentFiles", StringComparison.OrdinalIgnoreCase)) return "content";
            return "other";
        }

        [McpServerTool(Name = "project_item_add", Title = "Add existing project item", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
        [Description("Add one existing file reference to a loaded project's project-system state, optionally under an existing project folder/filter. Requires an idle IDE and saved documents, solution and project. Saves only the target project file after the change. The file must already exist; the source file contents are never copied or changed. Returns the resulting hierarchy item path and physical path. Does not alter configuration or startup project.")]
        public async Task<ProjectWorkflowOutput<ProjectItemMutationOutput>> AddProjectItemTool(string project, string filePath, string filter = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath) || !File.Exists(filePath)) throw Failure(ToolErrorCode.InvalidArgument, "An existing absolute file path is required.");
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var selected = SelectProject(project);
            IdleAndSaved(selected.Project);
            var fullPath = Path.GetFullPath(filePath);
            var found = WalkItems(selected.Project.ProjectItems, "").FirstOrDefault(i => SamePath(i.FilePath, fullPath));
            if (found != null) throw Failure(ToolErrorCode.InvalidArgument, "The file is already included in the project: " + found.Path);
            var parent = ResolveFolder(selected.Project.ProjectItems, filter, out var canonicalFilter);
            var item = parent.AddFromFile(fullPath);
            selected.Project.Save(selected.Project.FullName);
            var itemPath = string.IsNullOrEmpty(canonicalFilter) ? item.Name : canonicalFilter + "/" + item.Name;
            return Wrap(new ProjectItemMutationOutput { Project = selected.UniqueName, Item = itemPath, FilePath = ItemFilePath(item) ?? fullPath, Added = true, FileStillExists = File.Exists(fullPath), Filter = canonicalFilter });
        }

        [McpServerTool(Name = "project_item_remove", Title = "Remove project item", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
        [Description("Exclude one exact project item from the loaded project's project-system state by its '/'-separated hierarchy path. Requires an idle IDE and saved documents, solution and project. Saves the target project file after removing only the item reference; never deletes the source file from disk. Inspect the project hierarchy before retrying an interrupted request. Does not alter configuration or startup project.")]
        public async Task<ProjectWorkflowOutput<ProjectItemMutationOutput>> RemoveProjectItemTool(string project, string itemPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(itemPath)) throw Failure(ToolErrorCode.InvalidArgument, "An exact project item path is required.");
            await _vs.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var selected = SelectProject(project);
            IdleAndSaved(selected.Project);
            var item = WalkItems(selected.Project.ProjectItems, "").SingleOrDefault(i => Same(i.Path, itemPath));
            if (item == null) throw Failure(ToolErrorCode.ProjectNotFound, "No project item has the exact path '" + itemPath + "'. Read project_items first.");
            var physicalPath = item.FilePath;
            if (item.ProjectItem.ProjectItems != null && item.ProjectItem.ProjectItems.Count > 0) throw Failure(ToolErrorCode.InvalidArgument, "Only individual files can be removed; remove child files separately.");
            item.ProjectItem.Remove();
            selected.Project.Save(selected.Project.FullName);
            return Wrap(new ProjectItemMutationOutput { Project = selected.UniqueName, Item = item.Path, FilePath = physicalPath, Removed = true, FileStillExists = physicalPath != null && File.Exists(physicalPath) });
        }

        private ProjectWorkflowOutput<T> Wrap<T>(T data)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new ProjectWorkflowOutput<T> { Instance = _vs.ReadInstance(), Data = data };
        }

        private McpToolException Failure(ToolErrorCode code, string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new McpToolException(code, message, new JObject { ["instance"] = _vs.Instance() });
        }

        private SelectedProject Select(string project, string configuration, string platform)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var selected = SelectProject(project);
            var vcProject = selected.Project.Object as VCProject;
            if (vcProject == null) throw Failure(ToolErrorCode.NotSupported, "The selected project does not expose the native Visual C++ project engine.");
            if (string.IsNullOrWhiteSpace(configuration) && string.IsNullOrWhiteSpace(platform))
            {
                var active = selected.Project.ConfigurationManager.ActiveConfiguration;
                configuration = active.ConfigurationName;
                platform = active.PlatformName;
                if (string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(platform))
                    throw Failure(ToolErrorCode.NoActiveConfiguration, "Visual Studio did not expose the project's mapping for the active solution configuration.");
            }
            else if (string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(platform)) throw Failure(ToolErrorCode.InvalidArgument, "Supply both configuration and platform, or omit both to use the active solution mapping.");
            var matches = new List<VCConfiguration>();
            foreach (var rawCandidate in (IEnumerable)(IVCCollection)vcProject.Configurations)
            {
                var candidate = rawCandidate as VCConfiguration;
                if (candidate != null && Same(candidate.ConfigurationName, configuration) && Same(((VCPlatform)candidate.Platform).Name, platform)) matches.Add(candidate);
            }
            if (matches.Count != 1) throw Failure(matches.Count == 0 ? ToolErrorCode.ConfigurationNotFound : ToolErrorCode.ExecutionError, matches.Count == 0 ? "The exact project configuration/platform pair does not exist." : "The project exposes duplicate configuration/platform pairs.");
            return new SelectedProject { Project = selected.Project, UniqueName = selected.UniqueName, ConfigurationObject = matches[0], ConfigurationName = configuration, PlatformName = platform };
        }

        private SelectedProject SelectProject(string project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_vs.Dte.Solution.IsOpen) throw Failure(ToolErrorCode.NoSolution, "No solution is open in this Visual Studio instance.");
            var unique = VsContext.ResolveProject(_vs.EnumerateProjects(), project, _vs.Instance());
            if (unique == null) throw Failure(ToolErrorCode.InvalidArgument, "Project is required.");
            var target = FindProject(_vs.Dte.Solution.Projects, unique);
            if (target == null) throw Failure(ToolErrorCode.ProjectNotFound, "The selected project is not loaded.");
            return new SelectedProject { Project = target, UniqueName = unique };
        }

        private void IdleAndSaved(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_vs.TestsRunning() || _vs.Dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode || _vs.DebugLaunchPending || _vs.Dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
                throw Failure(ToolErrorCode.ExecutionError, "Operation requires an idle IDE with no pending launch, build or debug session.");
            if (_vs.Dte.Documents.Cast<Document>().Any(d => { ThreadHelper.ThrowIfNotOnUIThread(); return !d.Saved; }) || !_vs.Dte.Solution.Saved || !project.Saved)
                throw Failure(ToolErrorCode.ExecutionError, "Save all documents, the solution and target project before changing project settings or items.");
        }

        private static void Read(Dictionary<string, CppPropertyValue> output, VCConfiguration config, object tool, string key, string directName, string propertyName)
        {
            object raw = null;
            object evaluated = null;
            var ruleName = key == "additionalDependencies" || key == "additionalLibraryDirectories" || key == "outputFile" || key == "programDatabaseFile" ? "Link" :
                key == "additionalIncludeDirectories" || key == "preprocessorDefinitions" || key == "languageStandard" || key == "usePrecompiledHeader" || key == "precompiledHeaderThrough" ? "CL" : null;
            var ruleProperty = ruleName == "CL" && key == "usePrecompiledHeader" ? "PrecompiledHeader" :
                ruleName == "CL" && key == "precompiledHeaderThrough" ? "PrecompiledHeaderFile" : propertyName;
            try
            {
                if (ruleName != null && config.Rules is IVCCollection rules && rules.Item(ruleName) is IVCRulePropertyStorage storage)
                {
                    raw = storage.GetUnevaluatedPropertyValue(ruleProperty);
                    evaluated = storage.GetEvaluatedPropertyValue(ruleProperty);
                }
            }
            catch (Exception) { }
            if (raw == null) { try { raw = config.GetUnevaluatedPropertyValue(propertyName); } catch (Exception) { } }
            if (evaluated == null) { try { evaluated = config.GetEvaluatedPropertyValue(propertyName); } catch (Exception) { } }
            if (raw == null && tool != null) raw = GetToolProperty(tool, directName);
            if (evaluated == null && tool != null)
            {
                var value = GetToolProperty(tool, directName);
                if (value != null) { try { evaluated = config.Evaluate(Convert.ToString(value)); } catch (Exception) { } }
                if (evaluated == null) evaluated = value;
            }
            output[key] = new CppPropertyValue { Raw = String(raw), Evaluated = String(evaluated), Source = raw != null || evaluated != null ? "Visual C++ project engine" : "unavailable" };
        }

        private static void ReadFile(Dictionary<string, CppPropertyValue> output, VCFileConfiguration fileConfiguration, object tool, string key, string propertyName)
        {
            object raw = null;
            object evaluated = null;
            try
            {
                if (fileConfiguration is IVCRulePropertyStorage fileStorage)
                {
                    raw = fileStorage.GetUnevaluatedPropertyValue(propertyName);
                    evaluated = fileStorage.GetEvaluatedPropertyValue(propertyName);
                }
            }
            catch (Exception) { }
            try
            {
                if (raw == null && tool is IVCRulePropertyStorage toolStorage) raw = toolStorage.GetUnevaluatedPropertyValue(propertyName);
                if (evaluated == null && tool is IVCRulePropertyStorage evaluatedToolStorage) evaluated = evaluatedToolStorage.GetEvaluatedPropertyValue(propertyName);
            }
            catch (Exception) { }
            if (raw == null) raw = GetToolProperty(tool, propertyName);
            if (evaluated == null && raw != null) { try { evaluated = fileConfiguration.Evaluate(Convert.ToString(raw)); } catch (Exception) { } }
            output[key] = new CppPropertyValue { Raw = String(raw), Evaluated = String(evaluated ?? raw), Source = raw != null ? "Visual C++ file configuration" : "unavailable" };
        }

        private static object GetToolProperty(object tool, string propertyName)
        {
            try
            {
                if (tool is IVCRulePropertyStorage ruleStorage)
                    return ruleStorage.GetUnevaluatedPropertyValue(propertyName) ?? ruleStorage.GetEvaluatedPropertyValue(propertyName);
                if (tool is VCCLCompilerTool compiler)
                {
                    switch (propertyName)
                    {
                        case "AdditionalIncludeDirectories": return compiler.AdditionalIncludeDirectories;
                        case "PreprocessorDefinitions": return compiler.PreprocessorDefinitions;
                        case "UsePrecompiledHeader":
                        case "PrecompiledHeader": return compiler.UsePrecompiledHeader;
                        case "PrecompiledHeaderFile": return compiler.PrecompiledHeaderThrough;
                        case "LanguageStandard": return null;
                    }
                }
                if (tool is VCLinkerTool linker)
                {
                    switch (propertyName)
                    {
                        case "AdditionalDependencies": return linker.AdditionalDependencies;
                        case "AdditionalLibraryDirectories": return linker.AdditionalLibraryDirectories;
                        case "OutputFile": return linker.OutputFile;
                        case "ProgramDatabaseFile": return linker.ProgramDatabaseFile;
                    }
                }
                if (tool is VCLibrarianTool librarian)
                {
                    switch (propertyName)
                    {
                        case "AdditionalDependencies": return librarian.AdditionalDependencies;
                        case "AdditionalLibraryDirectories": return librarian.AdditionalLibraryDirectories;
                    }
                }
            }
            catch (NotImplementedException) { return null; }
            catch (MissingMethodException) { return null; }
            return null;
        }

        private CppLaunchSettings ReadLaunch(VCConfiguration config) => ReadLaunch(GetRuleStorage(config, "WindowsLocalDebugger"));

        private CppLaunchSettings ReadLaunch(IVCRulePropertyStorage settings)
        {
            if (settings == null) throw Failure(ToolErrorCode.NotSupported, "Visual Studio does not expose the WindowsLocalDebugger property rule for this configuration.");
            return new CppLaunchSettings
            {
                Command = ReadRuleValue(settings, "LocalDebuggerCommand"), Arguments = ReadRuleValue(settings, "LocalDebuggerCommandArguments"),
                WorkingDirectory = ReadRuleValue(settings, "LocalDebuggerWorkingDirectory"), Environment = ReadRuleValue(settings, "LocalDebuggerEnvironment"),
                MergeEnvironment = ParseBoolean(ReadRuleValue(settings, "LocalDebuggerMergeEnvironment")),
                DebuggerType = NormalizeDebuggerType(ReadRuleValue(settings, "LocalDebuggerDebuggerType"))
            };
        }

        private static string ReadRuleValue(IVCRulePropertyStorage storage, string property) => storage.GetUnevaluatedPropertyValue(property) ?? storage.GetEvaluatedPropertyValue(property);

        private static IVCRulePropertyStorage GetRuleStorage(VCConfiguration configuration, string rule)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return configuration.Rules is IVCCollection rules ? rules.Item(rule) as IVCRulePropertyStorage : null;
        }

        private string LaunchRuleProperty(string field)
        {
            switch (field)
            {
                case "ApplicationCommand": return "LocalDebuggerCommand";
                case "CommandArguments": return "LocalDebuggerCommandArguments";
                case "WorkingDirectory": return "LocalDebuggerWorkingDirectory";
                case "Environment": return "LocalDebuggerEnvironment";
                case "EnvironmentMerge": return "LocalDebuggerMergeEnvironment";
                case "DebuggerType": return "LocalDebuggerDebuggerType";
                default: throw Failure(ToolErrorCode.NotSupported, "Native debugger setting is not supported: " + field);
            }
        }

        private static string NormalizeDebuggerType(string value)
        {
            switch ((value ?? "").Trim())
            {
                case "Auto": return "auto";
                case "NativeOnly": return "nativeOnly";
                case "ManagedOnly": return "managedOnly";
                case "Mixed": return "mixed";
                case "ManagedCore": return "managedCore";
                case "NativeWithManagedCore": return "nativeWithManagedCore";
                default: return value;
            }
        }

        private string ParseDebuggerType(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "auto": return "Auto";
                case "nativeonly": return "NativeOnly";
                case "managedonly": return "ManagedOnly";
                case "mixed": return "Mixed";
                case "managedcore": return "ManagedCore";
                case "nativewithmanagedcore": return "NativeWithManagedCore";
                default: throw Failure(ToolErrorCode.InvalidArgument, "Debugger type must be auto, nativeOnly, managedOnly, mixed, managedCore or nativeWithManagedCore.");
            }
        }

        private static bool? ParseBoolean(string value) => bool.TryParse(value, out var parsed) ? parsed : (bool?)null;

        private void ValidateEnvironment(string environment)
        {
            foreach (var line in environment.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var index = line.IndexOf('=');
                var name = index > 0 ? line.Substring(0, index) : string.Empty;
                if (index < 1 || line.IndexOf('\0') >= 0 || !IsValidEnvironmentName(name))
                    throw Failure(ToolErrorCode.InvalidArgument, "Environment must contain one NAME=VALUE entry per line; names cannot be empty.");
            }
        }

        private static bool IsValidEnvironmentName(string name) => !string.IsNullOrEmpty(name);

        private static readonly HashSet<string> LaunchPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ApplicationCommand", "CommandArguments", "WorkingDirectory", "Environment", "EnvironmentMerge", "DebuggerType" };

        private static string Output(VCConfiguration config, string member, Dictionary<string, string> unavailable, string label)
        {
            try
            {
                object nativeValue;
                switch (member)
                {
                    case "primaryOutput": nativeValue = config.PrimaryOutput; break;
                    case "programDatabase": nativeValue = config.ProgramDatabase; break;
                    case "importLibrary": nativeValue = config.ImportLibrary; break;
                    case "outputDirectory": nativeValue = config.OutputDirectory; break;
                    case "intermediateDirectory": nativeValue = config.IntermediateDirectory; break;
                    default: throw new ArgumentOutOfRangeException(nameof(member));
                }
                var value = String(nativeValue);
                if (string.IsNullOrWhiteSpace(value)) { unavailable[label] = "Visual C++ returned no path for this output."; return null; }
                return value;
            }
            catch (Exception ex) { unavailable[label] = ex.GetBaseException().Message; return null; }
        }

        private static object GetTool(object config, string legacyName, string ruleName)
        {
            var typedConfig = config as VCConfiguration;
            if (typedConfig != null && typedConfig.Tools is IVCCollection tools)
            {
                var found = tools.Item(legacyName);
                if (found != null) return found;
            }
            if (typedConfig != null && typedConfig.Rules is IVCCollection rules)
                return rules.Item(ruleName);
            return null;
        }

        private VCFile FindCppFile(SelectedProject selected, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var vcProject = selected.Project.Object as VCProject;
            if (vcProject == null) throw Failure(ToolErrorCode.NotSupported, "The selected project does not expose the native Visual C++ project engine.");
            var matches = new List<VCFile>();
            foreach (var rawFile in (IEnumerable)(IVCCollection)vcProject.Files)
            {
                var file = rawFile as VCFile;
                if (file != null && SamePath(file.FullPath, path)) matches.Add(file);
            }
            if (matches.Count != 1) throw Failure(matches.Count == 0 ? ToolErrorCode.ProjectNotFound : ToolErrorCode.ExecutionError, matches.Count == 0 ? "The exact file is not included in this C++ project." : "The file path appears more than once in the C++ project.");
            return matches[0];
        }

        private VCFileConfiguration FindFileConfiguration(VCFile file, SelectedProject selected)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var matches = new List<VCFileConfiguration>();
            foreach (var rawCandidate in (IEnumerable)(IVCCollection)file.FileConfigurations)
            {
                var candidate = rawCandidate as VCFileConfiguration;
                var projectConfiguration = candidate?.ProjectConfiguration as VCConfiguration;
                if (projectConfiguration != null && Same(projectConfiguration.ConfigurationName, selected.ConfigurationName) && Same(((VCPlatform)projectConfiguration.Platform).Name, selected.PlatformName)) matches.Add(candidate);
            }
            if (matches.Count > 1) throw Failure(ToolErrorCode.ExecutionError, "The C++ file exposes duplicate configuration/platform pairs.");
            return matches.SingleOrDefault();
        }

        private static string String(object value) => value == null ? null : Convert.ToString(value);
        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool SamePath(string a, string b) => a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        private static Project FindProject(Projects projects, string uniqueName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (Project project in projects)
            {
                if (project == null) continue;
                if (Same(project.UniqueName, uniqueName)) return project;
                if (Same(project.Kind, "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}"))
                    foreach (ProjectItem item in project.ProjectItems)
                        if (item.SubProject != null)
                        {
                            if (Same(item.SubProject.UniqueName, uniqueName)) return item.SubProject;
                            var nested = FindProjectItems(item.SubProject.ProjectItems, uniqueName);
                            if (nested != null) return nested;
                        }
            }
            return null;
        }
        private static Project FindProjectItems(ProjectItems items, string uniqueName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (ProjectItem item in items) if (item.SubProject != null)
            {
                if (Same(item.SubProject.UniqueName, uniqueName)) return item.SubProject;
                var nested = FindProjectItems(item.SubProject.ProjectItems, uniqueName);
                if (nested != null) return nested;
            }
            return null;
        }

        private ProjectItems ResolveFolder(ProjectItems root, string filter, out string canonicalFilter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(filter)) { canonicalFilter = null; return root; }
            var current = root;
            var resolved = new List<string>();
            foreach (var part in filter.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                ProjectItem match = null;
                foreach (ProjectItem item in current) if (Same(item.Name, part)) { match = item; break; }
                if (match == null || match.ProjectItems == null ||
                    (Same(match.Kind, Microsoft.VisualStudio.VSConstants.GUID_ItemType_PhysicalFile.ToString("B")) && match.ProjectItems.Count == 0))
                    throw Failure(ToolErrorCode.ProjectNotFound, "Filter/folder path does not exist or refers to a file: " + filter);
                resolved.Add(match.Name);
                current = match.ProjectItems;
            }
            canonicalFilter = string.Join("/", resolved);
            return current;
        }

        private static IEnumerable<ItemEntry> WalkItems(ProjectItems items, string parent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (ProjectItem item in items)
            {
                var path = string.IsNullOrEmpty(parent) ? item.Name : parent + "/" + item.Name;
                yield return new ItemEntry { ProjectItem = item, Path = path, FilePath = ItemFilePath(item) };
                if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                    foreach (var child in WalkItems(item.ProjectItems, path)) yield return child;
            }
        }
        private static string ItemFilePath(ProjectItem item) { try { return item.FileCount > 0 ? item.FileNames[1] : null; } catch { return null; } }

        private sealed class SelectedProject { public Project Project; public string UniqueName; public VCConfiguration ConfigurationObject; public string ConfigurationName; public string PlatformName; }
        private sealed class ItemEntry { public ProjectItem ProjectItem; public string Path; public string FilePath; }
    }
}
