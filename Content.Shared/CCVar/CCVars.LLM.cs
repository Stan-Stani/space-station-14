using Robust.Shared.Configuration;


namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    public static readonly CVarDef<string> LLMApiUrl =
        CVarDef.Create("llm.api_url", "http://localhost:11434/v1", CVar.SERVERONLY);

    public static readonly CVarDef<string> LLMApiKey =
        CVarDef.Create("llm.api_key", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);

    public static readonly CVarDef<string> LLMModel =
        CVarDef.Create("llm.model", "llama3", CVar.SERVERONLY);

    public static readonly CVarDef<string> LLMSystemPrompt =
        CVarDef.Create("llm.system_prompt", "You are an NPC in Space Station 14.", CVar.SERVERONLY);
}
