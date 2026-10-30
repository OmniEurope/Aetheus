// SPDX-License-Identifier: EUPL-1.2
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

// Pre-commit guard (recette R-49): every pipeline definition under the given directory must be readable YAML,
// with the same parser and version as the backend. On 2026-10-25 a plain scalar holding " : " made
// aetheus-deploy-prod unreadable in production: the Run button and the candidate's chain both stopped.
// This checks YAML syntax only, not what a pipeline declares.
var directory = args.Length > 0 ? args[0] : ".pipeline";
if (!Directory.Exists(directory))
{
    Console.WriteLine($"No {directory} directory: nothing to check.");
    return 0;
}

var failures = 0;
var files = Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories)
    .Concat(Directory.EnumerateFiles(directory, "*.yml", SearchOption.AllDirectories))
    .Order(StringComparer.Ordinal);
foreach (var file in files)
{
    try
    {
        using var reader = new StreamReader(file);
        new YamlStream().Load(reader);
    }
    catch (YamlException exception)
    {
        failures++;
        Console.Error.WriteLine($"{file}:{exception.Start.Line}:{exception.Start.Column}: {exception.Message}");
    }
}

if (failures > 0)
{
    Console.Error.WriteLine($"{failures} pipeline file(s) are not valid YAML. Quote a value that holds \": \" or \" #\".");
    return 1;
}
return 0;
