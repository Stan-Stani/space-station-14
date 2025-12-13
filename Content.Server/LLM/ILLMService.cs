using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.LLM;

public interface ILLMService
{
    /// <summary>
    /// Generates a response from the LLM provider based on the given prompt.
    /// </summary>
    /// <param name="systemPrompt">The system prompt to define behavior.</param>
    /// <param name="userPrompt">The user's input/query.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The generated text response.</returns>
    Task<string> GenerateResponseAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}
