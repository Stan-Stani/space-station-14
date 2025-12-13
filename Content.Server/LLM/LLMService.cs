using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Generic;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Server.LLM;

public sealed class LLMService : ILLMService, IPostInjectInit
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ILogManager _logManager = default!;

    private ISawmill _sawmill = default!;
    private readonly HttpClient _httpClient = new();

    public void PostInject()
    {
        _sawmill = _logManager.GetSawmill("llm");
    }

    private struct OpenAIRequest
    {
        public string model { get; set; }
        public bool stream { get; set; }
        public List<OpenAIMessage> messages { get; set; }
    }

    private struct OpenAIMessage
    {
        public string role { get; set; }
        public string content { get; set; }
    }

    private struct OpenAIResponse
    {
        public List<OpenAIChoice> choices { get; set; }
    }

    private struct OpenAIChoice
    {
        public OpenAIMessage message { get; set; }
    }

    private struct OllamaResponse
    {
        public string response { get; set; }
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

        var requestPayload = new OpenAIRequest
        {
            model = model,
            stream = false,
            messages = new List<OpenAIMessage>
            {
                new() { role = "system", content = systemPrompt },
                new() { role = "user", content = userPrompt }
            }
        };

        try
        {
            var jsonContent = JsonSerializer.Serialize(requestPayload);
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseString = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _sawmill.Error($"LLM API returned error: {response.StatusCode} - {responseString}");
                return string.Empty;
            }

            // Try parsing as OpenAI first
            try
            {
                var openAiResponse = JsonSerializer.Deserialize<OpenAIResponse>(responseString);
                if (openAiResponse.choices != null && openAiResponse.choices.Count > 0)
                {
                    return openAiResponse.choices[0].message.content;
                }
            }
            catch {}

            // Try parsing as Ollama
            try
            {
                var ollamaResponse = JsonSerializer.Deserialize<OllamaResponse>(responseString);
                if (!string.IsNullOrEmpty(ollamaResponse.response))
                {
                    return ollamaResponse.response;
                }
            }
            catch {}

            return responseString;
        }
        catch (Exception e)
        {
            _sawmill.Error($"Exception during LLM request: {e.Message}");
            return string.Empty;
        }
    }
}
