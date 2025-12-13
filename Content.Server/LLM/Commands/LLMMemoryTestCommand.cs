using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.IoC;
using OpenAI.Chat;
using System.Collections.Generic;
using Content.Server.LLM;

namespace Content.Server.LLM.Commands;

[AdminCommand(AdminFlags.Admin)]
public sealed class LLMMemoryTestCommand : IConsoleCommand
{
    public string Command => "llm_memory_test";
    public string Description => "Tests LLM conversation memory.";
    public string Help => "llm_memory_test";

    [Dependency] private readonly ILLMService _llmService = default!;

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        shell.WriteLine("Testing LLM Memory...");

        var history = new List<ChatMessage>
        {
            new SystemChatMessage("You are a helpful assistant."),
            new UserChatMessage("My name is TestUser."),
            new AssistantChatMessage("Hello TestUser! How can I help you today?"),
            new UserChatMessage("What is my name?")
        };

        try
        {
            var response = await _llmService.GenerateResponseAsync(history);
            shell.WriteLine($"LLM Response: {response}");

            if (response.Contains("TestUser"))
                shell.WriteLine("SUCCESS: Memory verified.");
            else
                shell.WriteLine("WARNING: Memory check uncertain (LLM might have ignored context).");
        }
        catch (System.Exception e)
        {
            shell.WriteLine($"ERROR: {e.Message}");
        }
    }
}
