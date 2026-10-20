// SPDX-License-Identifier: EUPL-1.2
using System.Collections;
using System.Runtime.InteropServices;
using Aetheus.Agent.Core.Functions;

// aetheus-fn <function>: runs one pipeline function inside a step container (see AetheusFnCommand).
using var cancellation = new CancellationTokenSource();
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    cancellation.Cancel();
});
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var environment = Environment.GetEnvironmentVariables()
    .Cast<DictionaryEntry>()
    .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? string.Empty, StringComparer.Ordinal);

try
{
    return await AetheusFnCommand.RunAsync(args, environment, Console.Out, Console.Error, cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    await Console.Error.WriteLineAsync("aetheus-fn: cancelled.");
    return 130;
}
