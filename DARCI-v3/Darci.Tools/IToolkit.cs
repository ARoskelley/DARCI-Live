using Darci.Shared;
using Darci.Tools.Cad;

namespace Darci.Tools;

/// <summary>
/// DARCI's toolkit - the capabilities she can use to act in the world.
/// This is an abstraction layer that hides implementation details.
/// </summary>
public interface IToolkit
{
    // === Communication ===
    Task SendMessage(string userId, string content, bool externalNotify = false);
    Task<string> GenerateReply(ReplyContext context);
    
    // === Language/Thinking ===
    Task<string> Generate(string prompt);
    Task<MessageIntent> ClassifyIntent(string message);
    
    // === Memory ===
    Task StoreMemory(string content, string[] tags);
    Task<List<string>> RecallMemories(string query, int limit = 5);
    Task ConsolidateMemories();
    
    // === Research ===
    Task<string> SearchWeb(string query);
    
    // === Files ===
    Task<string> ReadFile(string path);
    Task WriteFile(string path, string content);
    
    // === Goals ===
    Task<int> CreateGoal(string description, string userId);
    Task ProgressGoal(int goalId);
    
    // === CAD Generation ===
    
    /// <summary>
    /// Full feedback loop: generate script → execute → validate → self-critique → fix → repeat
    /// </summary>
    Task<CadPipelineResult> GenerateCAD(
        string description,
        CadDimensionSpec? dimensions = null,
        int maxIterations = 5);
    
    /// <summary>
    /// Single-shot: execute a known script without the feedback loop
    /// </summary>
    Task<CadGenerateResponse?> ExecuteCADScript(
        string script,
        CadDimensionSpec? dimensions = null,
        string filename = "output.stl");
    
    /// <summary>
    /// Check if the Python CAD engine is reachable
    /// </summary>
    Task<bool> IsCADEngineHealthy();
}
