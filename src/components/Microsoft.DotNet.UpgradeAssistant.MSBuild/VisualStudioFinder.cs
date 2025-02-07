﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.Setup.Configuration;

namespace Microsoft.DotNet.UpgradeAssistant.MSBuild
{
    /// <summary>
    ///     Helper class to wrap the Microsoft.VisualStudio.Setup.Configuration.Interop API to query
    ///     Visual Studio setup for instances installed on the machine.
    ///     Code derived from sample: https://code.msdn.microsoft.com/Visual-Studio-Setup-0cedd331.
    /// </summary>
    /// <remarks>
    /// The package is not supported on .NET 6, so we copied the COM declarations to use instead.
    /// </remarks>
    internal class VisualStudioFinder : IConfigureOptions<WorkspaceOptions>
    {
        private const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
        private readonly ILogger<VisualStudioFinder> _logger;

        public VisualStudioFinder(ILogger<VisualStudioFinder> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Configure(WorkspaceOptions options)
        {
            (options.VisualStudioPath, options.VisualStudioVersion) = GetLatestVisualStudioPath(options.VisualStudioPath);
        }

        private (string? Path, int? Version) GetLatestVisualStudioPath(string? suppliedPath)
        {
            var latest = GetLatestPath(suppliedPath);

            if (latest is null)
            {
                _logger.LogWarning("Did not find a Visual Studio instance");
                return default;
            }

            var version = Version.Parse(latest.GetInstallationVersion());
            var installation = latest.GetInstallationPath();

            if (Directory.Exists(installation))
            {
                _logger.LogDebug("Using Visual Studio v{VsVersion} [{VsPath}]", version, installation);

                return (installation, version?.Major);
            }
            else
            {
                _logger.LogWarning("Found Visual Studio {VsVersion}, but directory '{VsPath}' does not exist.", version, installation);

                return default;
            }
        }

        private ISetupInstance2? GetLatestPath(string? suppliedPath)
        {
            var result = default(ISetupInstance2);
            var resultVersion = new Version(0, 0);

            try
            {
                // This code is not obvious. See the sample (link above) for reference.
                var query = (ISetupConfiguration2)GetQuery();
                var e = query.EnumAllInstances();

                int fetched;
                var instances = new ISetupInstance[1];
                do
                {
                    // Call e.Next to query for the next instance (single item or nothing returned).
                    e.Next(1, instances, out fetched);
                    if (fetched <= 0)
                    {
                        continue;
                    }

                    var instance = (ISetupInstance2)instances[0];
                    var state = instance.GetState();

                    if (!Version.TryParse(instance.GetInstallationVersion(), out var version))
                    {
                        continue;
                    }

                    // If the install was complete and a valid version, consider it.
                    if (state == InstanceState.Complete ||
                        (state.HasFlag(InstanceState.Registered) && state.HasFlag(InstanceState.NoRebootRequired)))
                    {
                        var instanceHasMSBuild = false;

                        foreach (var package in instance.GetPackages())
                        {
                            if (string.Equals(package.GetId(), "Microsoft.Component.MSBuild", StringComparison.OrdinalIgnoreCase))
                            {
                                instanceHasMSBuild = true;
                                break;
                            }
                        }

                        if (instanceHasMSBuild && instance is not null)
                        {
                            if (suppliedPath is not null && string.Equals(suppliedPath, instance.GetInstallationPath(), StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogTrace("Identified supplied path for Visual Studio v{Version} [{Path}]", version, instance.GetInstallationPath());

                                return instance;
                            }
                            else if (version > resultVersion)
                            {
                                _logger.LogTrace("Found Visual Studio v{Version} [{Path}]", version, instance.GetInstallationPath());

                                result = instance;
                                resultVersion = version;
                            }
                        }
                    }
                }
                while (fetched > 0);
            }
            catch (COMException)
            {
            }
            catch (DllNotFoundException)
            {
                // This is OK, VS "15" or greater likely not installed.
            }

            return result;
        }

        private static ISetupConfiguration GetQuery()
        {
            try
            {
                // Try to CoCreate the class object.
                return new SetupConfiguration();
            }
            catch (COMException ex) when (ex.ErrorCode == REGDB_E_CLASSNOTREG)
            {
                // Try to get the class object using app-local call.
                var result = NativeMethods.GetSetupConfiguration(out var query, IntPtr.Zero);

                if (result < 0)
                {
                    throw;
                }

                return query;
            }
        }
    }
}
