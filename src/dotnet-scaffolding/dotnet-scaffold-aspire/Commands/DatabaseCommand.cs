// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.DotNet.Scaffolding.Helpers.Extensions.Roslyn;
using Microsoft.DotNet.Scaffolding.Helpers.General;
using Microsoft.DotNet.Scaffolding.Helpers.Roslyn;
using Microsoft.DotNet.Scaffolding.Helpers.Services;
using Microsoft.DotNet.Scaffolding.Helpers.Services.Environment;
using Microsoft.DotNet.Tools.Scaffold.Aspire.Helpers;
using Spectre.Console.Cli;

namespace Microsoft.DotNet.Tools.Scaffold.Aspire.Commands
{
    internal class DatabaseCommand : AsyncCommand<DatabaseCommand.DatabaseCommandSettings>
    {
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IEnvironmentService _environmentService;
        //Dictionary to hold autogenerated project paths that are created during build-time for Aspire host projects.
        //The string key is the full project path (.csproj) and the string value is the full project name (with namespace
        private Dictionary<string, string> _autoGeneratedProjectNames;
        public DatabaseCommand(IFileSystem fileSystem, ILogger logger, IEnvironmentService environmentService)
        {
            _environmentService = environmentService;
            _fileSystem = fileSystem;
            _logger = logger;
            _autoGeneratedProjectNames = [];
        }
        public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] DatabaseCommandSettings settings)
        {
            new MsBuildInitializer(_logger).Initialize();
            if (!ValidateDatabaseCommandSettings(settings))
            {
                return -1;
            }

            _logger.LogMessage("Installing packages...");
            InstallPackages(settings);

            _logger.LogMessage("Updating App host project...");
            var appHostResult = await UpdateAppHostAsync(settings);

            _logger.LogMessage("Adding new DbContext...");
            var dbContextCreationResult = CreateNewDbContext(settings);

            _logger.LogMessage("Updating web/worker project...");
            var workerResult = await UpdateWebAppAsync(settings);

            if (appHostResult && dbContextCreationResult && workerResult)
            {
                _logger.LogMessage("Finished");
                return 0;
            }
            else
            {
                _logger.LogMessage("An error occurred.");
                return -1;
            }
        }

        /// <summary>
        /// generate a path for DbContext, then use DbContextHelper.CreateDbContext to invoke 'NewDbContext.tt'
        /// DbContextHelper.CreateDbContext will also write the resulting templated string (class text) to disk
        /// </summary>
        private bool CreateNewDbContext(DatabaseCommandSettings settings)
        {
            var newDbContextPath = CreateNewDbContextPath(settings);
            var projectBasePath = Path.GetDirectoryName(settings.Project);
            var relativeContextPath = Path.GetRelativePath(settings.Project, newDbContextPath);
            if (GetCmdsHelper.DatabaseTypeDefaults.TryGetValue(settings.Type, out var dbContextProperties) && dbContextProperties is not null)
            {
                return DbContextHelper.CreateDbContext(dbContextProperties, newDbContextPath, projectBasePath, _fileSystem);
            }

            return false;
        }

        private string CreateNewDbContextPath(DatabaseCommandSettings commandSettings)
        {
            if (!GetCmdsHelper.DatabaseTypeDefaults.TryGetValue(commandSettings.Type, out var dbContextProperties) || dbContextProperties is null)
            {
                return string.Empty;
            }

            var dbContextPath = string.Empty;
            var dbContextFileName = $"{dbContextProperties.DbContextName}.cs";
            var baseProjectPath = Path.GetDirectoryName(commandSettings.Project);
            if (!string.IsNullOrEmpty(baseProjectPath))
            {
                dbContextPath = Path.Combine(baseProjectPath, dbContextFileName);
                dbContextPath = StringUtil.GetUniqueFilePath(dbContextPath);
            }

            return dbContextPath;
        }

        private bool ValidateDatabaseCommandSettings(DatabaseCommandSettings commandSettings)
        {
            if (string.IsNullOrEmpty(commandSettings.Type) || !GetCmdsHelper.DatabaseTypeCustomValues.Contains(commandSettings.Type, StringComparer.OrdinalIgnoreCase))
            {
                string dbTypeDisplayList = string.Join(", ", GetCmdsHelper.DatabaseTypeCustomValues.GetRange(0, GetCmdsHelper.DatabaseTypeCustomValues.Count - 1)) +
                    (GetCmdsHelper.DatabaseTypeCustomValues.Count > 1 ? " and " : "") + GetCmdsHelper.DatabaseTypeCustomValues[GetCmdsHelper.DatabaseTypeCustomValues.Count - 1];
                _logger.LogMessage("Missing/Invalid --type option.", LogMessageType.Error);
                _logger.LogMessage($"Valid options : {dbTypeDisplayList}", LogMessageType.Error);
                return false;
            }

            if (string.IsNullOrEmpty(commandSettings.AppHostProject) || !_fileSystem.FileExists(commandSettings.AppHostProject))
            {
                _logger.LogMessage("Missing/Invalid --apphost-project option.", LogMessageType.Error);
                return false;
            }

            if (string.IsNullOrEmpty(commandSettings.Project) || !_fileSystem.FileExists(commandSettings.Project))
            {
                _logger.LogMessage("Missing/Invalid --project option.", LogMessageType.Error);
                return false;
            }

            return true;
        }

        private void InstallPackages(DatabaseCommandSettings commandSettings)
        {
            PackageConstants.DatabasePackages.DatabasePackagesAppHostDict.TryGetValue(commandSettings.Type, out string? appHostPackageName);
            if (!string.IsNullOrEmpty(appHostPackageName))
            {
                DotnetCommands.AddPackage(
                    packageName: appHostPackageName,
                    logger: _logger,
                    projectFile: commandSettings.AppHostProject,
                    includePrerelease: commandSettings.Prerelease);
            }

            PackageConstants.DatabasePackages.DatabasePackagesApiServiceDict.TryGetValue(commandSettings.Type, out string? projectPackageName);
            if (!string.IsNullOrEmpty(projectPackageName))
            {
                DotnetCommands.AddPackage(
                    packageName: projectPackageName,
                    logger: _logger,
                    projectFile: commandSettings.Project,
                    includePrerelease: commandSettings.Prerelease);
            }
        }

        private async Task<bool> UpdateAppHostAsync(DatabaseCommandSettings commandSettings)
        {
            CodeModifierConfig? config = ProjectModifierHelper.GetCodeModifierConfig("db-apphost.json", System.Reflection.Assembly.GetExecutingAssembly());
            var workspaceSettings = new WorkspaceSettings
            {
                InputPath = commandSettings.AppHostProject
            };

            var hostAppSettings = new AppSettings();
            hostAppSettings.AddSettings("workspace", workspaceSettings);
            var codeService = new CodeService(hostAppSettings, _logger);
            //initialize _autoGeneratedProjectNames here. 
            await GetAutoGeneratedProjectNamesAsync(codeService, commandSettings.Project);

            CodeChangeOptions options = new()
            {
                IsMinimalApp = await ProjectModifierHelper.IsMinimalApp(codeService),
                UsingTopLevelsStatements = await ProjectModifierHelper.IsUsingTopLevelStatements(codeService)
            };

            //edit CodeModifierConfig to add the web project name from _autoGeneratedProjectNames.
            _autoGeneratedProjectNames.TryGetValue(commandSettings.Project, out var autoGenProjectName);
            config = EditConfigForAppHost(config, options, autoGenProjectName, commandSettings.Type);

            var projectModifier = new ProjectModifier(
                _environmentService,
                hostAppSettings,
                codeService,
                _logger,
                options,
                config);
            return await projectModifier.RunAsync();
        }

        private async Task GetAutoGeneratedProjectNamesAsync(CodeService codeService, string projectPath)
        {
            var allDocuments = await codeService.GetAllDocumentsAsync();
            var allSyntaxRoots = await Task.WhenAll(allDocuments.Select(doc => doc.GetSyntaxRootAsync()));

            // Get all classes with the "Projects" namespace
            var classesInNamespace = allSyntaxRoots
                .SelectMany(root => root?.DescendantNodes().OfType<ClassDeclarationSyntax>() ?? Enumerable.Empty<ClassDeclarationSyntax>())
                .Where(cls => cls.IsInNamespace("Projects"))
                .ToList();

            foreach (var classSyntax in classesInNamespace)
            {
                string? projectPathValue = classSyntax.GetStringPropertyValue("ProjectPath");
                // Get the full class name including the namespace
                var className = classSyntax.Identifier.Text;
                if (!string.IsNullOrEmpty(projectPathValue))
                {
                    _autoGeneratedProjectNames.Add(projectPathValue, $"Projects.{className}");
                }
            }
        }

        private async Task<bool> UpdateWebAppAsync(DatabaseCommandSettings commandSettings)
        {
            CodeModifierConfig? config = ProjectModifierHelper.GetCodeModifierConfig("db-webapi.json", System.Reflection.Assembly.GetExecutingAssembly());
            var workspaceSettings = new WorkspaceSettings
            {
                InputPath = commandSettings.Project
            };

            var webAppSettings = new AppSettings();
            webAppSettings.AddSettings("workspace", workspaceSettings);
            var codeService = new CodeService(webAppSettings, _logger);

            CodeChangeOptions options = new()
            {
                IsMinimalApp = await ProjectModifierHelper.IsMinimalApp(codeService),
                UsingTopLevelsStatements = await ProjectModifierHelper.IsUsingTopLevelStatements(codeService)
            };

            config = EditConfigForApiService(config, options, commandSettings.Type);
            var projectModifier = new ProjectModifier(
            _environmentService,
            webAppSettings,
            codeService,
            _logger,
            options,
            config);
            return await projectModifier.RunAsync();
        }

        private CodeModifierConfig? EditConfigForAppHost(CodeModifierConfig? configToEdit, CodeChangeOptions codeChangeOptions, string? projectName, string dbType)
        {
            if (configToEdit is null)
            {
                return null;
            }

            var programCsFile = configToEdit.Files?.FirstOrDefault(x => !string.IsNullOrEmpty(x.FileName) && x.FileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase));
            if (programCsFile != null &&
                programCsFile.Methods != null &&
                programCsFile.Methods.Count != 0 &&
                GetCmdsHelper.DatabaseTypeDefaults.TryGetValue(dbType, out var dbProperties) &&
                dbProperties is not null)
            {
                var globalMethod = programCsFile.Methods.Where(x => x.Key.Equals("Global", StringComparison.OrdinalIgnoreCase)).First().Value;
                var addDbChange = globalMethod?.CodeChanges?.FirstOrDefault(x => !string.IsNullOrEmpty(x.Block) && x.Block.Contains("AddDatabase"));
                var addProjectChange = globalMethod?.CodeChanges?.FirstOrDefault(x => !string.IsNullOrEmpty(x.Parent) && x.Parent.Contains("builder.AddProject<{0}>"));
                if (!codeChangeOptions.UsingTopLevelsStatements && addProjectChange != null)
                {
                    addProjectChange = DocumentBuilder.AddLeadingTriviaSpaces(addProjectChange, spaces: 12);
                }

                if (addProjectChange != null && !string.IsNullOrEmpty(addProjectChange.Parent) && !string.IsNullOrEmpty(projectName))
                {
                    //format projectName onto "builder.AddProject<{0}>"
                    addProjectChange.Parent = string.Format(addProjectChange.Parent, projectName);
                    //format DbContextProperties onto "var {0} = builder.{1}("{2}").AddDatabase("{0}")"
                    addProjectChange.Block = string.Format(addProjectChange.Block, dbProperties.DbName);
                }

                if (!codeChangeOptions.UsingTopLevelsStatements && addDbChange != null)
                {
                    addDbChange = DocumentBuilder.AddLeadingTriviaSpaces(addDbChange, spaces: 12);
                }

                if (addDbChange != null)
                {
                    addDbChange.Block = string.Format(addDbChange.Block, dbProperties.DbName, dbProperties.AddDbMethod, dbProperties.DbType);
                }
            }

            return configToEdit;
        }

        private CodeModifierConfig? EditConfigForApiService(CodeModifierConfig? configToEdit, CodeChangeOptions codeChangeOptions, string dbType)
        {
            if (configToEdit is null)
            {
                return null;
            }

            var programCsFile = configToEdit.Files?.FirstOrDefault(x => !string.IsNullOrEmpty(x.FileName) && x.FileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase));
            if (programCsFile != null && programCsFile.Methods != null && programCsFile.Methods.Count != 0)
            {
                var globalMethod = programCsFile.Methods.Where(x => x.Key.Equals("Global", StringComparison.OrdinalIgnoreCase)).First().Value;
                //only one change in here
                var addDbChange = globalMethod?.CodeChanges?.FirstOrDefault();
                if (!codeChangeOptions.UsingTopLevelsStatements && addDbChange != null)
                {
                    addDbChange = DocumentBuilder.AddLeadingTriviaSpaces(addDbChange, spaces: 12);
                }

                if (addDbChange != null &&
                    !string.IsNullOrEmpty(addDbChange.Block) &&
                    GetCmdsHelper.DatabaseTypeDefaults.TryGetValue(dbType, out var dbProperties) &&
                    dbProperties is not null)
                {
                    //formatting DbContextProperties vars onto "builder.{0}<{1}>("{2}")"
                    addDbChange.Block = string.Format(addDbChange.Block, dbProperties.AddDbContextMethod, dbProperties.DbContextName, dbProperties.DbName);
                }
            }

            return configToEdit;
        }

        public class DatabaseCommandSettings : CommandSettings
        {
            [CommandOption("--type <TYPE>")]
            public required string Type { get; set; }

            [CommandOption("--apphost-project <APPHOSTPROJECT>")]
            public required string AppHostProject { get; set; }

            [CommandOption("--project <PROJECT>")]
            public required string Project { get; set; }

            [CommandOption("--prerelease")]
            public required bool Prerelease { get; set; }
        }
    }
}
