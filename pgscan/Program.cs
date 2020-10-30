using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Inedo.DependencyScan
{
    public static class Program
    {
        private static XNamespace NuSpec = "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd";

        public static int Main(string[] args)
        {
            try
            {
                var uniquePackages = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (args.Length < 1)
                {
                    Usage();
                    return 1;
                }

                var argList = new ArgList(args);
                if (string.IsNullOrWhiteSpace(argList.Command))
                    throw new PgScanException("Command is not specified.", true);

                var inputFiles = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);

                var inputName = argList.GetRequiredNamed("input")?.Replace('/', '\\');
                if (inputName.Contains('*'))
                {
                    var folder = "";
                    var fileSpec = "";
                    SearchOption opt = SearchOption.AllDirectories;

                    // Need to find matching files in folder
                    if (inputName.Contains("**"))
                    {
                        var parts = inputName.Split("**");
                        folder = parts[0].TrimEnd('\\');
                        fileSpec = parts[parts.Length - 1].Trim('\\');

                        if (string.IsNullOrWhiteSpace(fileSpec))
                        {
                            fileSpec = "*.??proj";
                        }
                    }
                    else
                    {
                        var parts = inputName.Split("*");
                        folder = parts[0].TrimEnd('\\');
                        fileSpec = parts[parts.Length - 1].Trim('\\');
                        if (string.IsNullOrWhiteSpace(fileSpec))
                        {
                            fileSpec = "*.??proj";
                        }
                        else
                        {
                            fileSpec = $"*{fileSpec}";
                        }
                        opt = SearchOption.TopDirectoryOnly;

                    }

                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        folder = Environment.CurrentDirectory;
                    }

                    Console.WriteLine($"folder:   '{folder}'");
                    Console.WriteLine($"fileSpec: '{fileSpec}'");

                    var possibleMatches = Directory.GetFiles(folder, fileSpec, opt);
                    foreach (var possible in possibleMatches)
                    {
                        inputFiles.Add(possible);
                    }
                }
                else
                {
                    inputFiles.Add(inputName);
                }

                var position = argList.GetNamedPosition("input");

                foreach (var inputFile in inputFiles)
                {
                    args[position] = $"--input=\"{inputFile}\"";

                    argList = new ArgList(args);

                    switch (argList.Command.ToLowerInvariant())
                    {
                        case "report":
                            Report(argList, uniquePackages);
                            break;

                        case "publish":
                            Publish(argList, uniquePackages);
                            break;

                        default:
                            throw new PgScanException($"Invalid command: {argList.Command}", true);
                    }
                }


                if (uniquePackages.Any() && argList.Named.TryGetValue("application-nuspec", out var nuspecFile))
                {
                    Console.WriteLine("Adding Dependencies to NuSpec...");
                    var xd = XDocument.Load(nuspecFile);

                    var xeMetadata = xd.Root.Element(NuSpec + "metadata");
                    var xe = xeMetadata.Element(NuSpec + "dependencies");
                    if (xe == null)
                    {
                        xe = new XElement(NuSpec + "dependencies");
                        xeMetadata.Add(xe);
                    }

                    foreach (var kvp in uniquePackages)
                    {
                        xe.Add(new XElement(NuSpec + "dependency", new XAttribute("id", kvp.Key), new XAttribute("version", kvp.Value)));
                    }


                    xe = xeMetadata.Element(NuSpec + "repository");
                    if (xe == null)
                    {
                        xe = new XElement(NuSpec + "repository",
                            new XAttribute("type", "git"),
                            new XAttribute("url", Environment.GetEnvironmentVariable("BUILD_REPOSITORY_URI")),
                            new XAttribute("commit", Environment.GetEnvironmentVariable("BUILD_SOURCEVERSION")));
                        xeMetadata.Add(xe);
                    }
                    else
                    {
                        xe.SetAttributeValue("url", Environment.GetEnvironmentVariable("BUILD_REPOSITORY_URI"));
                        xe.SetAttributeValue("commit", Environment.GetEnvironmentVariable("BUILD_SOURCEVERSION"));
                    }

                    xd.Save(nuspecFile);

                    Console.WriteLine($"Dependencies added to NuSpec!  {uniquePackages.Count} represented.");
                }

            }
            catch (PgScanException ex)
            {
                Console.Error.WriteLine(ex.Message);

                if (ex.WriteUsage)
                    Usage();

                return ex.ExitCode;
            }

            return 0;
        }

        private static void Report(ArgList args, SortedDictionary<string, string> uniquePackages)
        {
            if (!args.Named.TryGetValue("input", out var inputFileName))
                throw new PgScanException("Missing required argument --input=<input file name>");

            Console.WriteLine($"Reporting {inputFileName}...");

            args.Named.TryGetValue("type", out var typeName);
            typeName = typeName ?? GetImplicitTypeName(inputFileName);
            if (string.IsNullOrWhiteSpace(typeName))
                throw new PgScanException("Missing --type argument and could not infer type based on input file name.");

            var scanner = DependencyScanner.GetScanner(typeName);
            scanner.SourcePath = inputFileName;
            var projects = scanner.ResolveDependencies();
            if (projects.Count > 0)
            {
                foreach (var p in projects)
                {
                    Console.WriteLine(p.Name ?? "(project)");
                    foreach (var d in p.Dependencies)
                        Console.WriteLine($"  => {d.Name} {d.Version}");

                    Console.WriteLine();
                }
            }
            else
            {
                Console.WriteLine("No projects found.");
            }
        }

        private static void Publish(ArgList args, SortedDictionary<string, string> uniquePackages)
        {
            var inputFileName = args.GetRequiredNamed("input");

            Console.WriteLine($"Publishing {inputFileName}...");

            args.Named.TryGetValue("type", out var typeName);
            typeName = typeName ?? GetImplicitTypeName(inputFileName);
            if (string.IsNullOrWhiteSpace(typeName))
                throw new PgScanException("Missing --type argument and could not infer type based on input file name.");

            var packageFeed = args.GetRequiredNamed("package-feed");
            var progetUrl = args.GetRequiredNamed("proget-url");
            var consumerSource = args.GetRequiredNamed("consumer-package-source");
            var consumerVersion = args.GetRequiredNamed("consumer-package-version");

            args.Named.TryGetValue("consumer-package-name", out var consumerName);
            args.Named.TryGetValue("consumer-package-group", out var consumerGroup);
            args.Named.TryGetValue("api-key", out var apiKey);

            string consumerFeed = null;
            string consumerUrl = null;

            if (consumerSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || consumerSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                consumerUrl = consumerSource;
            else
                consumerFeed = consumerSource;

            var client = new ProGetClient(progetUrl);

            var scanner = DependencyScanner.GetScanner(typeName);
            scanner.SourcePath = inputFileName;
            var projects = scanner.ResolveDependencies();
            foreach (var project in projects)
            {
                foreach (var package in project.Dependencies)
                {
                    Console.WriteLine($"Publishing consumer data for {package}...");

                    var name = consumerName ?? project.Name;

                    client.RecordPackageDependency(
                        package,
                        packageFeed,
                        new PackageConsumer
                        {
                            Name = name,
                            Version = consumerVersion,
                            Group = consumerGroup,
                            Feed = consumerFeed,
                            Url = consumerUrl
                        },
                        apiKey
                    );

                    if(uniquePackages.ContainsKey(package.Name))
                    {
                        if(Version.Parse(uniquePackages[package.Name]) < Version.Parse(package.Version))
                        {
                            uniquePackages[package.Name] = package.Version;
                        }
                    }
                    else
                    {
                        uniquePackages.Add(package.Name, package.Version);
                    }
                }
            }

            Console.WriteLine("Dependencies published!");
        }

        private static string GetImplicitTypeName(string fileName)
        {
            switch (Path.GetExtension(fileName).ToLowerInvariant())
            {
                case ".sln":
                case ".csproj":
                case ".vbproj":
                    return "nuget";

                case ".json":
                    return "npm";

                default:
                    return Path.GetFileName(fileName).Equals("requirements.txt", StringComparison.OrdinalIgnoreCase) ? "pypi" : null;
            }
        }

        private static void Usage()
        {
            Console.WriteLine($"pgscan v{typeof(Program).Assembly.GetName().Version}");
            Console.WriteLine("Usage: pgscan <command> [options...]");
            Console.WriteLine("Options:");
            Console.WriteLine("  --type=<nuget|npm|pypi>");
            Console.WriteLine("  --input=<source file name>");
            Console.WriteLine("  --package-feed=<ProGet feed name>");
            Console.WriteLine("  --proget-url=<ProGet base URL>");
            Console.WriteLine("  --consumer-package-source=<feed name or URL>");
            Console.WriteLine("  --consumer-package-name=<name>");
            Console.WriteLine("  --consumer-package-version=<version>");
            Console.WriteLine("  --consumer-package-group=<group>");
            Console.WriteLine("  --api-key=<ProGet API key>");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  report\tDisplay dependency data");
            Console.WriteLine("  publish\tPublish dependency data to ProGet");
            Console.WriteLine();
        }
    }
}
