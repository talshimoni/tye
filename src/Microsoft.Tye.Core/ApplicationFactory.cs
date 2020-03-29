﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Tye.ConfigModel;

namespace Microsoft.Tye
{
    public static class ApplicationFactory
    {
        public static async Task<ApplicationBuilder> CreateAsync(OutputContext output, FileInfo source)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var queue = new Queue<ConfigApplication>();
            var visited = new HashSet<string>();
            var rootConfig = ConfigFactory.FromFile(source);
            ValidateConfigApplication(rootConfig);
            var root = new ApplicationBuilder(source, rootConfig.Name ?? source.Directory.Name.ToLowerInvariant());

            queue.Enqueue(rootConfig);

            while (queue.Count > 0)
            {
                var config = queue.Dequeue();

                if (visited.Contains(config.Source.FullName))
                {
                    continue;
                }

                visited.Add(config.Source.FullName);

                if (config == rootConfig && !string.IsNullOrEmpty(config.Registry))
                {
                    root.Registry = new ContainerRegistry(config.Registry);
                }

                foreach (var configService in config.Services)
                {
                    ServiceBuilder service;
                    if (!string.IsNullOrEmpty(configService.Project))
                    {
                        var expandedProject = Environment.ExpandEnvironmentVariables(configService.Project);
                        var projectFile = new FileInfo(Path.Combine(config.Source.DirectoryName, expandedProject));

                        var project = new ProjectServiceBuilder(configService.Name, projectFile);
                        service = project;

                        project.Build = configService.Build ?? true;
                        project.Args = configService.Args;
                        project.Replicas = configService.Replicas ?? 1;

                        await ProjectReader.ReadProjectDetailsAsync(output, project);

                        // We don't apply more container defaults here because we might need
                        // to promptly for the registry name.
                        project.ContainerInfo = new ContainerInfo()
                        {
                            UseMultiphaseDockerfile = false,
                        };
                    }
                    else if (!string.IsNullOrEmpty(configService.Image))
                    {
                        var container = new ContainerServiceBuilder(configService.Name, configService.Image)
                        {
                            Args = configService.Args,
                            Replicas = configService.Replicas ?? 1
                        };
                        service = container;
                    }
                    else if (!string.IsNullOrEmpty(configService.Executable))
                    {
                        var expandedExecutable = Environment.ExpandEnvironmentVariables(configService.Executable);
                        var workingDirectory = "";

                        // Special handling of .dlls as executables (it will be executed as dotnet {dll})
                        if (Path.GetExtension(expandedExecutable) == ".dll")
                        {
                            expandedExecutable = Path.GetFullPath(Path.Combine(config.Source.Directory.FullName, expandedExecutable));
                            workingDirectory = Path.GetDirectoryName(expandedExecutable)!;
                        }

                        var executable = new ExecutableServiceBuilder(configService.Name, expandedExecutable)
                        {
                            Args = configService.Args,
                            WorkingDirectory = configService.WorkingDirectory != null ?
                            Path.GetFullPath(Path.Combine(config.Source.Directory.FullName, Environment.ExpandEnvironmentVariables(configService.WorkingDirectory))) :
                            workingDirectory,
                            Replicas = configService.Replicas ?? 1
                        };
                        service = executable;
                    }
                    else if (configService.External)
                    {
                        var external = new ExternalServiceBuilder(configService.Name);
                        service = external;
                    }
                    else
                    {
                        throw new CommandException("Unable to determine service type.");
                    }

                    // There's no hierarchy, just add it to the list of services.
                    root.Services.Add(service);

                    // If there are no bindings and we're in ASP.NET Core project then add an HTTP and HTTPS binding
                    if (configService.Bindings.Count == 0 &&
                        service is ProjectServiceBuilder project2 &&
                        project2.IsAspNet)
                    {
                        // HTTP is the default binding
                        service.Bindings.Add(new BindingBuilder()
                        {
                            AutoAssignPort = true,
                            Protocol = "http"
                        });

                        service.Bindings.Add(new BindingBuilder()
                        {
                            Name = "https",
                            AutoAssignPort = true,
                            Protocol = "https"
                        });
                    }
                    else
                    {
                        foreach (var configBinding in configService.Bindings)
                        {
                            var binding = new BindingBuilder()
                            {
                                Name = configBinding.Name,
                                AutoAssignPort = configBinding.AutoAssignPort,
                                ConnectionString = configBinding.ConnectionString,
                                Host = configBinding.Host,
                                ContainerPort = configBinding.ContainerPort,
                                Port = configBinding.Port,
                                Protocol = configBinding.Protocol,
                            };

                            // Assume HTTP for projects only (containers may be different)
                            if (binding.ConnectionString == null && configService.Project != null)
                            {
                                binding.Protocol ??= "http";
                            }

                            service.Bindings.Add(binding);
                        }
                    }

                    foreach (var configEnvVar in configService.Configuration)
                    {
                        var envVar = new EnvironmentVariable(configEnvVar.Name, configEnvVar.Value);
                        if (service is ProjectServiceBuilder project)
                        {
                            project.EnvironmentVariables.Add(envVar);
                        }
                        else if (service is ContainerServiceBuilder container)
                        {
                            container.EnvironmentVariables.Add(envVar);
                        }
                        else if (service is ExecutableServiceBuilder executable)
                        {
                            executable.EnvironmentVariables.Add(envVar);
                        }
                        else if (service is ExternalServiceBuilder)
                        {
                            throw new CommandException("External services do not support environment variables.");
                        }
                        else
                        {
                            throw new CommandException("Unable to determine service type.");
                        }
                    }

                    foreach (var configVolume in configService.Volumes)
                    {
                        var volume = new VolumeBuilder(configVolume.Source, configVolume.Target);
                        if (service is ProjectServiceBuilder project)
                        {
                            project.Volumes.Add(volume);
                        }
                        else if (service is ContainerServiceBuilder container)
                        {
                            container.Volumes.Add(volume);
                        }
                        else if (service is ExecutableServiceBuilder executable)
                        {
                            throw new CommandException("Executable services do not support volumes.");
                        }
                        else if (service is ExternalServiceBuilder)
                        {
                            throw new CommandException("External services do not support volumes.");
                        }
                        else
                        {
                            throw new CommandException("Unable to determine service type.");
                        }
                    }
                }

                foreach (var configIngress in config.Ingress)
                {
                    var ingress = new IngressBuilder(configIngress.Name);
                    ingress.Replicas = configIngress.Replicas ?? 1;

                    root.Ingress.Add(ingress);

                    foreach (var configBinding in configIngress.Bindings)
                    {
                        var binding = new IngressBindingBuilder()
                        {
                            AutoAssignPort = configBinding.AutoAssignPort,
                            Name = configBinding.Name,
                            Port = configBinding.Port,
                            Protocol = configBinding.Protocol ?? "http",
                        };
                        ingress.Bindings.Add(binding);
                    }

                    foreach (var configRule in configIngress.Rules)
                    {
                        var rule = new IngressRuleBuilder()
                        {
                            Host = configRule.Host,
                            Path = configRule.Path,
                            Service = configRule.Service,
                        };
                        ingress.Rules.Add(rule);
                    }
                }

                foreach (var configDependency in config.Dependencies)
                {
                    // TODO: Validate the extension is yaml
                    var expandedPath = Environment.ExpandEnvironmentVariables(configDependency.Path!);
                    var dependencyFile = new FileInfo(Path.Combine(config.Source.DirectoryName, expandedPath));
                    var dependencyConfig = ConfigFactory.FromFile(dependencyFile);
                    ValidateConfigApplication(dependencyConfig);
                    queue.Enqueue(dependencyConfig);
                }
            }

            return root;
        }

        private static void ValidateConfigApplication(ConfigApplication config)
        {
            var context = new ValidationContext(config);
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(config, context, results, validateAllProperties: true))
            {
                throw new CommandException(
                    "Configuration validation failed." + Environment.NewLine +
                    string.Join(Environment.NewLine, results.Select(r => r.ErrorMessage)));
            }

            foreach (var service in config.Services)
            {
                context = new ValidationContext(service);
                if (!Validator.TryValidateObject(service, context, results, validateAllProperties: true))
                {
                    throw new CommandException(
                        $"Service '{service.Name}' validation failed." + Environment.NewLine +
                        string.Join(Environment.NewLine, results.Select(r => r.ErrorMessage)));
                }

                foreach (var binding in service.Bindings)
                {
                    context = new ValidationContext(binding);
                    if (!Validator.TryValidateObject(binding, context, results, validateAllProperties: true))
                    {
                        throw new CommandException(
                            $"Binding '{binding.Name}' of service '{service.Name}' validation failed." + Environment.NewLine +
                            string.Join(Environment.NewLine, results.Select(r => r.ErrorMessage)));
                    }
                }

                foreach (var envVar in service.Configuration)
                {
                    context = new ValidationContext(service);
                    if (!Validator.TryValidateObject(service, context, results, validateAllProperties: true))
                    {
                        throw new CommandException(
                            $"Environment variable '{envVar.Name}' of service '{service.Name}' validation failed." + Environment.NewLine +
                            string.Join(Environment.NewLine, results.Select(r => r.ErrorMessage)));
                    }
                }

                foreach (var volume in service.Volumes)
                {
                    context = new ValidationContext(service);
                    if (!Validator.TryValidateObject(service, context, results, validateAllProperties: true))
                    {
                        throw new CommandException(
                            $"Volume '{volume.Source}' of service '{service.Name}' validation failed." + Environment.NewLine +
                            string.Join(Environment.NewLine, results.Select(r => r.ErrorMessage)));
                    }
                }
            }

            foreach (var ingress in config.Ingress)
            {
                // We don't currently recurse into ingress rules or ingress bindings right now.
                // There's nothing to validate there.
                context = new ValidationContext(ingress);
                if (!Validator.TryValidateObject(ingress, context, results, validateAllProperties: true))
                {
                    throw new CommandException(
                        $"Ingress '{ingress.Name}' validation failed." + Environment.NewLine +
                        string.Join(Environment.NewLine, results.Select(r => r.ErrorMessage)));
                }
            }

            foreach (var dependency in config.Dependencies)
            {
                // We don't currently recurse into ingress rules or ingress bindings right now.
                // There's nothing to validate there.
                context = new ValidationContext(dependency);
                if (!Validator.TryValidateObject(dependency, context, results, validateAllProperties: true))
                {
                    throw new CommandException(
                        $"Dependency '{dependency.Path}' validation failed." + Environment.NewLine +
                        string.Join(Environment.NewLine, results.Select(r => r.ErrorMessage)));
                }
            }
        }
    }
}
