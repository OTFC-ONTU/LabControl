// Throwaway: creates a development lab in a data directory and writes the USB payload's
// trust part, so the Windows VM can be re-provisioned into it. Never used for a real lab.
using System.Net;
using LabControl.Console.Services;
using Microsoft.Extensions.Logging;

var dataDirectory = args[0];
var payloadDirectory = args[1];
var labName = args.Length > 2 ? args[2] : "M6-dev";
var passphrase = Environment.GetEnvironmentVariable("DEVLAB_PASSPHRASE") ?? throw new InvalidOperationException("DEVLAB_PASSPHRASE not set");

using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
var options = new ConsoleOptions { DataDirectory = dataDirectory, BindAddress = IPAddress.Any };
var bootstrap = new ConsoleBootstrap(options, loggers);
var session = bootstrap.CreateLab(labName, "dev", passphrase, Environment.MachineName + " (M6 dev)", out var recovery);
Console.WriteLine($"lab {session.LabName} {session.LabId} created in {dataDirectory}");
Directory.CreateDirectory(payloadDirectory);
var written = session.WritePayload(payloadDirectory, pcCount: 2);
Console.WriteLine($"payload: {written}");
Console.WriteLine("recovery code: (not printed)");
_ = recovery;
