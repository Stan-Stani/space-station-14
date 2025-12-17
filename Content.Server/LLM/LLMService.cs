using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Azure.Core; // For TokenCredential
using Azure.Identity;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using System.Linq; // Added for AzureOpenAIClient

namespace Content.Server.LLM;

public sealed class LLMService : ILLMService, IPostInjectInit
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ILogManager _logManager = default!;

    private ISawmill _sawmill = default!;

    public void PostInject()
    {
        _sawmill = _logManager.GetSawmill("llm");
    }

    public Task<string> GenerateResponseAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };
        return GenerateResponseAsync(messages, cancellationToken);
    }

    public async Task<string> GenerateResponseAsync(List<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var apiUrl = _cfg.GetCVar(CCVars.LLMApiUrl);
        var apiKey = _cfg.GetCVar(CCVars.LLMApiKey);
        var model = _cfg.GetCVar(CCVars.LLMModel);

        if (string.IsNullOrEmpty(apiUrl))
        {
            _sawmill.Error("LLM API URL is not configured.");
            return string.Empty;
        }

        try
        {
            var client = CreateClient(apiUrl, apiKey, model);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("sending to llm:");
            foreach (var m in messages)
            {
                string role = m switch
                {
                    SystemChatMessage => "system",
                    UserChatMessage => "user",
                    AssistantChatMessage => "assistant",
                    ToolChatMessage => "tool",
                    _ => "unknown"
                };

                var text = string.Join("", m.Content.Select(c => c.Text));
                sb.AppendLine($"[{role}]: {text}");
            }
            _sawmill.Debug(sb.ToString());
            ChatCompletion completion = await client.CompleteChatAsync(messages, cancellationToken: cancellationToken);

            if (completion.Content != null && completion.Content.Count > 0)
            {
                return completion.Content[0].Text;
            }

            return string.Empty;
        }
        catch (Exception e)
        {
            _sawmill.Error($"Exception during LLM request: {e.Message}");
            return string.Empty;
        }
    }

    private ChatClient CreateClient(string apiUrl, string apiKey, string model)
    {
        // Basic detection for Azure OpenAI
        if (apiUrl.Contains("azure.com", StringComparison.OrdinalIgnoreCase))
        {
            // Parse the base URI for Azure (scheme + host).
            var uri = new Uri(apiUrl);
            var baseUri = new Uri($"{uri.Scheme}://{uri.Host}");

            AzureOpenAIClient azureClient;

            if (!string.IsNullOrEmpty(apiKey) && apiKey != "dummy")
            {
                // Use API Key if provided and valid
                azureClient = new AzureOpenAIClient(baseUri, new ApiKeyCredential(apiKey));
            }
            else
            {
                // Fallback to Identity
                azureClient = new AzureOpenAIClient(baseUri, new DefaultAzureCredential());
            }

            // In Azure, 'model' CVar should correspond to the Deployment Name.
            return azureClient.GetChatClient(model);
        }
        else
        {
            // Standard OpenAI / Ollama
            var keyToUse = string.IsNullOrEmpty(apiKey) ? "dummy-key" : apiKey;

            OpenAIClientOptions clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(apiUrl)
            };

            return new ChatClient(model, new ApiKeyCredential(keyToUse), clientOptions);
        }
    }

    // BearerTokenPolicy removed as it is no longer needed with AzureOpenAIClient
}
