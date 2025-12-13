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
using Azure.AI.OpenAI; // Added for AzureOpenAIClient

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

    public async Task<string> GenerateResponseAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
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
            ChatClient client;

            // Basic detection for Azure OpenAI
            if (apiUrl.Contains("azure.com", StringComparison.OrdinalIgnoreCase))
            {
                // Parse the base URI for Azure (scheme + host).
                // AzureOpenAIClient expects "https://myresource.openai.azure.com/"
                // User might provide "https://myresource.openai.azure.com/openai/v1/..."
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
                client = azureClient.GetChatClient(model);
            }
            else
            {
                // Standard OpenAI / Ollama

                // If using a custom endpoint (like Ollama), passing the full URI including /v1 might be safer if the client respects it,
                // but usually ChatClient expects the *Endpoint* property in options?
                // Actually, for standard OpenAI, new ChatClient(model, key, options) defaults to OpenAI public API.
                // We need to set the endpoint if it's not OpenAI. public.

                var keyToUse = string.IsNullOrEmpty(apiKey) ? "dummy-key" : apiKey;

                OpenAIClientOptions clientOptions = new OpenAIClientOptions
                {
                    Endpoint = new Uri(apiUrl)
                };

                client = new ChatClient(model, new ApiKeyCredential(keyToUse), clientOptions);
            }

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt)
            };

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

    // BearerTokenPolicy removed as it is no longer needed with AzureOpenAIClient
}
