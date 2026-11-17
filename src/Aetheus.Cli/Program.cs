// SPDX-License-Identifier: EUPL-1.2
using System.CommandLine;
using System.Globalization;
using System.Net.Http.Json;
using Aetheus.Cli;

Option<string> serverOption = new("--server")
{
    Description = "Aetheus API server URL (absolute HTTP or HTTPS URL without embedded credentials).",
    DefaultValueFactory = _ => LocalDevelopmentEndpoints.CliApiBaseUrl,
    Recursive = true
};

Option<bool> tokenStdinOption = new("--token-stdin")
{
    Description = "Read the JWT authentication token from standard input. Prefer AETHEUS_TOKEN for automation.",
    Recursive = true
};

Option<int> pageOption = new("--page")
{
    Description = $"Page number (1 to {CliInvocation.MaximumPage})",
    DefaultValueFactory = _ => 1,
    Recursive = true
};

Option<int> pageSizeOption = new("--page-size")
{
    Description = $"Items per page (1 to {PaginationRequest.MaxPageSize})",
    DefaultValueFactory = _ => PaginationRequest.DefaultPageSize,
    Recursive = true
};

RootCommand rootCommand = new("Aetheus CLI - manage servers, pipelines, and deployments");
rootCommand.Options.Add(serverOption);
rootCommand.Options.Add(tokenStdinOption);
rootCommand.Options.Add(pageOption);
rootCommand.Options.Add(pageSizeOption);

async Task<CliInvocation> ResolveInvocationAsync(ParseResult result, CancellationToken ct) =>
    await CliInvocation.CreateAsync(
        result.GetValue(serverOption)!,
        result.GetValue(tokenStdinOption),
        result.GetValue(pageOption),
        result.GetValue(pageSizeOption),
        ct);

Command serverListCommand = new("server-list", "List all servers");
rootCommand.Subcommands.Add(serverListCommand);
serverListCommand.SetAction((parseResult, cancellationToken) => CliRuntime.ExecuteAsync(async ct =>
{
    var invocation = await ResolveInvocationAsync(parseResult, ct);
    using var client = invocation.CreateClient();
    var result = await client.GetFromJsonAsync<PaginatedResult<ServerDto>>(
        $"api/servers?page={invocation.Page}&pageSize={invocation.PageSize}", ct);
    var servers = result?.Items;
    if (servers is null || servers.Count == 0)
    {
        Console.WriteLine("No servers found.");
        return 0;
    }

    Console.WriteLine($"{"Id",-5} {"Name",-25} {"Hostname",-30} {"Status",-12}");
    Console.WriteLine(new string('-', 72));
    foreach (var server in servers)
        Console.WriteLine($"{server.Id,-5} {server.Name,-25} {server.Hostname,-30} {server.Status,-12}");
    return 0;
}, cancellationToken));

Command pipelineListCommand = new("pipeline-list", "List all pipelines");
rootCommand.Subcommands.Add(pipelineListCommand);
pipelineListCommand.SetAction((parseResult, cancellationToken) => CliRuntime.ExecuteAsync(async ct =>
{
    var invocation = await ResolveInvocationAsync(parseResult, ct);
    using var client = invocation.CreateClient();
    var result = await client.GetFromJsonAsync<PaginatedResult<PipelineDto>>(
        $"api/pipelines?page={invocation.Page}&pageSize={invocation.PageSize}", ct);
    if (result is null || result.Items.Count == 0)
    {
        Console.WriteLine("No pipelines found.");
        return 0;
    }

    Console.WriteLine($"{"Id",-5} {"Name",-30} {"Project",-20} {"LastRun",-33}");
    Console.WriteLine(new string('-', 92));
    foreach (var pipeline in result.Items)
    {
        var lastRun = pipeline.LastRunAt?.ToString("O", CultureInfo.InvariantCulture) ?? "Never";
        Console.WriteLine($"{pipeline.Id,-5} {pipeline.Name,-30} {pipeline.ProjectName,-20} {lastRun,-33}");
    }
    return 0;
}, cancellationToken));

Argument<int> pipelineIdArg = new("pipeline-id") { Description = "ID of the pipeline to run" };
Command pipelineRunCommand = new("pipeline-run", "Trigger a pipeline run");
pipelineRunCommand.Arguments.Add(pipelineIdArg);
rootCommand.Subcommands.Add(pipelineRunCommand);
pipelineRunCommand.SetAction((parseResult, cancellationToken) => CliRuntime.ExecuteAsync(async ct =>
{
    var invocation = await ResolveInvocationAsync(parseResult, ct);
    using var client = invocation.CreateClient();
    using var response = await client.PostAsync(
        $"api/pipelines/{parseResult.GetValue(pipelineIdArg)}/run", null, ct);
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"Error: {response.StatusCode} - {await response.Content.ReadAsStringAsync(ct)}");
        return 1;
    }

    var run = await response.Content.ReadFromJsonAsync<PipelineRunDto>(cancellationToken: ct);
    Console.WriteLine($"Pipeline run started: #{run?.BuildNumber} - Status: {run?.Status}");
    return 0;
}, cancellationToken));

Command taskListCommand = new("task-list", "List recent tasks");
rootCommand.Subcommands.Add(taskListCommand);
taskListCommand.SetAction((parseResult, cancellationToken) => CliRuntime.ExecuteAsync(async ct =>
{
    var invocation = await ResolveInvocationAsync(parseResult, ct);
    using var client = invocation.CreateClient();
    var result = await client.GetFromJsonAsync<PaginatedResult<ServerTaskDto>>(
        $"api/tasks?page={invocation.Page}&pageSize={invocation.PageSize}", ct);
    if (result is null || result.Items.Count == 0)
    {
        Console.WriteLine("No tasks found.");
        return 0;
    }

    Console.WriteLine($"{"Id",-5} {"Name",-35} {"Status",-15} {"Server",-20}");
    Console.WriteLine(new string('-', 75));
    foreach (var task in result.Items)
        Console.WriteLine($"{task.Id,-5} {CliRuntime.Truncate(task.Name, 35),-35} {task.Status,-15} {task.ServerName,-20}");
    return 0;
}, cancellationToken));

Command healthCommand = new("health", "Check anonymous API readiness");
rootCommand.Subcommands.Add(healthCommand);
healthCommand.SetAction((parseResult, cancellationToken) => CliRuntime.ExecuteAsync(async ct =>
{
    var server = CliRuntime.ParseServerUri(parseResult.GetValue(serverOption)!);
    using var client = new AetheusApiClient(server, token: null);
    using var response = await client.GetAsync("health/ready", ct);
    var body = await response.Content.ReadAsStringAsync(ct);
    Console.WriteLine($"Health: {response.StatusCode} - {body}");
    return response.IsSuccessStatusCode ? 0 : 1;
}, cancellationToken));

return await rootCommand.Parse(args).InvokeAsync();
