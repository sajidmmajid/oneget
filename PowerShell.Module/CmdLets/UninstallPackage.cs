// 
//  Copyright (c) Microsoft Corporation. All rights reserved. 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  

namespace Microsoft.PowerShell.OneGet.CmdLets {
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq;
    using System.Management.Automation;
    using Microsoft.OneGet.Packaging;
    using Microsoft.OneGet.Utility.Async;
    using Microsoft.OneGet.Utility.Collections;
    using Microsoft.OneGet.Utility.Extensions;

    [Cmdlet(VerbsLifecycle.Uninstall, Constants.PackageNoun, SupportsShouldProcess = true, HelpUri = "http://go.microsoft.com/fwlink/?LinkID=517142")]
    public sealed class UninstallPackage : GetPackage {
        private Dictionary<string, List<SoftwareIdentity>> _resultsPerName;

        protected override IEnumerable<string> ParameterSets {
            get {
                return new[] {Constants.PackageByInputObjectSet, Constants.PackageBySearchSet};
            }
        }

        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ParameterSetName = Constants.PackageByInputObjectSet)]
        public SoftwareIdentity[] InputObject {get; set;}

        [Parameter(Mandatory = true, Position = 0, ParameterSetName = Constants.PackageBySearchSet)]
        public override string[] Name {get; set;}

        [Parameter(ParameterSetName = Constants.PackageBySearchSet)]
        public override string RequiredVersion {get; set;}

        [Alias("Version")]
        [Parameter(ParameterSetName = Constants.PackageBySearchSet)]
        public override string MinimumVersion {get; set;}

        [Parameter(ParameterSetName = Constants.PackageBySearchSet)]
        public override string MaximumVersion {get; set;}

        /*
        [Alias("Provider")]
        [Parameter(ValueFromPipelineByPropertyName = true, ParameterSetName = Constants.PackageBySearchSet)]
        public override string[] ProviderName {get; set;}
        */

        protected override void GenerateCmdletSpecificParameters(Dictionary<string, object> unboundArguments) {
            if (!IsInvocation) {
                var providerNames = PackageManagementService.ProviderNames;
                var whatsOnCmdline = GetDynamicParameterValue<string[]>("ProviderName");
                if (whatsOnCmdline != null) {
                    providerNames = providerNames.Concat(whatsOnCmdline).Distinct();
                }

                DynamicParameterDictionary.AddOrSet("ProviderName", new RuntimeDefinedParameter("ProviderName", typeof(string[]), new Collection<Attribute> {
                    new ParameterAttribute {
                        ValueFromPipelineByPropertyName = true,
                        ParameterSetName = Constants.PackageBySearchSet
                    },
                    new AliasAttribute("Provider"),
                    new ValidateSetAttribute(providerNames.ToArray())
                }));
            }
            else {
                DynamicParameterDictionary.AddOrSet("ProviderName", new RuntimeDefinedParameter("ProviderName", typeof(string[]), new Collection<Attribute> {
                    new ParameterAttribute {
                        ValueFromPipelineByPropertyName = true,
                        ParameterSetName = Constants.PackageBySearchSet
                    },
                    new AliasAttribute("Provider")
                }));
            }
        }

        public override bool ProcessRecordAsync() {
            if (IsPackageByObject) {
                return UninstallPackages(InputObject);
            }

            // otherwise, it's just packages by name 
            _resultsPerName = new Dictionary<string, List<SoftwareIdentity>>();
            SelectedProviders.ParallelForEach(provider => {
                foreach (var n in Name) {
                    var c = _resultsPerName.GetOrAdd(n, () => new List<SoftwareIdentity>());
                    foreach (var pkg in ProcessNames(provider, n)) {
                        lock (c) {
                            if (IsPackageInVersionRange(pkg)) {
                                c.Add(pkg);
                            }
                        }
                    }
                }
            });

            return true;
        }

        public override bool EndProcessingAsync() {
            if (_resultsPerName == null) {
                return true;
            }
            // Show errors before?
            foreach (var name in UnprocessedNames) {
                Error(Errors.NoMatchFound, name);
            }

            if (Stopping) {
                return false;
            }

            foreach (var n in _resultsPerName.Keys) {
                // check if we have a 1 package per name 
                if (_resultsPerName[n].Count > 1) {
                    Error(Errors.DisambiguateForUninstall, n, _resultsPerName[n]);
                    return false;
                }

                if (Stopping) {
                    return false;
                }

                if (!UninstallPackages(_resultsPerName[n])) {
                    return false;
                }
            }
            return true;
        }

        private bool UninstallPackages(IEnumerable<SoftwareIdentity> packagesToUnInstall) {
            foreach (var pkg in packagesToUnInstall) {
                var provider = SelectProviders(pkg.ProviderName).FirstOrDefault();

                if (provider == null) {
                    Error(Errors.UnknownProvider, pkg.ProviderName);
                    return false;
                }

                try {
                    foreach (var installedPkg in provider.UninstallPackage(pkg, this).CancelWhen(_cancellationEvent.Token)) {
                        if (IsCanceled) {
                            return false;
                        }
                        WriteObject(installedPkg);
                    }
                } catch (Exception e) {
                    e.Dump();
                    Error(Errors.UninstallationFailure, pkg.Name);
                    return false;
                }
            }
            return true;
        }

        public bool ShouldProcessPackageUninstall(string packageName, string version) {
            return Force || ShouldProcess(FormatMessageString(Constants.TargetPackage, packageName), FormatMessageString(Constants.ActionUninstallPackage)).Result;
        }
    }
}