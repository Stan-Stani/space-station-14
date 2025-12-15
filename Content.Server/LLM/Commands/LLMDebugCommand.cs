using Content.Shared.Administration;
using Content.Server.Administration;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using System.Linq;

namespace Content.Server.LLM.Commands;

[AdminCommand(AdminFlags.Admin)]
public sealed class LLMDebugCommand : IConsoleCommand
{
    public string Command => "llm_debug";
    public string Description => "Lists LLM-enabled NPCs and their conversation history.";
    public string Help => "llm_debug [EntityUid]";

    [Dependency] private readonly IEntityManager _entityManager = default!;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0)
        {
            shell.WriteLine("Listing LLM-enabled entities:");
            var query = _entityManager.EntityQueryEnumerator<LLMPersonalityComponent>();
            while (query.MoveNext(out var uid, out var component))
            {
                var name = _entityManager.ToPrettyString(uid);
                shell.WriteLine($"- {name} (UID: {uid}): {component.History.Count} messages in history.");
            }
            shell.WriteLine("Use 'llm_debug <UID>' to view detailed history.");
        }
        else
        {
            if (!int.TryParse(args[0], out var id))
            {
                shell.WriteLine("Invalid EntityUid.");
                return;
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
}
