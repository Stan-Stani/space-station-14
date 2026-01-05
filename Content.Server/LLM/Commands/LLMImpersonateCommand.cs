using Content.Shared.Administration;
using Content.Server.Administration;
using Content.Server.LLM;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.Server.LLM.Commands;

[AdminCommand(AdminFlags.Admin)]
public sealed class LLMImpersonateCommand : IConsoleCommand
{
    public string Command => "llm_impersonate";
    public string Description => "Allows user to send message as an llman";
    public string Help => "llm_impersonate [EntityUid_Or_*] [Message]";
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0)
        {
            shell.WriteLine("Missing arguments. Usage: llm_impersonate [EntityUid_Or_*] [Message]");
            return;
        }

        if (args[0] == "*")
        {
            var llmSystem = _entitySystemManager.GetEntitySystem<LLMPersonalitySystem>();
            var query = _entityManager.EntityQueryEnumerator<LLMPersonalityComponent>();

            var total = 0;
            var triggered = 0;

            while (query.MoveNext(out var myUid, out _))
            {
                total++;
                llmSystem.ProcessLLMDecision(myUid, args.Length > 1 ? argStr.Substring(args[0].Length).Trim() : string.Empty, "");
                triggered++;
            }

            shell.WriteLine($"Triggered LLM decision for {triggered}/{total} LLM-enabled entities.");
            return;
        }

        if (!int.TryParse(args[0], out var id))
        {
            shell.WriteLine("Invalid EntityUid.");
            return;
        }
        else
        {
            var llmSystem = _entitySystemManager.GetEntitySystem<LLMPersonalitySystem>();
            llmSystem.ProcessLLMDecision(new EntityUid(id), args.Length > 1 ? argStr.Substring(args[0].Length).Trim() : string.Empty, "");
            shell.WriteLine($"Triggered LLM decision for 1 LLM-enabled entity: {id}.");

        }

        var uid = new EntityUid(id);
        if (!_entityManager.TryGetComponent<LLMPersonalityComponent>(uid, out var component))
        {
            shell.WriteLine("Entity does not have LLMPersonalityComponent.");
            return;
        }

        shell.WriteLine($"History for {_entityManager.ToPrettyString(uid)}:");
        foreach (var msg in component.History)
        {
            shell.WriteLine($"[{msg.Role.ToUpper()}]: {msg.Content}");
        }
    }
}
