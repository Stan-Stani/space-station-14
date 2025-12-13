using Content.Shared.Administration;
using Content.Server.Administration;
using Robust.Shared.Console;
using Robust.Shared.IoC;
using System.Threading;

namespace Content.Server.LLM.Commands;

[AdminCommand(AdminFlags.Admin)]
public sealed class LLMTestCommand : IConsoleCommand
{
    public string Command => "llm_test";
    public string Description => "Tests the LLM service configuration and connectivity.";
    public string Help => "Usage: llm_test [prompt]";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var llm = IoCManager.Resolve<ILLMService>();

        string prompt = "Hello, are you there?";
        if (args.Length > 0)
        {
            prompt = string.Join(" ", args);
        }

        shell.WriteLine($"Sending prompt to LLM: \"{prompt}\"...");

        // Note: Void async method, exceptions might be swallowed if not careful, but shell.WriteLine helps.
        try
        {
            var response = await llm.GenerateResponseAsync("You are a test assistant.", prompt);

            if (string.IsNullOrEmpty(response))
            {
                shell.WriteError("LLM returned empty response or error.");
            }
            else
            {
                shell.WriteLine($"LLM Response: {response}");
            }
        }
        catch (System.Exception e)
        {
            shell.WriteError($"Exception: {e.Message}");
        }
    }
}
