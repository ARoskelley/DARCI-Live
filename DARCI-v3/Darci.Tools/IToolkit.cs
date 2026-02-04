using Darci.Core;
using Darci.Core.Models;

namespace Darci.Tools;

/// <summary>
/// DARCI's toolkit - the capabilities she can use to act in the world.
/// This is an abstraction layer that hides implementation details.
/// </summary>
public interface IToolkit
{
    // === Communication ===
    
    /// <summary>
    /// Send a message to a user (queued for delivery)
    /// </summary>
    Task SendMessage(string userId, string content);
    
    /// <summary>
    /// Generate a reply given context
    /// </summary>
    Task<string> GenerateReply(ReplyContext context);
    
    // === Language/Thinking ===
    
    /// <summary>
    /// Generate text using the LLM
    /// </summary>
    Task<string> Generate(string prompt);
    
    /// <summary>
    /// Classify the intent of a message
    /// </summary>
    Task<MessageIntent> ClassifyIntent(string message);
    
    // === Memory ===
    
    /// <summary>
    /// Store something in memory
    /// </summary>
    Task StoreMemory(string content, string[] tags);
    
    /// <summary>
    /// Recall memories relevant to a query
    /// </summary>
    Task<List<string>> RecallMemories(string query, int limit = 5);
    
    /// <summary>
    /// Run memory consolidation/maintenance
    /// </summary>
    Task ConsolidateMemories();
    
    // === Research ===
    
    /// <summary>
    /// Search the web for information
    /// </summary>
    Task<string> SearchWeb(string query);
    
    // === Files ===
    
    /// <summary>
    /// Read a file's contents
    /// </summary>
    Task<string> ReadFile(string path);
    
    /// <summary>
    /// Write content to a file
    /// </summary>
    Task WriteFile(string path, string content);
    
    // === Goals ===
    
    /// <summary>
    /// Create a new goal
    /// </summary>
    Task<int> CreateGoal(string description, string userId);
    
    /// <summary>
    /// Mark progress on a goal
    /// </summary>
    Task ProgressGoal(int goalId);
}
